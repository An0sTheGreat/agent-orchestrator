#!/usr/bin/env python3
"""Bounded local-LLM planner/implementer/reviewer orchestrator.

Uses only the Python standard library and an OpenAI-compatible chat API.
"""

from __future__ import annotations

import argparse
import datetime as dt
import difflib
import fnmatch
import hashlib
import http.server
import json
import os
import re
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote, unquote, urlsplit
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parent
WORKFLOW_FILES = {"PROJECT.md", "STATE.md", "TASK.md", "RESULT.md", "REVIEW.md", "ISSUES.md", "config.json"}  # required to run
CHECKPOINT_FILE = ".ao_checkpoint.json"
HANDOFF_FILE = "HANDOFF.md"
COMPLETION_PHASES = ("passed", "blocked")
CHECKPOINT_PHASES = ("implementing", "reviewing", "extra_reviewing") + COMPLETION_PHASES  # passed/blocked: recording the outcome
_ATOMIC_FILES = WORKFLOW_FILES | {HANDOFF_FILE, CHECKPOINT_FILE}  # everything write_atomic can write
PROTECTED_FILES = _ATOMIC_FILES | {name + ".tmp" for name in _ATOMIC_FILES}  # models may not edit


class WorkflowError(RuntimeError):
    pass


def load_text(path: Path) -> str:
    return path.read_text(encoding="utf-8") if path.exists() else ""


def save_text(path: Path, text: str) -> None:
    path.write_text(text.rstrip() + "\n", encoding="utf-8")


class IncompleteReply(WorkflowError):
    """The reply holds no whole JSON object (empty, prose only, or cut off inside one): re-ask, never repair."""


def _scan_object(text: str, start: int) -> tuple[int, str] | None:
    """Walk the object opening at text[start] string-aware. Returns (end, lossless-fixed text), or None if it
    never closes. Fixes apply outside strings only: comments and trailing commas dropped, smart quotes used as
    JSON quotes made ASCII; inside strings raw newlines/tabs are escaped. Nothing is ever added or closed."""
    out: list[str] = []
    depth, quote, i = 0, "", start
    while i < len(text):
        ch = text[i]
        if quote:
            if ch == "\\":
                out.append(text[i:i + 2])
                i += 2
                continue
            if ch == quote or (quote != '"' and ch in '"“”'):
                quote, ch = "", '"'
            out.append({"\n": "\\n", "\r": "\\r", "\t": "\\t"}.get(ch, ch))
        elif ch in '"“”':
            quote = ch if ch == '"' else "”"
            out.append('"')
        elif text.startswith("//", i):
            i = len(text) if text.find("\n", i) < 0 else text.find("\n", i)
            continue
        elif text.startswith("/*", i):
            if text.find("*/", i + 2) < 0:
                return None
            i = text.find("*/", i + 2) + 2
            continue
        else:
            if ch in "}]":
                while out and out[-1].isspace():
                    out.pop()
                if out and out[-1] == ",":
                    out.pop()
            out.append(ch)
            depth += (ch in "{[") - (ch in "}]")
            if depth == 0:
                return i + 1, "".join(out)
        i += 1
    return None


def extract_json(text: str) -> dict[str, Any]:
    """The reply's JSON object, read without guessing. Think blocks and prose around it (fences included) are
    ignored; every balanced top-level {...} is tried as-is, then with lossless syntax fixes (see _scan_object).
    Of several, the LAST that parses as an object wins: models draft, then give the final answer. A reply that
    ends inside an unclosed object raises IncompleteReply even after a complete draft, since the cut-off final
    answer may differ; missing keys are never filled and truncated JSON is never closed."""
    text = answer_text({"content": text})
    found: list[dict[str, Any]] = []
    error = ""
    i = text.find("{")
    while i >= 0:
        scanned = _scan_object(text, i)
        if scanned is None:
            raise IncompleteReply("Model did not return valid JSON: the reply ends inside an unfinished JSON object")
        end, fixed = scanned
        for candidate in (text[i:end], fixed):
            try:
                found.append(json.loads(candidate))  # starts with "{", so a dict
                break
            except json.JSONDecodeError as exc:
                error = str(exc)
        i = text.find("{", end)
    if found:
        return found[-1]
    if not error:
        raise IncompleteReply("Model did not return valid JSON: no JSON object in the reply")
    raise WorkflowError(f"Model did not return valid JSON: {error}")


def safe_relative_path(raw: str, project_root: Path) -> Path:
    candidate = Path(raw)
    if candidate.is_absolute():
        raise WorkflowError(f"Absolute path is not allowed: {raw}")
    resolved = (project_root / candidate).resolve()
    try:
        resolved.relative_to(project_root.resolve())
    except ValueError as exc:
        raise WorkflowError(f"Path leaves project root: {raw}") from exc
    return resolved


def path_is_allowed(raw: str, allowed: list[str], project_root: Path) -> Path:
    path = safe_relative_path(raw, project_root)
    normalized = path.relative_to(project_root.resolve()).as_posix()
    if normalized in PROTECTED_FILES or not any(fnmatch.fnmatch(normalized, pattern) for pattern in allowed):
        raise WorkflowError(f"Path is outside this task's file scope: {raw}")
    return path


EVENTS = False  # set by --events: structured "@@AO {json}" lines for the launcher UI


def emit(event: str, **data: Any) -> None:
    if EVENTS:
        print("@@AO " + json.dumps({"e": event, **data}, ensure_ascii=False), flush=True)


def split_reasoning(message: dict[str, Any]) -> tuple[str, str]:
    """Return (thinking, answer) from a chat message; handles reasoning_content and inline <think> blocks."""
    content = str(message.get("content") or "")
    thinking = str(message.get("reasoning_content") or "")
    match = re.search(r"<think>(.*?)</think>", content, re.S)
    if match:
        thinking = "\n".join(part for part in (thinking, match.group(1)) if part).strip()
        content = (content[:match.start()] + content[match.end():]).strip()
    return thinking, content


def has_output(message: dict[str, Any]) -> bool:
    """True if the reply carries tool calls or any answer text outside reasoning (an unterminated <think> is reasoning)."""
    if message.get("tool_calls"):
        return True
    return bool(answer_text(message))


class ThinkSplitter:
    """Splits streamed content into (kind, text) pieces, honouring inline <think>...</think> blocks."""

    def __init__(self) -> None:
        self.buf, self.thinking = "", False

    def feed(self, text: str) -> list[tuple[str, str]]:
        self.buf += text
        out: list[tuple[str, str]] = []
        while True:
            tag = "</think>" if self.thinking else "<think>"
            kind = "thinking" if self.thinking else "text"
            at = self.buf.find(tag)
            if at >= 0:
                if at:
                    out.append((kind, self.buf[:at]))
                self.buf, self.thinking = self.buf[at + len(tag):], not self.thinking
                continue
            # hold back a trailing fragment that could still become the tag
            keep = next((k for k in range(min(len(tag) - 1, len(self.buf)), 0, -1) if tag.startswith(self.buf[-k:])), 0)
            if len(self.buf) > keep:
                out.append((kind, self.buf[:len(self.buf) - keep]))
            self.buf = self.buf[len(self.buf) - keep:]
            return out

    def flush(self) -> list[tuple[str, str]]:
        rest, self.buf = self.buf, ""
        return [("thinking" if self.thinking else "text", rest)] if rest else []


class ApiClient:
    def __init__(self, settings: dict[str, Any]):
        self.base_url = settings["base_url"].rstrip("/")
        key_env = settings.get("api_key_env", "")
        self.api_key = os.environ.get(key_env, "") if key_env else settings.get("api_key", "")
        if not self.api_key:
            source = key_env or "api_key"
            raise WorkflowError(f"Missing Unsloth API key: set {source}")
        self.expected = settings.get("expected_model_contains", "").lower()
        self.timeout = int(settings.get("timeout_seconds", 600))
        self.management_url = settings.get("management_url", "").rstrip("/")
        self.auto_load = settings.get("auto_load")
        self.role = ""
        self.title = ""  # human-readable name for log lines (a custom worker's title); events keep `role`
        self.thinking_off = False  # set once a thinking-truncation fallback worked; later requests skip the wasted attempt
        self.cut_off = False  # the last reply stopped at max_tokens

    def _request_at(self, base_url: str, method: str, route: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
        data = json.dumps(body).encode("utf-8") if body is not None else None
        request = Request(
            f"{base_url}{route}",
            data=data,
            method=method,
            headers={"Authorization": f"Bearer {self.api_key}", "Content-Type": "application/json"},
        )
        try:
            with urlopen(request, timeout=self.timeout) as response:
                return json.loads(response.read().decode("utf-8"))
        except HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            raise WorkflowError(f"API {exc.code}: {detail}") from exc
        except (URLError, TimeoutError) as exc:
            raise WorkflowError(f"Cannot reach {base_url}: {exc}") from exc

    def _request(self, method: str, route: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
        return self._request_at(self.base_url, method, route, body)

    def model_id(self) -> str:
        payload = self._request("GET", "/models")
        models = [m for m in payload.get("data", []) if m.get("loaded", True) is not False]  # Unsloth lists installed models too
        if not models:
            raise WorkflowError("The API returned no loaded models")
        return str(models[0]["id"])

    def load_mismatch(self) -> str:
        """Why the loaded model's load settings differ from this role's auto_load ("" if same or unknowable)."""
        if not (self.management_url and self.auto_load):
            return ""
        try:
            status = self._request_at(self.management_url, "GET", "/api/inference/status")
        except WorkflowError:
            return ""
        # requested_* hold what the active load was invoked with; the unprefixed reasoning_budget is the effective value.
        have = {
            "max_seq_length": status.get("requested_context_length"),
            "reasoning_budget": status.get("requested_reasoning_budget", status.get("reasoning_budget")),
        }
        diffs = [
            f"{key} {have[key]} -> {self.auto_load[key]}"
            for key in have
            if key in self.auto_load and isinstance(have[key], int) and have[key] != self.auto_load[key]
        ]
        return ", ".join(diffs)

    def ensure_model(self, role: str, interactive: bool) -> str:
        load_error = ""
        name = self.title or role
        while True:
            try:
                model = self.model_id()
            except WorkflowError:
                model = ""
            matched = bool(model) and (not self.expected or self.expected in model.lower())
            reason = self.load_mismatch() if matched else ""
            if matched and not reason:
                print(f"[{name}] {model}")
                emit("model", role=role, id=model)
                return model
            if self.management_url and self.auto_load:
                try:
                    if reason:
                        print(f"[{name}] {model} is loaded with other settings ({reason}); reloading ...")
                    print(f"[{name}] loading {self.auto_load['model_path']} ...")
                    emit("loading", role=role, path=self.auto_load["model_path"], url=self.management_url)
                    self._request_at(self.management_url, "POST", "/api/inference/load", self.auto_load)
                    # A model the user had loaded and we reloaded is ours from here on, so it is unloaded at exit.
                    LOADED_BY_RUN[self.auto_load["model_path"]] = self
                    model = self.model_id()
                    if not self.expected or self.expected in model.lower():
                        print(f"[{name}] {model}")
                        emit("model", role=role, id=model)
                        return model
                    load_error = f"load completed but API reports {model!r}"
                except WorkflowError as exc:
                    load_error = str(exc)
                    if matched:
                        # A failed reload may already have unloaded the old model (OOM, timeout mid-load): re-check.
                        try:
                            model = self.model_id()
                        except WorkflowError:
                            model = ""
                        if model and (not self.expected or self.expected in model.lower()):
                            print(f"[{name}] reload failed ({load_error}); continuing with the loaded model")
                            emit("model", role=role, id=model)
                            return model
            expected = self.expected or "the configured model"
            if not interactive:
                raise WorkflowError(f"{name}: expected {expected!r}, loaded {model or 'nothing'!r}; {load_error}")
            detail = f"\nAutomatic load failed: {load_error}" if load_error else ""
            answer = input(f"{detail}\nLoad {role} model ({expected}) in Unsloth, then press Enter [q quits]: ")
            if answer.strip().lower() == "q":
                raise KeyboardInterrupt

    def complete(
        self,
        model: str,
        messages: list[dict[str, Any]],
        sampling: dict[str, Any],
        tools: list[dict[str, Any]] | None = None,
    ) -> dict[str, Any]:
        body: dict[str, Any] = {"model": model, "messages": messages, **sampling}
        if tools:
            body.update({"tools": tools, "tool_choice": "auto"})
        if self.thinking_off:
            body["enable_thinking"] = False
        fell_back = False
        while True:
            started = time.time()
            streamed = False
            try:
                if EVENTS:  # the launcher renders output as it is generated
                    payload = self._stream_chat(body)
                    streamed = payload.pop("_streamed", False)
                else:
                    payload = self._request("POST", "/chat/completions", body)
            except WorkflowError as exc:
                text = str(exc).lower()
                if text.startswith("api 400") and "context" in text and re.search(r"length|exceed|too long|too many tokens", text):
                    raise WorkflowError(
                        f"{self.title or self.role}: the conversation exceeded the model's context length. "
                        "Raise the role's auto_load.max_seq_length."
                    ) from exc
                raise
            try:
                choice = payload["choices"][0]
                message = choice["message"]
            except (KeyError, IndexError, TypeError) as exc:
                raise WorkflowError(f"Unexpected API response: {payload}") from exc
            if choice.get("finish_reason") == "length" and not has_output(message):
                if body.get("enable_thinking") is not False:
                    print(f"[{self.title or self.role}] reply cut off while thinking; retrying without thinking")
                    body = {**body, "enable_thinking": False}
                    fell_back = True
                    emit("done", role=self.role, final=False)  # close the discarded streamed attempt in the launcher
                    continue
                raise WorkflowError(
                    f"{self.title or self.role}: the output limit was reached while thinking and the reply was empty. "
                    "Raise sampling.max_tokens or lower the role's auto_load.reasoning_budget."
                )
            self.cut_off = choice.get("finish_reason") == "length"
            if self.cut_off:
                print(f"[{self.title or self.role}] reply cut off at the output limit (max_tokens={body.get('max_tokens', '?')})")
            if EVENTS:
                self._emit_turn(message, payload, started, streamed)
            if fell_back and not self.thinking_off:
                self.thinking_off = True
                print(f"[{self.title or self.role}] thinking disabled for the rest of this turn")
            return message

    def _stream_chat(self, body: dict[str, Any]) -> dict[str, Any]:
        """POST with stream=true; emit delta events as they arrive and return a normal-shaped response."""
        request = Request(
            f"{self.base_url}/chat/completions",
            data=json.dumps({**body, "stream": True, "stream_options": {"include_usage": True}}).encode("utf-8"),
            method="POST",
            headers={"Authorization": f"Bearer {self.api_key}", "Content-Type": "application/json", "Accept": "text/event-stream"},
        )
        content: list[str] = []
        reasoning: list[str] = []
        calls: dict[int, dict[str, Any]] = {}
        usage: dict[str, Any] = {}
        finish = None
        splitter = ThinkSplitter()

        def put(kind: str, text: str) -> None:
            (reasoning if kind == "thinking" else content).append(text)
            emit("delta", role=self.role, kind=kind, text=text)

        try:
            with urlopen(request, timeout=self.timeout) as response:
                if response.headers.get_content_type() != "text/event-stream":
                    return json.loads(response.read().decode("utf-8"))  # server ignored stream=true
                for raw in response:
                    line = raw.decode("utf-8", errors="replace").strip()
                    if not line.startswith("data:"):
                        continue
                    data = line[5:].strip()
                    if data == "[DONE]":
                        break
                    chunk = json.loads(data)
                    if chunk.get("error"):
                        raise WorkflowError(f"API stream error: {chunk['error']}")
                    usage = chunk.get("usage") or usage
                    for choice in chunk.get("choices") or []:
                        finish = choice.get("finish_reason") or finish
                        delta = choice.get("delta") or {}
                        thought = delta.get("reasoning_content") or delta.get("reasoning")
                        if thought:
                            put("thinking", thought)
                        for kind, text in splitter.feed(delta.get("content") or ""):
                            put(kind, text)
                        for call in delta.get("tool_calls") or []:
                            slot = calls.setdefault(call.get("index", 0), {"id": "", "name": "", "arguments": ""})
                            function = call.get("function") or {}
                            slot["id"] = call.get("id") or slot["id"]
                            slot["name"] += function.get("name") or ""
                            fragment = function.get("arguments") or ""
                            slot["arguments"] += fragment
                            emit("delta", role=self.role, kind="tool", index=call.get("index", 0), name=slot["name"], text=fragment)
        except HTTPError as exc:
            raise WorkflowError(f"API {exc.code}: {exc.read().decode('utf-8', errors='replace')}") from exc
        except (URLError, TimeoutError) as exc:
            raise WorkflowError(f"Cannot reach {self.base_url}: {exc}") from exc
        for kind, text in splitter.flush():
            put(kind, text)
        message: dict[str, Any] = {"role": "assistant", "content": "".join(content)}
        if reasoning:
            message["reasoning_content"] = "".join(reasoning)
        if calls:
            message["tool_calls"] = [
                {"id": slot["id"] or f"call_{index}", "type": "function", "function": {"name": slot["name"], "arguments": slot["arguments"]}}
                for index, slot in sorted(calls.items())
            ]
        return {"choices": [{"message": message, "finish_reason": finish}], "usage": usage, "_streamed": True}

    def _monitor_row(self, started: float) -> dict[str, Any]:
        """Unsloth's own per-request stats (same source as the live-TPS mod); empty if unavailable."""
        try:
            rows = self._request_at(self.management_url or self.base_url, "GET", "/api/inference/monitor").get("entries", [])
        except WorkflowError:
            return {}
        mine = [r for r in rows if r.get("kind") == "request" and r.get("via_api_key") and r.get("started_at", 0) >= started - 2]
        return max(mine, key=lambda r: r["started_at"]) if mine else {}

    def _emit_turn(self, message: dict[str, Any], payload: dict[str, Any], started: float, streamed: bool = False) -> None:
        if streamed:
            emit("done", role=self.role, final=not message.get("tool_calls"))
        else:
            thinking, answer = split_reasoning(message)
            if thinking:
                emit("thinking", role=self.role, text=thinking[:40_000])
            if answer or message.get("tool_calls"):
                emit("message", role=self.role, text=answer[:40_000], final=not message.get("tool_calls"))
        usage, row = payload.get("usage") or {}, self._monitor_row(started)
        elapsed = max(time.time() - started, 1e-3)
        completion = row.get("completion_tokens") or usage.get("completion_tokens") or 0
        emit(
            "usage",
            role=self.role,
            prompt=row.get("prompt_tokens") or usage.get("prompt_tokens") or 0,
            completion=completion,
            tps=row.get("tok_per_sec") or round(completion / elapsed, 2),
            prompt_tps=row.get("prompt_tok_per_sec"),
        )


BUILTIN_ROLES = ("planner", "implementer", "reviewer")


def custom_workers(config: dict[str, Any], warn: bool = False) -> list[dict[str, Any]]:
    """Valid entries of config["custom_workers"] in config order; invalid ones are skipped (logged when warn)."""
    raw = config.get("custom_workers")
    if not raw:
        return []

    def skip(label: str, why: str) -> None:
        if warn:
            print(f"[workers] ignoring custom worker {label}: {why}")

    if not isinstance(raw, list):
        skip("list", "custom_workers must be an array")
        return []
    workers: list[dict[str, Any]] = []
    ids: set[str] = set(BUILTIN_ROLES) | set(config.get("roles") or {})
    titles: set[str] = set(BUILTIN_ROLES)
    for index, item in enumerate(raw):
        if not isinstance(item, dict):
            skip(f"#{index + 1}", "not an object")
            continue

        def text(key: str) -> str:
            value = item.get(key)
            return value.strip() if isinstance(value, str) else ""

        title = text("title")
        auto = item.get("auto_load")
        problems = [
            why for bad, why in (
                (not text("id") or text("id") in ids, "missing, duplicate or reserved id"),
                (not title or title.lower() in titles, "missing, duplicate or reserved title"),
                (item.get("kind") not in ("specialist", "reviewer"), "kind must be 'specialist' or 'reviewer'"),
                (not text("base_url"), "missing base_url"),
                (not (text("expected_model_contains") or (isinstance(auto, dict) and auto.get("model_path"))),
                 "no model (expected_model_contains or auto_load.model_path)"),
                (not text("system_prompt"), "missing system_prompt"),
                (item.get("kind") == "specialist" and not text("description"), "a specialist needs a description"),
                (auto is not None and not isinstance(auto, dict), "auto_load must be an object"),
                (not isinstance(item.get("sampling", {}), dict), "sampling must be an object"),
            ) if bad
        ]
        if problems:
            skip(repr(title or f"#{index + 1}"), "; ".join(problems))
            continue
        ids.add(text("id"))
        titles.add(title.lower())
        workers.append({**item, "title": title})
    return workers


def specialist_titles(config: dict[str, Any]) -> list[str]:
    return [w["title"] for w in custom_workers(config) if w["kind"] == "specialist"]


def worker_name(task: dict[str, Any]) -> str:
    """The task's "worker" field; blank or "Implementer" (any case) means no Specialist."""
    name = str(task.get("worker") or "").strip()
    return "" if name.lower() == "implementer" else name


def find_specialist(task: dict[str, Any], workers: list[dict[str, Any]]) -> dict[str, Any] | None:
    """The Specialist a task is assigned to: by worker_id (survives renames), else by title (case-insensitive).
    None means the Implementer; a Specialist that no longer exists falls back to it with a warning."""
    name = worker_name(task)
    if not name:
        return None
    specialists = [w for w in workers if w["kind"] == "specialist"]
    found = next((w for w in specialists if task.get("worker_id") and w["id"] == task["worker_id"]), None) or next(
        (w for w in specialists if w["title"].lower() == name.lower()), None)
    if not found:
        print(f"[workers] Specialist {name!r} no longer exists; continuing with the Implementer")
    return found


def role_prompt(base: str, worker: dict[str, Any]) -> str:
    """The built-in role prompt unchanged, followed by the custom worker's own section."""
    return f"{base.rstrip()}\n\n=== YOUR ROLE: {worker['title']} ===\n{worker['system_prompt'].strip()}\n"


def display_name(config: dict[str, Any], role: str) -> str:
    """Title of the custom worker with this id, else the role itself (for human-readable log lines)."""
    return next((w["title"] for w in custom_workers(config) if w["id"] == role), role)


def role_settings(config: dict[str, Any], role: str) -> tuple[ApiClient, dict[str, Any]]:
    """Client + sampling for a built-in role, or for a custom worker when `role` is its id."""
    if role in (config.get("roles") or {}):
        settings = config["roles"][role]
    else:
        settings = next((w for w in custom_workers(config) if w["id"] == role), None) or config["roles"][role]
    sampling = dict(settings.get("sampling", {}))
    sampling.update(sampling.pop("extra_body", {}))
    client = ApiClient(settings)
    client.role = role
    client.title = display_name(config, role)
    return client, sampling


def ask_once(config: dict[str, Any], role: str, system: str, user: str, interactive: bool, thinking: bool = True) -> str:
    client, sampling = role_settings(config, role)
    if not thinking:
        sampling = {**sampling, "enable_thinking": False}  # this call only
    model = client.ensure_model(role, interactive)
    message = client.complete(model, [{"role": "system", "content": system}, {"role": "user", "content": user}], sampling)
    return str(message.get("content") or "")


REPAIR_NOTE = "Return only one valid JSON object with the same intended data. Do not add commentary or markdown fences."


def extract_json_with_repair(
    config: dict[str, Any], role: str, response: str, interactive: bool, max_repairs: int = 2,
    system: str = "", user: str = "",
) -> tuple[dict[str, Any], str]:
    """Parse the reply, asking the model again (thinking off) when it cannot be read. `system`/`user` are the
    prompt that produced `response`. A reply with no whole object (empty, cut off, prose only) is re-asked from
    that prompt, never "repaired": repairing a fragment would invent the rest. A whole but malformed object gets
    up to max_repairs repairs that keep the original output contract, then one fresh attempt. At most two
    fresh attempts in all; without a prompt only repairs are possible."""
    name = display_name(config, role)
    repairs = fresh = 0
    while True:
        try:
            return extract_json(response), response
        except WorkflowError as exc:
            incomplete = isinstance(exc, IncompleteReply)
            if user and (incomplete or repairs == max_repairs) and fresh < (2 if incomplete else 1):
                fresh += 1
                print(f"[{name}] {'reply cut off / no JSON' if incomplete else 'JSON still invalid'}; re-asking without thinking")
                response = ask_once(config, role, system, f"{user}\n\nReturn only the JSON object, with no commentary.", interactive, thinking=False)
            elif repairs < max_repairs and not (incomplete and user):
                repairs += 1
                print(f"[{name}] invalid JSON; requesting correction ({repairs}/{max_repairs}) ...")
                response = ask_once(
                    config,
                    role,
                    f"{system}\n\nYour previous reply was not valid JSON. {REPAIR_NOTE}" if system else f"Repair malformed JSON. {REPAIR_NOTE}",
                    f"PARSER ERROR\n{exc}\n\nMALFORMED RESPONSE\n{answer_text({'content': response})[-20_000:]}",
                    interactive,
                    thinking=False,
                )
            else:
                raise WorkflowError(
                    f"{name}: {exc} (after {repairs} repairs and {fresh} fresh attempts). "
                    "Nothing was saved from this reply; the run can simply be restarted."
                ) from exc


def function_tool(name: str, description: str, properties: dict[str, Any], required: list[str]) -> dict[str, Any]:
    return {
        "type": "function",
        "function": {
            "name": name,
            "description": description,
            "parameters": {"type": "object", "properties": properties, "required": required, "additionalProperties": False},
        },
    }


IMPLEMENTER_TOOLS = [
    function_tool("read_file", "Read one allowed UTF-8 text file.", {"path": {"type": "string"}}, ["path"]),
    function_tool(
        "write_file",
        "Create or replace one allowed UTF-8 text file with complete content.",
        {"path": {"type": "string"}, "content": {"type": "string"}},
        ["path", "content"],
    ),
    function_tool(
        "replace_text",
        "Replace one exact, unique string in an allowed UTF-8 text file.",
        {"path": {"type": "string"}, "old": {"type": "string"}, "new": {"type": "string"}},
        ["path", "old", "new"],
    ),
    function_tool(
        "run_validation",
        "Run one validation command pre-approved in config.json by its name.",
        {"name": {"type": "string"}},
        ["name"],
    ),
]

REVIEWER_TOOLS = [IMPLEMENTER_TOOLS[0], IMPLEMENTER_TOOLS[3]]


def execute_tool(
    name: str,
    arguments: dict[str, Any],
    project_root: Path,
    allowed: list[str],
    validations: dict[str, list[str]],
    writable: bool,
) -> str:
    if name == "read_file":
        path = path_is_allowed(str(arguments["path"]), allowed, project_root)
        if not path.is_file():
            return "ERROR: file does not exist yet; if the task needs it, create it with write_file"
        return load_text(path)
    if name == "write_file" and writable:
        path = path_is_allowed(str(arguments["path"]), allowed, project_root)
        path.parent.mkdir(parents=True, exist_ok=True)
        save_text(path, str(arguments["content"]))
        return "OK"
    if name == "replace_text" and writable:
        path = path_is_allowed(str(arguments["path"]), allowed, project_root)
        content = load_text(path)
        old = str(arguments["old"])
        count = content.count(old)
        if count != 1:
            return f"ERROR: expected exactly one match, found {count}"
        save_text(path, content.replace(old, str(arguments["new"]), 1))
        return "OK"
    if name == "run_validation":
        validation_name = str(arguments["name"])
        command = validations.get(validation_name)
        if not command:
            return f"ERROR: unknown validation {validation_name!r}; allowed: {', '.join(validations) or 'none'}"
        try:
            completed = subprocess.run(
                command,
                cwd=project_root,
                text=True,
                capture_output=True,
                timeout=300,
                shell=False,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            return f"ERROR: {exc}"
        output = (completed.stdout + completed.stderr)[-12000:]
        return f"exit_code={completed.returncode}\n{output}"
    return f"ERROR: tool {name!r} is unavailable"


def allowed_files_context(files: dict[str, str | None]) -> str:
    """What the implementer starts from: each allowed file's current content, or a note that it must be created."""
    parts = []
    for name, content in files.items():
        if content is None:
            parts.append(f"--- {name} --- (does not exist yet; create it with write_file)")
        else:
            parts.append(f"--- {name} ---\n{content[:20_000]}")
    return "\n\n".join(parts)


def text_tool_calls(text: str, tool_names: set[str]) -> list[dict[str, Any]]:
    """Recover tool calls a model printed as JSON text instead of emitting them natively."""
    decoder, calls, pos = json.JSONDecoder(), [], text.find("{")
    while pos != -1:
        try:
            obj, end = decoder.raw_decode(text, pos)
        except json.JSONDecodeError:
            pos = text.find("{", pos + 1)
            continue
        if isinstance(obj, dict) and obj.get("name") in tool_names and isinstance(obj.get("arguments"), dict):
            calls.append({"id": f"text{len(calls)}", "function": {"name": obj["name"], "arguments": json.dumps(obj["arguments"])}})
        pos = text.find("{", end)
    return calls


def _parsed_arguments(raw: str) -> Any:
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw


def syntax_gate(project_root: Path, allowed: list[str], before: dict[str, str | None] | None = None) -> tuple[list[dict[str, str]], str]:
    """Deterministic syntax check of the existing allowed files. Returns (defects, one-line summary).

    Only files whose content differs from `before` can produce defects; errors in unchanged files are
    reported in the summary as pre-existing. Without `before`, every file counts as changed.
    """
    defects: list[dict[str, str]] = []
    preexisting: list[str] = []
    checked: list[str] = []
    node = shutil.which("node")
    skipped_js = False
    for name, content in snapshot(project_root, allowed).items():
        if content is None:
            continue
        suffix = Path(name).suffix.lower()
        error = None
        if suffix in {".js", ".mjs", ".cjs"}:
            if not node:
                skipped_js = True
                continue
            try:
                # Decode as UTF-8 ourselves: text=True uses the locale codepage on Windows and can lose the error text.
                done = subprocess.run([node, "--check", name], cwd=project_root, capture_output=True, timeout=30, shell=False)
                if done.returncode:
                    output = (done.stderr or done.stdout).decode("utf-8", errors="replace").strip()[-1500:]
                    error = output or f"node --check exited {done.returncode}"
            except (OSError, subprocess.TimeoutExpired) as exc:
                print(f"[syntax] could not run node on {name}: {exc}")
                continue
        elif suffix == ".py":
            try:
                compile(content.lstrip("﻿"), name, "exec")
            except (SyntaxError, ValueError) as exc:
                error = f"{type(exc).__name__}: {exc} (line {getattr(exc, 'lineno', '?')})"
        elif suffix == ".json":
            try:
                json.loads(content.lstrip("﻿"))
            except json.JSONDecodeError as exc:
                error = f"JSONDecodeError: {exc}"
        else:
            continue
        checked.append(name)
        if error and before is not None and before.get(name) == content:
            preexisting.append(name)
        elif error:
            defects.append({"file": name, "defect": "Syntax error", "expected": "The file parses without errors", "evidence": error})
    if defects:
        summary = f"FAILED ({', '.join(d['file'] for d in defects)})"
    elif checked:
        summary = f"passed ({', '.join(n for n in checked if n not in preexisting)})"
    else:
        summary = "skipped (node not found)" if skipped_js else "skipped (no checkable files)"
    if preexisting:
        summary += f"; pre-existing errors in unchanged files (not caused by this task): {', '.join(preexisting)}"
    print(f"[syntax] {summary}")
    return defects, f"SYNTAX CHECK: {summary}"


def syntax_message(defects: list[dict[str, str]]) -> str:
    return "SYNTAX CHECK FAILED. Fix these errors with the file tools, then answer again:\n" + "\n".join(
        f"- {d['file']}: {d['evidence']}" for d in defects
    )


# Injected first into the checked page: records page errors, unhandled rejections and console.error, then after
# load + a short settle runs the task's acceptance steps (__AO_STEPS__: a JSON list, or null for a load-only check)
# within __AO_BUDGET__ ms and writes a JSON report into the DOM, which the browser's --dump-dom prints.
# A step fails on its own check or when a page error is recorded while it runs; the steps after a failure are not run.
BEHAVIOUR_PROBE = r"""<script>(function(){var E=[],S=__AO_STEPS__;function p(m,s,l){E.push({message:String(m).slice(0,500),source:s||"",line:l||0});}
window.addEventListener("error",function(e){if(e.target===window)p(e.message||e.error,e.filename,e.lineno);},true);
window.addEventListener("unhandledrejection",function(e){var r=e.reason;p("Unhandled promise rejection: "+(r&&r.message||r));});
var ce=console.error;console.error=function(){p(Array.prototype.map.call(arguments,String).join(" "),"console.error",0);return ce.apply(console,arguments);};
function shown(el){return el.getClientRects().length>0&&getComputedStyle(el).visibility!=="hidden";}
function cut(t){t=String(t);return t.length>200?t.slice(0,200)+"...":t;}
function q(s){var el;try{el=document.querySelector(s);}catch(x){throw new Error("invalid selector "+s);}if(!el)throw new Error("selector "+s+" not found");return el;}
function use(s){var el=q(s);if(!shown(el))throw new Error("selector "+s+" is not visible");return el;}
function fire(el){el.dispatchEvent(new Event("input",{bubbles:true}));el.dispatchEvent(new Event("change",{bubbles:true}));}
function sleep(ms){return new Promise(function(r){setTimeout(r,ms);});}
async function run(st){var k=st["do"]||st.expect,el,t;switch(k){
case "click":use(st.selector).click();return;
case "fill":el=use(st.selector);el.value=String(st.value);fire(el);return;
case "select":el=use(st.selector);var o=Array.prototype.find.call(el.options||[],function(o){return o.value===String(st.value)||o.text.trim()===String(st.value);});
if(!o)throw new Error("no option '"+st.value+"' in "+st.selector);el.value=o.value;fire(el);return;
case "check":el=use(st.selector);if(el.checked!==st.value)el.click();if(el.checked!==st.value)throw new Error(st.selector+" did not become "+(st.value?"checked":"unchecked"));return;
case "goto":location.hash=st.hash;return;
case "wait":await sleep(Math.min(st.ms,2000));return;
case "text":t=q(st.selector).innerText;if(t.indexOf(st.contains)<0)throw new Error("expected text '"+st.contains+"' in "+st.selector+"; got '"+cut(t)+"'");return;
case "visible":if(!shown(q(st.selector)))throw new Error(st.selector+" is not visible");return;
case "hidden":if(shown(q(st.selector)))throw new Error(st.selector+" is visible");return;
case "count":t=document.querySelectorAll(st.selector).length;if(t<st.min||(st.max!=null&&t>st.max))throw new Error("expected "+st.min+(st.max!=null?"-"+st.max:" or more")+" match(es) for "+st.selector+"; got "+t);return;
case "storage":t=localStorage.getItem(st.key);if(t===null)throw new Error("localStorage['"+st.key+"'] missing");
if(st.contains!=null&&t.indexOf(st.contains)<0)throw new Error("expected localStorage['"+st.key+"'] to contain '"+st.contains+"'; got '"+cut(t)+"'");
if(st.json_length_min!=null){var a;try{a=JSON.parse(t);}catch(x){a=null;}if(!Array.isArray(a)||a.length<st.json_length_min)throw new Error("expected localStorage['"+st.key+"'] to be a JSON array of at least "+st.json_length_min+" item(s); got '"+cut(t)+"'");}return;
case "no_errors":if(E.length)throw new Error("page error: "+E[0].message);return;}
throw new Error("unknown step");}
async function steps(){var R=[],stop=false,end=Date.now()+__AO_BUDGET__;for(var i=0;i<S.length;i++){var r={index:i+1,step:S[i],ok:null,detail:"not run"};R.push(r);if(stop)continue;
if(Date.now()>end){r.ok=false;r.detail="time budget exceeded";stop=true;continue;}
var n=E.length;try{await run(S[i]);await sleep(150);r.ok=E.length===n;r.detail=r.ok?"":"page error: "+E[n].message;}catch(x){r.ok=false;r.detail=String(x&&x.message||x);}
stop=!r.ok;}return R;}
window.addEventListener("load",function(){setTimeout(async function(){var R=S?await steps():null,b=document.body,v=0;if(b){var all=b.querySelectorAll("*");for(var i=0;i<all.length;i++)if(all[i].getClientRects().length)v++;}
var s=document.createElement("script");s.type="application/json";s.id="__ao_report";
s.textContent=JSON.stringify({errors:E,text:(b?b.innerText:"").slice(0,1500),visible:v,steps:R}).replace(/</g,"\\u003c");
document.documentElement.appendChild(s);},500);});})();</script>"""


def behaviour_probe(steps: list[dict[str, Any]] | None) -> tuple[str, int]:
    """The probe for these acceptance steps and the --virtual-time-budget (ms) it needs."""
    waits = sum(min(step.get("ms", 0), 2000) for step in steps or [] if step.get("do") == "wait")
    budget = min(15_000, 5000 + 400 * len(steps or []) + waits)
    # json.dumps escapes U+2028/2029 (ensure_ascii); "<" is escaped so no "</script>" or "<!--" can end the script early
    encoded = json.dumps(steps).replace("<", "\\u003c")
    return BEHAVIOUR_PROBE.replace("__AO_BUDGET__", str(budget - 2000)).replace("__AO_STEPS__", encoded), budget


def find_browser(configured: str | None = None) -> str | None:
    """Configured path, else Edge, else Chrome at their standard install paths, else msedge/chrome/chromium on PATH."""
    if configured:
        return configured if Path(configured).is_file() else None
    roots = [os.environ.get(v) for v in ("ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA")]
    for sub in (r"Microsoft\Edge\Application\msedge.exe", r"Google\Chrome\Application\chrome.exe"):
        for root in filter(None, roots):
            if (Path(root) / sub).is_file():
                return str(Path(root) / sub)
    return next(filter(None, map(shutil.which, ("msedge", "chrome", "chromium", "chromium-browser", "google-chrome"))), None)


def load_page(
    browser: str, project_root: Path, page: str, timeout: int = 30, steps: list[dict[str, Any]] | None = None,
) -> dict[str, Any]:
    """Serve project_root on 127.0.0.1, load `page` headlessly in a fresh profile, run the acceptance `steps` and return
    the probe report ({"errors": [...], "text": str, "visible": int, "steps": [{"index", "step", "ok", "detail"}] or
    None}; ok is null for steps not run). Missing scripts/stylesheets (404) are added as errors.
    Raises WorkflowError when no report comes back (browser failed, timed out, or the probe never ran)."""
    target = "/" + quote(page)
    probe, budget = behaviour_probe(steps)
    missing: list[str] = []

    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *args: Any, **kwargs: Any) -> None:
            super().__init__(*args, directory=str(project_root), **kwargs)

        def do_GET(self) -> None:  # noqa: N802 (http.server naming)
            if urlsplit(self.path).path != target:
                parts = unquote(urlsplit(self.path).path).lower().split("/")
                if "api_key.txt" in parts or any(p.startswith(".") for p in parts if p):  # never serve secrets or dotfiles
                    return self.send_error(403)
                return super().do_GET()
            text = (project_root / page).read_text(encoding="utf-8", errors="replace")
            for pattern in (r"<head\b[^>]*>", r"<html\b[^>]*>", r"<!doctype[^>]*>"):
                found = re.search(pattern, text, re.IGNORECASE)
                if found:
                    text = text[:found.end()] + probe + text[found.end():]
                    break
            else:
                text = probe + text
            body = text.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def send_error(self, code: int, *args: Any, **kwargs: Any) -> None:
            name = unquote(urlsplit(self.path).path).lstrip("/")
            if code == 404 and Path(name).suffix.lower() in {".js", ".mjs", ".css"}:
                missing.append(name)
            super().send_error(code, *args, **kwargs)

        def log_message(self, *args: Any) -> None:
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    profile = tempfile.mkdtemp(prefix="ao-behaviour-")
    try:
        url = f"http://127.0.0.1:{server.server_address[1]}{target}"
        args = [browser, "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                f"--user-data-dir={profile}", f"--virtual-time-budget={budget}", "--dump-dom", url]
        try:
            proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, shell=False)
        except OSError as exc:
            raise WorkflowError(f"could not start the browser: {exc}") from exc
        try:
            dom = proc.communicate(timeout=timeout)[0].decode("utf-8", errors="replace")
        except subprocess.TimeoutExpired:
            if os.name == "nt":  # the browser's own child processes too, by this pid only
                subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True)
            proc.kill()
            proc.communicate()
            raise WorkflowError(f"the page did not finish loading within {timeout} s (endless loop or hung browser?)")
        found = re.search(r'id="__ao_report">(.*?)</script>', dom, re.DOTALL)
        if not found:
            raise WorkflowError(f"no report came back from the page (browser exit code {proc.returncode}); its scripts may never have run")
        report = json.loads(found.group(1))
    finally:
        server.shutdown()
        server.server_close()
        shutil.rmtree(profile, ignore_errors=True)
    origin = url.rsplit(target, 1)[0] + "/"
    for error in report["errors"]:
        error["source"] = str(error.get("source") or "").replace(origin, "")
    report["errors"] += [{"message": f"Failed to load {name} (404 not found)", "source": page, "line": 0} for name in missing]
    return report


def behaviour_gate(
    project_root: Path, config: dict[str, Any], steps: list[dict[str, Any]] | None = None,
) -> tuple[list[dict[str, str]], str]:
    """Load the project's page headlessly and run the task's acceptance steps. Returns (defects, BEHAVIOUR CHECK block);
    ([], "") when not a web project. steps None is a load-only check (before planning); [] is a task without steps.

    Applies when the root has index.html or config sets behaviour_check.page; behaviour_check false disables it.
    """
    setting = config.get("behaviour_check", {})
    if setting is False:
        return [], ""
    setting = setting if isinstance(setting, dict) else {}
    page = str(setting.get("page") or "index.html")
    path = safe_relative_path(page, project_root)
    if not path.is_file():
        if setting.get("page"):
            print(f"[behaviour] skipped (page {page} not found)")
            return [], f"BEHAVIOUR CHECK: skipped (page {page} not found)"
        return [], ""
    page = path.relative_to(project_root.resolve()).as_posix()
    browser = find_browser(setting.get("browser"))
    if not browser:
        print("[behaviour] skipped (no Chromium browser found)")
        return [], "BEHAVIOUR CHECK: skipped (no Chromium browser found; install Edge or Chrome, or set behaviour_check.browser)"
    try:
        report = load_page(browser, project_root, page, steps=steps or None)
    except WorkflowError as exc:
        print(f"[behaviour] FAILED (could not check {page}: {exc})")
        defect = {"file": page, "defect": "Behaviour check could not complete", "expected": "The page loads in a headless browser",
                  "evidence": str(exc)}
        return [defect], f"BEHAVIOUR CHECK: FAILED (could not check {page}: {exc})"
    errors = [f"{e['message']} ({e['source'] or page}{':' + str(e['line']) if e['line'] else ''})" for e in report["errors"]]
    results = report.get("steps") or []
    failed = next((r for r in results if r["ok"] is False), None)
    step_lines = [
        f"{'✓' if r['ok'] else '✗' if r['ok'] is False else '-'} {r['index']}. {json.dumps(r['step'], ensure_ascii=False)}"
        + (f" — {r['detail']}" if r["detail"] else "") for r in results
    ]
    if steps is None:
        steps_part = ""
    elif results:
        steps_part = f"Acceptance steps ({sum(r['ok'] is True for r in results)}/{len(results)} passed):\n" + "\n".join(step_lines) + "\n"
    else:
        steps_part = "Acceptance steps: none (no acceptance steps were given; only the page load was checked)\n"
    if errors or failed:
        print(f"[behaviour] FAILED ({page}: "
              + (f"acceptance step {failed['index']} failed: {failed['detail']})" if failed else f"{len(errors)} error(s): {errors[0]})"))
        defects = [{"file": page, "defect": "Error while loading the page", "expected": "The page loads without console or page errors",
                    "evidence": error} for error in errors]
        if failed:
            defects.append({"file": page, "defect": "Acceptance step failed", "expected": "Every acceptance step passes",
                            "evidence": f"acceptance step {failed['index']} failed: "
                                        f"{json.dumps(failed['step'], ensure_ascii=False)} — {failed['detail']}"})
        return defects, f"BEHAVIOUR CHECK: FAILED ({page})\nErrors:\n" + "\n".join(f"- {e}" for e in errors or ["none"]) + ("\n" + steps_part).rstrip("\n")
    print(f"[behaviour] passed ({page}: no errors, {len(results)} acceptance step(s), {report['visible']} visible elements)")
    return [], (
        f"BEHAVIOUR CHECK: passed ({page} loaded in a headless browser with a fresh profile)\nErrors: none\n{steps_part}"
        f"Visible elements: {report['visible']}\nVisible text sample:\n{report['text'].strip() or '(no visible text)'}"
    )


TOOL_CALL_TEXT = re.compile(r'"name"\s*:\s*"(?:write_file|replace_text|read_file|run_validation)"')


def run_agent(
    config: dict[str, Any],
    role: str,
    system: str,
    user: str,
    project_root: Path,
    allowed: list[str],
    writable: bool,
    interactive: bool,
    before: dict[str, str | None] | None = None,
) -> str:
    """`before` is the task-start snapshot: allowed files already differing from it are prior (unreviewed) work."""
    client, sampling = role_settings(config, role)
    model = client.ensure_model(role, interactive)
    messages: list[dict[str, Any]] = [{"role": "system", "content": system}, {"role": "user", "content": user}]
    tools = IMPLEMENTER_TOOLS if writable else REVIEWER_TOOLS
    max_steps = int(config.get("max_tool_steps", 30))
    validations = config.get("validation_commands", {})
    tool_names = {tool["function"]["name"] for tool in tools}
    ran_tools = False
    nudges = 0
    fixes = syntax_fixes = 0  # shared budget (3) for unverified-report and syntax send-backs; syntax uses at most 2
    last_error = False
    cutoffs = 0
    prev_text_calls = None
    start = snapshot(project_root, allowed)
    baseline = start if before is None else before
    prior_work = any(start.get(n) != baseline.get(n) for n in start)  # a reply without writes goes on to review
    name = display_name(config, role)
    for step in range(max_steps):
        try:
            response = client.complete(model, messages, sampling, tools)
        except WorkflowError as exc:
            if not tools or '"param":"tools"' not in str(exc):
                raise
            print(f"[{name}] model template does not support tool calling; continuing without tools ...")
            tools = []
            response = client.complete(model, messages, sampling, tools)
        messages.append(response)
        calls = response.get("tool_calls") or text_tool_calls(str(response.get("content") or ""), tool_names)
        native = bool(response.get("tool_calls"))
        unexecuted = not native and not calls and bool(TOOL_CALL_TEXT.search(str(response.get("content") or "")))
        if not native:
            signature = [(c["function"]["name"], _parsed_arguments(c["function"]["arguments"])) for c in calls]
            if calls and signature == prev_text_calls:
                calls = []
            prev_text_calls = signature
        else:
            prev_text_calls = None
        # Fake clients in tests have no cut_off attribute.
        if getattr(client, "cut_off", False) and (unexecuted or any(
            not isinstance(_parsed_arguments((c.get("function") or {}).get("arguments") or "{}"), dict) for c in calls
        )):
            cutoffs += 1
            if cutoffs > 2:
                raise WorkflowError(
                    f"{name}: the reply was cut off at the output limit {cutoffs} times in a row. "
                    "Raise this role's sampling.max_tokens (or auto_load.max_seq_length if the context is full)."
                )
            print(f"[{name}] truncated tool call not executed; sending back ({cutoffs}/2) ...")
            messages[-1] = {"role": "assistant", "content": str(response.get("content") or "")}  # never replay broken arguments
            messages.append({"role": "user", "content": "Your reply was cut off at the output limit, so its tool call was NOT executed. Use replace_text for smaller edits, or write files in smaller pieces."})
            continue
        cutoffs = 0
        if not calls:
            if writable and not ran_tools and not prior_work and nudges < 2:
                nudges += 1
                print(f"[{name}] no tool was executed; requesting real tool calls ({nudges}/2) ...")
                messages.append({"role": "user", "content": "No file was changed. Use the provided tool-calling interface to write the files; do not print JSON."})
                continue
            if writable and not ran_tools and not prior_work:
                reply = str(response.get("content") or "").strip()
                save_text(project_root / "RESULT.md", f"BLOCKED; {name} never executed a tool; no files were written.\n\nLast {name} reply:\n{reply}")
                raise WorkflowError(f"{name} never executed a tool; no files were written. Last reply: {reply[:500]}")
            reply = str(response.get("content") or "")
            if not writable:
                return reply
            reasons = []
            if unexecuted:
                reasons.append("it printed a tool call that was not valid JSON (comments or trailing text) and was NOT executed")
            if last_error:
                reasons.append("the last tool call failed and nothing succeeded after it")
            if reasons and fixes < 3 and step < max_steps - 1:
                fixes += 1
                print(f"[{name}] unverified report ({'; '.join(reasons)}); sending back ({fixes}/3) ...")
                messages.append({"role": "user", "content": "Your report is not accepted: " + "; ".join(reasons) + ". Redo it through the tool-calling interface with strictly valid JSON (no comments, no trailing text), then report only what actually succeeded."})
                continue
            defects = [] if reasons else syntax_gate(project_root, allowed, baseline)[0]
            if defects and fixes < 3 and syntax_fixes < 2 and step < max_steps - 1:
                fixes += 1
                syntax_fixes += 1
                print(f"[{name}] syntax errors remain; sending back ({syntax_fixes}/2) ...")
                messages.append({"role": "user", "content": syntax_message(defects)})
                continue
            return f"UNVERIFIED: {'; '.join(reasons)}.\n\n{reply}" if reasons else reply
        results = []
        for call in calls:
            try:
                function = call["function"]
                arguments = json.loads(function.get("arguments") or "{}")
                result = execute_tool(function["name"], arguments, project_root, allowed, validations, writable)
                emit("tool", role=role, name=function["name"], args=function.get("arguments") or "", result=result[:6000])
                ran_tools = ran_tools or (function["name"] in {"write_file", "replace_text"} and result == "OK")
                if function["name"] in {"write_file", "replace_text"}:
                    last_error = result.startswith("ERROR:")
            except (KeyError, TypeError, json.JSONDecodeError, WorkflowError) as exc:
                result = f"ERROR: {exc}"
                last_error = last_error or (isinstance(call, dict) and call.get("function", {}).get("name") in {"write_file", "replace_text"})
                emit("tool", role=role, name=str(call.get("function", {}).get("name", "?")), args="", result=result)
            if native:
                messages.append({"role": "tool", "tool_call_id": call.get("id", "tool"), "content": result})
            else:
                results.append(f"{call['function']['name']}: {result}")
        if not native:
            messages.append({"role": "user", "content": "TOOL RESULTS\n" + "\n".join(results)})
    raise WorkflowError(f"{role} exceeded max_tool_steps={max_steps}")


def task_limits(config: dict[str, Any]) -> tuple[int, int]:
    raw = config.get("task_limits", {})
    if not isinstance(raw, dict):
        raise WorkflowError("config task_limits must be an object")
    limits = []
    for key, default in (("max_allowed_files", 3), ("max_required_work", 7)):
        value = raw.get(key, default)
        if isinstance(value, bool) or not isinstance(value, int) or value < 1:
            raise WorkflowError(f"config task_limits.{key} must be a positive integer, got {value!r}")
        limits.append(value)
    return limits[0], limits[1]


def team_context(config: dict[str, Any]) -> str:
    lines = ["TEAM"]
    for role in ("implementer", "reviewer"):
        r = config.get("roles", {}).get(role) or {}
        auto = r.get("auto_load") or {}
        name = r.get("display_name") or auto.get("model_path") or r.get("expected_model_contains")
        parts = [str(name) if name else "unknown model"]
        if auto.get("gguf_variant"):
            parts.append(f"quant {auto['gguf_variant']}")
        if auto.get("max_seq_length"):
            parts.append(f"context {auto['max_seq_length']} tokens")
        if (r.get("sampling") or {}).get("max_tokens"):
            parts.append(f"max output {r['sampling']['max_tokens']} tokens")
        lines.append(f"{role.capitalize()}: " + ", ".join(parts))
    workers = custom_workers(config)
    for kind, heading in (
        ("specialist", 'Specialists (set "worker" to a title to give a task to one instead of the Implementer)'),
        ("reviewer", "Extra reviewers (they review every task after the Reviewer; you cannot assign them)"),
    ):
        group = [w for w in workers if w["kind"] == kind]
        if group:
            lines.append(heading + ":")
            lines.extend(f"- {w['title']}: {w.get('description') or 'no description'}" for w in group)
    files, work = task_limits(config)
    lines.append(f"Task limits: at most {files} allowed files and {work} required-work items per task")
    return "\n".join(lines)


MAX_ACCEPTANCE_STEPS = 12
_SELECTOR = (lambda v: isinstance(v, str) and v.strip() != "" and len(v) <= 200, "a CSS selector string of at most 200 characters")
_STRING = (lambda v: isinstance(v, str) and v != "", "a non-empty string")
_VALUE = (lambda v: isinstance(v, (str, int, float)) and not isinstance(v, bool), "a string")
_BOOL = (lambda v: isinstance(v, bool), "true or false")
_COUNT = (lambda v: isinstance(v, int) and not isinstance(v, bool) and v >= 0, "a non-negative integer")
_MS = (lambda v: isinstance(v, int) and not isinstance(v, bool) and 0 <= v <= 2000, "an integer from 0 to 2000")
# ("do" | "expect", kind) -> (required fields, optional fields); the probe in BEHAVIOUR_PROBE runs them
ACCEPTANCE_STEPS: dict[tuple[str, str], tuple[dict[str, Any], dict[str, Any]]] = {
    ("do", "click"): ({"selector": _SELECTOR}, {}),
    ("do", "fill"): ({"selector": _SELECTOR, "value": _VALUE}, {}),
    ("do", "select"): ({"selector": _SELECTOR, "value": _VALUE}, {}),
    ("do", "check"): ({"selector": _SELECTOR, "value": _BOOL}, {}),
    ("do", "goto"): ({"hash": _STRING}, {}),
    ("do", "wait"): ({"ms": _MS}, {}),
    ("expect", "text"): ({"selector": _SELECTOR, "contains": _STRING}, {}),
    ("expect", "visible"): ({"selector": _SELECTOR}, {}),
    ("expect", "hidden"): ({"selector": _SELECTOR}, {}),
    ("expect", "count"): ({"selector": _SELECTOR, "min": _COUNT}, {"max": _COUNT}),
    ("expect", "storage"): ({"key": _STRING}, {"contains": _STRING, "json_length_min": _COUNT}),
    ("expect", "no_errors"): ({}, {}),
}


def validate_acceptance(steps: Any) -> None:
    if not isinstance(steps, list) or len(steps) > MAX_ACCEPTANCE_STEPS:
        raise WorkflowError(f"Planner acceptance must be a list of at most {MAX_ACCEPTANCE_STEPS} steps")
    for number, step in enumerate(steps, 1):
        kinds = [(key, step[key]) for key in ("do", "expect") if isinstance(step, dict) and key in step]
        if len(kinds) != 1 or not isinstance(kinds[0][1], str) or kinds[0] not in ACCEPTANCE_STEPS:
            known = ", ".join(f'"{key}": "{kind}"' for key, kind in ACCEPTANCE_STEPS)
            raise WorkflowError(f"Planner acceptance step {number} must be an object with exactly one of {known}; "
                                f"got {json.dumps(step, ensure_ascii=False)[:300]}")
        required, optional = ACCEPTANCE_STEPS[kinds[0]]
        for name, (ok, kind) in (required | optional).items():
            if (name in required or name in step) and not ok(step.get(name)):
                raise WorkflowError(f'Planner acceptance step {number} ({kinds[0][0]} {kinds[0][1]}) needs "{name}" as {kind}')


def validate_task(
    task: dict[str, Any], project_root: Path, limits: tuple[int, int] = (3, 7), specialists: list[str] | None = (),
) -> None:
    max_files, max_work = limits
    required = ["id", "outcome", "allowed_files", "required_work", "validation", "stop_condition"]
    missing = [name for name in required if not task.get(name)]
    if missing:
        raise WorkflowError(f"Planner task is missing: {', '.join(missing)}")
    allowed = task["allowed_files"]
    if not isinstance(allowed, list) or not 1 <= len(allowed) <= max_files:
        count = len(allowed) if isinstance(allowed, list) else "a non-list value"
        raise WorkflowError(f"Planner must allow 1-{max_files} files; received {count}")
    for raw in allowed:
        path_is_allowed(str(raw), [str(raw)], project_root)
    steps = task["required_work"]
    if not isinstance(steps, list) or not 1 <= len(steps) <= max_work:
        raise WorkflowError(f"Planner must provide 1-{max_work} required-work items")
    if task.get("acceptance") is not None:
        validate_acceptance(task["acceptance"])
    worker = worker_name(task)
    # specialists=None skips the check: a resumed task keeps running even if its Specialist was removed (see find_specialist)
    if worker and specialists is not None and worker.lower() not in {title.lower() for title in specialists}:
        known = ", ".join(specialists) or "none"
        raise WorkflowError(f'Planner task names unknown worker {worker!r}; Specialists: {known}. Omit "worker" for the Implementer')


def request_plan(
    config: dict[str, Any], system: str, user: str, project_root: Path, interactive: bool, max_repairs: int = 2
) -> dict[str, Any]:
    prompt = user
    response = ask_once(config, "planner", system, prompt, interactive)
    for repair in range(max_repairs + 1):
        plan, response = extract_json_with_repair(config, "planner", response, interactive, system=system, user=prompt)
        try:
            if plan.get("status") == "COMPLETE":
                return plan
            if plan.get("status") != "TASK" or not isinstance(plan.get("task"), dict):
                raise WorkflowError("Planner must return status TASK with a task, or status COMPLETE")
            validate_task(plan["task"], project_root, task_limits(config), specialist_titles(config))
            plan["task"].pop("worker_id", None)
            spec = find_specialist(plan["task"], custom_workers(config))
            if spec:
                plan["task"]["worker_id"] = spec["id"]
            return plan
        except WorkflowError as exc:
            if repair == max_repairs:
                raise WorkflowError(f"Planner contract remained invalid after {max_repairs} corrections: {exc}") from exc
            print(f"[planner] invalid task: {exc}; requesting correction ({repair + 1}/{max_repairs}) ...")
            prompt = (
                f"{user}\n\nCORRECTION REQUIRED\nThe previous JSON task was rejected: {exc}\n"
                f"Return a corrected response that follows every schema and scope limit.\n\nPREVIOUS RESPONSE\n{response[-20_000:]}"
            )
            response = ask_once(config, "planner", system, prompt, interactive)
    raise AssertionError("unreachable")


UNREADABLE_VERDICT = "The reviewer's verdict could not be read; no verdict was given for this round."


def extract_review_with_repair(
    config: dict[str, Any], system: str, user: str, response: str, interactive: bool, max_repairs: int = 2,
    role: str = "reviewer",
) -> tuple[dict[str, Any], str]:
    """A verdict that stays unreadable after every correction becomes a FAIL round (never PASS), so the review
    loop goes on; the reviewer's raw reply is kept in the returned text for REVIEW.md."""
    prompt = user
    for repair in range(max_repairs + 1):
        try:
            review, response = extract_json_with_repair(config, role, response, interactive, system=system, user=prompt)
        except WorkflowError:
            review = {}  # still no JSON after repairs and fresh attempts; `response` is the reviewer's reply
        verdict = str(review.get("verdict", "")).upper()
        if verdict in {"PASS", "FAIL"}:
            return review, response
        if repair == max_repairs or not review:
            print(f"[{display_name(config, role)}] verdict unreadable; recording this round as FAIL")
            failed = {"verdict": "FAIL", "defects": [{"file": "", "evidence": UNREADABLE_VERDICT}]}
            return failed, f"{json.dumps(failed, indent=2)}\n\nUNREADABLE REVIEWER REPLY\n{answer_text({'content': response})[-4000:]}"
        print(f"[{display_name(config, role)}] invalid verdict; requesting correction ({repair + 1}/{max_repairs}) ...")
        prompt = (
            f"{user}\n\nCORRECTION REQUIRED\nThe previous response must contain verdict PASS or FAIL.\n"
            f"Return corrected JSON only.\n\nPREVIOUS RESPONSE\n{response[-20_000:]}"
        )
        response = ask_once(config, role, system, prompt, interactive)
    raise AssertionError("unreachable")


def task_markdown(task: dict[str, Any]) -> str:
    def bullets(items: list[Any]) -> str:
        return "\n".join(f"- {item}" for item in items)
    acceptance = "".join(
        f"\n{n}. `{json.dumps(step, ensure_ascii=False)}`" for n, step in enumerate(task.get("acceptance") or [], 1)
    )
    return (
        f"# {task['id']}\n\n## Outcome\n{task['outcome']}\n\n"
        f"## Allowed files\n{bullets(task['allowed_files'])}\n\n"
        f"## Required work\n{bullets(task['required_work'])}\n\n"
        f"## Validation\n{bullets(task['validation'])}\n\n"
        f"## Stop condition\n{task['stop_condition']}"
    ) + (
        "\n\n## Acceptance steps\nThe behaviour check runs these steps in order in a headless browser, on a fresh page with "
        "empty localStorage; the task passes only if every step does. The ids and selectors they use are part of the "
        f"contract: the page must have them.{acceptance}"
    ) * bool(acceptance)


def snapshot(project_root: Path, allowed: list[str]) -> dict[str, str | None]:
    result: dict[str, str | None] = {}
    for pattern in allowed:
        matches = list(project_root.glob(pattern)) if any(ch in pattern for ch in "*?[") else [project_root / pattern]
        for path in matches:
            if path.is_file():
                relative = path.resolve().relative_to(project_root.resolve()).as_posix()
                if relative not in PROTECTED_FILES:
                    result[relative] = load_text(path)
        if not matches:
            result[pattern] = None
        elif not any(path.is_file() for path in matches) and not any(ch in pattern for ch in "*?["):
            result[pattern] = None
    return result


def make_diff(before: dict[str, str | None], after: dict[str, str | None]) -> str:
    chunks: list[str] = []
    for name in sorted(set(before) | set(after)):
        old = (before.get(name) or "").splitlines(keepends=True)
        new = (after.get(name) or "").splitlines(keepends=True)
        chunks.extend(difflib.unified_diff(old, new, fromfile=f"a/{name}", tofile=f"b/{name}"))
    return "".join(chunks) or "(no file changes)\n"


def completion_marker(token: str) -> str:
    return f"<!-- ao-completion: {token} -->"


def append_passed_state(task: dict[str, Any], result: str, project_root: Path, token: str | None = None) -> None:
    state = load_text(project_root / "STATE.md").rstrip()
    stamp = dt.datetime.now().astimezone().isoformat(timespec="seconds")
    marker = f"\n{completion_marker(token)}" if token else ""
    entry = f"\n\n## Passed: {task['id']}{marker}\n- Time: {stamp}\n- Outcome: {task['outcome']}\n- Implementer: {result.strip()}"
    write_atomic(project_root / "STATE.md", state + entry)


def project_configuration_context(project_root: Path) -> str:
    excluded = {".git", ".hg", ".svn", ".venv", "node_modules", "bin", "obj", "dist", "build"}
    manifest_names = {
        "package.json", "pyproject.toml", "setup.cfg", "setup.py", "requirements.txt",
        "cargo.toml", "go.mod", "cmakelists.txt", "makefile", "pom.xml", "build.gradle",
        "build.gradle.kts", "gradlew", "gradlew.bat",
    }
    inventory: list[str] = []
    manifests: list[Path] = []
    for current, directories, files in os.walk(project_root):
        relative_directory = Path(current).relative_to(project_root)
        depth = len(relative_directory.parts)
        directories[:] = [] if depth >= 2 else [name for name in directories if name.lower() not in excluded]
        for name in sorted(files):
            relative = (relative_directory / name).as_posix()
            if relative not in PROTECTED_FILES and len(inventory) < 300:
                inventory.append(relative)
            lowered = name.lower()
            if depth <= 2 and (lowered in manifest_names or Path(name).suffix.lower() in {".sln", ".csproj", ".fsproj", ".vbproj"}):
                manifests.append(Path(current) / name)

    sections = ["PROJECT FILE INVENTORY\n" + "\n".join(inventory)]
    remaining = 40_000
    for path in manifests:
        content = load_text(path)[:12_000]
        if not content or remaining <= 0:
            continue
        content = content[:remaining]
        remaining -= len(content)
        sections.append(f"BUILD MANIFEST: {path.relative_to(project_root).as_posix()}\n{content}")
    return "\n\n".join(sections)


def apply_project_configuration(config: dict[str, Any], response: str) -> dict[str, Any]:
    payload = extract_json(response)
    commands = payload.get("validation_commands")
    if not isinstance(commands, dict) or len(commands) > 12:
        raise WorkflowError("Configurator must return validation_commands with at most 12 entries")
    blocked_executables = {"cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe", "bash", "sh", "wsl", "wsl.exe"}
    blocked_arguments = {"-c", "/c", "-command", "--command", "-e", "--eval"}
    validated: dict[str, list[str]] = {}
    for name, command in commands.items():
        if not isinstance(name, str) or not name.strip() or len(name) > 60:
            raise WorkflowError("Validation command names must be non-empty strings of at most 60 characters")
        if not isinstance(command, list) or not 1 <= len(command) <= 24 or not all(isinstance(item, str) and item for item in command):
            raise WorkflowError(f"Validation command {name!r} must be an argument array of 1-24 strings")
        executable = Path(command[0]).name.lower()
        arguments = {item.lower() for item in command[1:]}
        if executable in blocked_executables or arguments & blocked_arguments:
            raise WorkflowError(f"Validation command {name!r} uses a shell or inline-code option")
        if any(len(item) > 500 for item in command):
            raise WorkflowError(f"Validation command {name!r} contains an overlong argument")
        validated[name.strip()] = command

    updated = dict(config)
    updated["validation_commands"] = validated
    updated["project_configured"] = True
    return updated


def configure_project(config: dict[str, Any], project_root: Path, project: str, interactive: bool) -> dict[str, Any]:
    context = project_configuration_context(project_root)
    fingerprint = hashlib.sha256((project + "\n" + context).encode("utf-8")).hexdigest()
    commands = config.get("validation_commands")
    if config.get("project_configured") is True and (
        (isinstance(commands, dict) and bool(commands)) or config.get("configuration_fingerprint") == fingerprint
    ):
        return config
    print("[configurator] reading PROJECT.md and build manifests ...")
    user = f"PROJECT\n{project}\n\n{context}"
    system = load_text(ROOT / "prompts" / "configurator.txt")
    response = ask_once(config, "planner", system, user, interactive)
    _, response = extract_json_with_repair(config, "planner", response, interactive, system=system, user=user)
    configured = apply_project_configuration(config, response)
    configured["configuration_fingerprint"] = fingerprint
    write_atomic(project_root / "config.json", json.dumps(configured, indent=2))
    names = ", ".join(configured["validation_commands"]) or "none"
    print(f"[configurator] saved validation commands: {names}")
    return configured


def write_atomic(path: Path, text: str) -> None:
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(text.rstrip() + "\n", encoding="utf-8")
    os.replace(tmp, path)


def write_handoff(project_root: Path, checkpoint: dict[str, Any] | None, total_rounds: int = 0) -> None:
    if checkpoint:
        task = checkpoint["task"]
        phase = {
            "reviewing": "Reviewer checking", "extra_reviewing": "Extra reviewers checking", "passed": "Passed, recording result", "blocked": "Blocked, recording issue",
        }.get(checkpoint["phase"], "Implementer working")
        label = {"passed": "Implementer result", "blocked": "Final review"}.get(checkpoint["phase"], "Last reviewer feedback")
        feedback = str(checkpoint["reviewer_feedback"])[:2000]
        text = (
            f"# Handoff\n\n## In progress: {task['id']}\n- Outcome: {task['outcome']}\n"
            f"- Allowed files: {', '.join(str(f) for f in task['allowed_files'])}\n"
            f"- Phase: {phase}\n- Review round {int(checkpoint['round']) + 1} of {total_rounds}\n"
            f"- Updated: {checkpoint['updated']}\n\n## {label}\n{feedback}\n\n"
            "The next run resumes this task automatically. Delete .ao_checkpoint.json to discard it and plan a new task."
        )
    else:
        state = load_text(project_root / "STATE.md")
        # Real entries carry a completion marker under the heading; a "## Passed:" line inside an implementer
        # result body never does. Older entries without markers are only used when no marked entry exists.
        passed = (re.findall(r"^## Passed: (.+?)[ \t]*\r?\n<!-- ao-completion: [0-9a-f]+ -->", state, re.M)
                  or re.findall(r"^## Passed: (.+)$", state, re.M))
        text = "# Handoff\n\nNo task in progress." + (f"\nLast passed task: {passed[-1].strip()}" if passed else "")
    path = project_root / HANDOFF_FILE
    if not path.is_file() or load_text(path) != text.rstrip() + "\n":
        write_atomic(path, text)


def save_checkpoint(
    project_root: Path, task: dict[str, Any], before: dict[str, str | None], review_round: int,
    feedback: str, phase: str, total_rounds: int,
) -> str | None:
    """Persist the checkpoint; a passed/blocked save mints and returns the completion token for that outcome."""
    checkpoint = {
        "version": 1, "task": task, "before": before, "round": review_round, "reviewer_feedback": feedback,
        "phase": phase, "updated": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
    }
    token = uuid.uuid4().hex if phase in COMPLETION_PHASES else None
    if token:
        checkpoint["completion"] = token
    write_atomic(project_root / CHECKPOINT_FILE, json.dumps(checkpoint, indent=2, ensure_ascii=False))
    write_handoff(project_root, checkpoint, total_rounds)
    return token


def clear_checkpoint(project_root: Path) -> None:
    (project_root / CHECKPOINT_FILE).unlink(missing_ok=True)
    write_handoff(project_root, None)


def load_checkpoint(
    project_root: Path, limits: tuple[int, int], total_rounds: int,
) -> dict[str, Any] | None:
    """Return a valid checkpoint to resume, or None (warning and deleting a corrupt one)."""
    path = project_root / CHECKPOINT_FILE
    if not path.is_file():
        return None
    try:
        data = json.loads(load_text(path))
        if not isinstance(data, dict) or data.get("version") != 1:
            raise WorkflowError("unsupported version")
        if not isinstance(data["task"], dict) or not isinstance(data["before"], dict):
            raise WorkflowError("bad task or snapshot")
        if not all(isinstance(k, str) and (v is None or isinstance(v, str)) for k, v in data["before"].items()):
            raise WorkflowError("bad snapshot entries")
        if not isinstance(data["reviewer_feedback"], str) or data["phase"] not in CHECKPOINT_PHASES:
            raise WorkflowError("bad feedback or phase")
        if data["phase"] in COMPLETION_PHASES:
            # Only what recording needs: limits may have changed since the task finished.
            if not all(isinstance(data["task"].get(key), (str, int)) and data["task"].get(key) != "" for key in ("id", "outcome")):
                raise WorkflowError("completed task lacks id or outcome")
            if not isinstance(data.get("completion"), str) or not data["completion"]:
                raise WorkflowError("missing completion token")
            return data
        if not isinstance(data["round"], int) or not 0 <= data["round"] < total_rounds:
            raise WorkflowError("round out of range")
        validate_task(data["task"], project_root, limits, None)
        return data
    except (WorkflowError, KeyError, ValueError) as exc:
        print(f"[resume] discarding invalid {CHECKPOINT_FILE}: {exc}")
        clear_checkpoint(project_root)
        return None


def complete_task(task: dict[str, Any], phase: str, text: str, project_root: Path, token: str) -> None:
    """Idempotently record a finished task ("passed" or "blocked"), then clear the checkpoint.

    The token is minted when the completion checkpoint is saved and written into the entry as a marker, so a
    re-run after a kill finds its own entry and skips the append; ids are never used to detect duplicates.
    """
    marker = completion_marker(token)
    if phase == "passed":
        if marker not in load_text(project_root / "STATE.md"):
            append_passed_state(task, text, project_root, token)
    else:
        issues_path = project_root / "ISSUES.md"
        issues = load_text(issues_path)
        if marker not in issues:
            write_atomic(issues_path, issues + f"\n\n## {task['id']} blocked\n{marker}\n{text}")
    clear_checkpoint(project_root)


def run_extra_reviewers(
    config: dict[str, Any], base_prompt: str, extras: list[dict[str, Any]], reviewer_input: str,
    project_root: Path, allowed: list[str], interactive: bool,
) -> str:
    """Run every Extra reviewer in config order. Returns "" when all PASS, else a FAIL review (JSON) whose
    defects are prefixed with the failing reviewer's title; all reviewers run so one round carries all feedback."""
    defects: list[Any] = []
    for worker in extras:
        system = role_prompt(base_prompt, worker)
        text = run_agent(config, worker["id"], system, reviewer_input, project_root, allowed, False, interactive)
        review, _ = extract_review_with_repair(config, system, reviewer_input, text, interactive, role=worker["id"])
        verdict = str(review.get("verdict", "")).upper()
        print(f"[{worker['title']}] {verdict}")
        if verdict == "FAIL":
            found = review.get("defects")
            for item in (found if isinstance(found, list) and found else [{}]):
                item = item if isinstance(item, dict) else {"defect": str(item)}
                defects.append({**item, "defect": f"{worker['title']}: {item.get('defect') or 'failed the task without listing a defect'}"})
    return json.dumps({"verdict": "FAIL", "defects": defects}, indent=2, ensure_ascii=False) if defects else ""


def run_review_loop(
    config: dict[str, Any], prompts: dict[str, str], task: dict[str, Any], before: dict[str, str | None],
    start_round: int, reviewer_feedback: str, phase: str, max_review_rounds: int, project_root: Path, interactive: bool,
) -> None:
    """Implement/review until PASS (state appended, checkpoint cleared) or raise WorkflowError when blocked.

    Resume: rounds before start_round count as used. With phase "reviewing" the implementer is re-run for
    start_round only if RESULT.md is missing; otherwise the existing RESULT.md goes straight to the reviewer.
    """
    allowed = [str(item) for item in task["allowed_files"]]
    total = max_review_rounds + 1
    workers = custom_workers(config)
    specialist = find_specialist(task, workers)
    extras = [w for w in workers if w["kind"] == "reviewer"]
    for review_round in range(start_round, total):
        resumed = phase in ("reviewing", "extra_reviewing") and review_round == start_round and (project_root / "RESULT.md").is_file()
        if resumed:
            result = load_text(project_root / "RESULT.md").strip()
        else:
            save_checkpoint(project_root, task, before, review_round, reviewer_feedback, "implementing", total)
            current = snapshot(project_root, allowed)
            prior = [n for n in current if current[n] != before.get(n)]
            prior_note = (
                f"PRIOR WORK\n{', '.join(prior)} already contain unreviewed changes from an earlier attempt. Finish or fix "
                "them; if the task is already complete, report so without rewriting the files.\n\n"
            ) if prior else ""
            implementer_input = (
                f"TASK\n{load_text(project_root / 'TASK.md')}\n\nVERIFIED STATE\n{load_text(project_root / 'STATE.md')}\n\n"
                f"{prior_note}CURRENT ALLOWED FILES\n{allowed_files_context(current)}\n\n"
                f"REVIEWER FEEDBACK\n{reviewer_feedback}"
            )
            impl_role, impl_system = ("implementer", prompts["implementer"]) if not specialist else (
                specialist["id"], role_prompt(prompts["implementer"], specialist))
            result = run_agent(config, impl_role, impl_system, implementer_input, project_root, allowed, True, interactive, before)
            save_text(project_root / "RESULT.md", result)
        after = snapshot(project_root, allowed)
        diff = make_diff(before, after)
        defects, syntax_line = syntax_gate(project_root, allowed, before)
        gate = "syntax"
        if not defects:
            defects, behaviour_block = behaviour_gate(project_root, config, task.get("acceptance") or [])
            gate = "behaviour"
        if defects:
            review_text = json.dumps({"verdict": "FAIL", "defects": defects}, indent=2)
            save_text(project_root / "REVIEW.md", review_text)
            verdict = "FAIL"
            emit("verdict", id=str(task["id"]), verdict=verdict, round=review_round + 1)
            print(f"[{gate}] round {review_round + 1} failed the {gate} check; reviewer skipped")
        else:
            behaviour_part = f"{behaviour_block}\n\n" if behaviour_block else ""
            reviewer_input = (
                f"ORIGINAL TASK\n{load_text(project_root / 'TASK.md')}\n\nIMPLEMENTER RESULT\n{result}\n\n"
                f"{syntax_line}\n\n{behaviour_part}CHANGED FILE DIFF\n{diff}\n\nReview round: {review_round + 1}"
            )
            if resumed and phase == "extra_reviewing":
                verdict, review_text = "PASS", load_text(project_root / "REVIEW.md")  # the main Reviewer already passed this round
            else:
                save_checkpoint(project_root, task, before, review_round, reviewer_feedback, "reviewing", total)
                review_text = run_agent(config, "reviewer", prompts["reviewer"], reviewer_input, project_root, allowed, False, interactive)
                review, review_text = extract_review_with_repair(
                    config, prompts["reviewer"], reviewer_input, review_text, interactive
                )
                save_text(project_root / "REVIEW.md", review_text)
                verdict = str(review.get("verdict", "")).upper()
            if verdict == "PASS" and extras:
                save_checkpoint(project_root, task, before, review_round, reviewer_feedback, "extra_reviewing", total)
                extra_failure = run_extra_reviewers(config, prompts["reviewer"], extras, reviewer_input, project_root, allowed, interactive)
                if extra_failure:
                    verdict, review_text = "FAIL", extra_failure
                    save_text(project_root / "REVIEW.md", review_text)
            emit("verdict", id=str(task["id"]), verdict=verdict, round=review_round + 1)
        if verdict == "PASS":
            token = save_checkpoint(project_root, task, before, review_round, result, "passed", total)
            complete_task(task, "passed", result, project_root, str(token))
            emit("task_end", id=str(task["id"]), status="passed")
            print(f"\n{task['id']}: PASS")
            return
        reviewer_feedback = review_text
        if review_round == max_review_rounds:
            token = save_checkpoint(project_root, task, before, review_round, review_text, "blocked", total)
            complete_task(task, "blocked", review_text, project_root, str(token))
            emit("task_end", id=str(task["id"]), status="blocked")
            raise WorkflowError(f"{task['id']} failed after {total} reviews")


def answer_text(message: dict[str, Any]) -> str:
    """The reply without reasoning (an unterminated <think> counts as reasoning)."""
    text = re.sub(r"<think>.*?</think>", "", split_reasoning(message)[1], flags=re.S)
    return re.sub(r"<think>.*\Z", "", text, flags=re.S).strip()  # only a trailing unterminated block remains


def chat_call(config: dict[str, Any], role: str, stage: str, system: str, messages: list[dict[str, str]]) -> str:
    client, sampling = role_settings(config, role)
    emit("chat_stage", role=role, stage=stage, title=client.title or role)
    model = client.ensure_model(role, False)
    return answer_text(client.complete(model, [{"role": "system", "content": system}, *messages], sampling))


def run_chat(config: dict[str, Any], request_path: Path) -> int:
    """One chat turn: a single worker, or Planner -> Implementer -> Reviewer. No tools, no workflow files."""
    request = json.loads(load_text(request_path))
    if not isinstance(request, dict):
        raise WorkflowError("Chat request must be a JSON object")
    messages = [
        {"role": m["role"], "content": str(m["content"])}
        for m in request.get("messages") or []
        if isinstance(m, dict) and m.get("role") in ("user", "assistant") and str(m.get("content") or "").strip()
    ]
    if not messages or messages[-1]["role"] != "user":
        raise WorkflowError("Chat request needs messages ending with a user message")
    chat = load_text(ROOT / "prompts" / "chat.txt").strip()
    if request.get("mode") == "multi":
        missing = [r for r in BUILTIN_ROLES if r not in (config.get("roles") or {})]
        if missing:
            raise WorkflowError(f"Multi chat needs the built-in workers; missing in config: {', '.join(missing)}")
        convo = "CONVERSATION\n" + "\n\n".join(f"{m['role'].upper()}: {m['content']}" for m in messages)
        prompt = lambda role: load_text(ROOT / "prompts" / f"chat_{role}.txt")
        plan = chat_call(config, "planner", "plan", prompt("planner"), [{"role": "user", "content": convo}])
        draft = chat_call(config, "implementer", "draft", prompt("implementer"),
                          [{"role": "user", "content": f"{convo}\n\nPLAN\n{plan}"}])
        final = chat_call(config, "reviewer", "review", prompt("reviewer"),
                          [{"role": "user", "content": f"{convo}\n\nPLAN\n{plan}\n\nDRAFT\n{draft}"}])
        if not final:
            print("[chat] the Reviewer returned nothing usable; answering with the draft")
        role, text = ("reviewer", final) if final else ("implementer", draft)
    elif request.get("mode") == "single":
        role = str(request.get("worker") or "")
        worker = next((w for w in custom_workers(config) if w["id"] == role), None)
        if role not in BUILTIN_ROLES and not worker:
            raise WorkflowError(f"Unknown chat worker {role!r}")
        if role in BUILTIN_ROLES and role not in (config.get("roles") or {}):
            raise WorkflowError(f"Chat worker {role!r} is missing from config roles")
        if worker:
            system = f"{chat}\n\n=== YOUR ROLE: {worker['title']} ===\n{worker['system_prompt'].strip()}\n"
        else:
            system = f"{chat}\n\nYou are the {role.capitalize()} worker.\n"
        text = chat_call(config, role, "answer", system, messages)
    else:
        raise WorkflowError(f"Unknown chat mode {request.get('mode')!r}")
    if not text:
        raise WorkflowError("The model returned an empty reply")
    emit("chat_reply", role=role, text=text)
    return 0


def _run() -> int:
    # Piped stdout defaults to the ANSI codepage on Windows; model names can contain non-cp1252 characters.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=ROOT, help="Project code root (default: this folder)")
    parser.add_argument("--non-interactive", action="store_true", help="Fail instead of waiting for model swaps")
    parser.add_argument("--once", action="store_true", help="Stop after one task passes")
    parser.add_argument("--events", action="store_true", help="Emit structured @@AO event lines for the launcher UI")
    parser.add_argument("--chat", type=Path, help="Answer the chat request in this JSON file instead of running the workflow")
    args = parser.parse_args()
    global PROJECT_ROOT_ARG, EVENTS, CHAT_MODE
    PROJECT_ROOT_ARG = args.project.resolve()
    EVENTS = args.events
    CHAT_MODE = args.chat is not None

    project_root = args.project.resolve()
    if not project_root.is_dir():
        raise WorkflowError(f"Project folder does not exist: {project_root}")
    if args.chat:
        config = json.loads(load_text(project_root / "config.json"))
        custom_workers(config, warn=True)
        return run_chat(config, args.chat)
    missing = sorted(name for name in WORKFLOW_FILES if not (project_root / name).is_file())
    if missing:
        raise WorkflowError(f"Project is not initialized; missing: {', '.join(missing)}")
    config = json.loads(load_text(project_root / "config.json"))
    task_limits(config)
    custom_workers(config, warn=True)
    project = load_text(project_root / "PROJECT.md").strip()
    if not project or "REPLACE THIS" in project or "[Describe the project" in project:
        raise WorkflowError("Describe the real goal in PROJECT.md before running")
    prompts = {role: load_text(ROOT / "prompts" / f"{role}.txt") for role in ("planner", "implementer", "reviewer")}
    interactive = not args.non_interactive
    max_tasks = int(config.get("max_tasks", 20))
    max_review_rounds = int(config.get("max_review_rounds", 2))

    limits = task_limits(config)
    total_rounds = max_review_rounds + 1
    remaining = max_tasks
    while remaining > 0:
        remaining -= 1
        checkpoint = load_checkpoint(project_root, limits, total_rounds)
        if checkpoint:
            task, before = checkpoint["task"], checkpoint["before"]
            start_round, reviewer_feedback, phase = checkpoint["round"], checkpoint["reviewer_feedback"], checkpoint["phase"]
            if phase in COMPLETION_PHASES:
                remaining += 1  # recording only, no model work: not a task iteration
                print(f"[resume] recording finished task {task['id']} ({phase})")
                complete_task(task, phase, reviewer_feedback, project_root, checkpoint["completion"])
                if phase == "blocked":
                    raise WorkflowError(f"{task['id']} failed after {total_rounds} reviews")
                print(f"\n{task['id']}: PASS")
                if args.once:
                    return 0
                project = load_text(project_root / "PROJECT.md").strip()
                continue
            print(f"[resume] continuing {task['id']} at review round {start_round + 1}")
            emit("task_start")
            emit("resume", id=str(task["id"]), round=start_round)
            save_text(project_root / "TASK.md", task_markdown(task))
        else:
            write_handoff(project_root, None)  # a kill inside clear_checkpoint can leave a stale handoff
            emit("task_start")  # the launcher's task span (TASK TIME/TOKENS) includes the Configurator and Planner
            config = configure_project(config, project_root, project, interactive)
            behaviour_block = behaviour_gate(project_root, config)[1].split("\nVisible elements", 1)[0]  # no text sample
            behaviour_part = f"BEHAVIOUR CHECK OF CURRENT PROJECT\n{behaviour_block}\n\n" if behaviour_block else ""
            planner_input = (
                f"PROJECT\n{project}\n\nVERIFIED STATE\n{load_text(project_root / 'STATE.md')}\n\n"
                f"{project_configuration_context(project_root).split(chr(10) * 2 + 'BUILD MANIFEST', 1)[0]}\n\n"
                f"{team_context(config)}\n\n{behaviour_part}"
                f"KNOWN ISSUES\n{load_text(project_root / 'ISSUES.md')}"
            )
            plan = request_plan(config, prompts["planner"], planner_input, project_root, interactive)
            if plan.get("status") == "COMPLETE":
                print(f"\nCOMPLETE: {plan.get('summary', 'Project goal complete')}")
                return 0
            task = plan["task"]
            save_text(project_root / "TASK.md", task_markdown(task))
            emit("task", id=str(task["id"]), outcome=str(task["outcome"]))
            before = snapshot(project_root, [str(item) for item in task["allowed_files"]])
            start_round, reviewer_feedback, phase = 0, "None; this is the first attempt.", "implementing"
            save_checkpoint(project_root, task, before, 0, reviewer_feedback, "implementing", total_rounds)
        run_review_loop(
            config, prompts, task, before, start_round, reviewer_feedback, phase, max_review_rounds, project_root, interactive
        )
        if args.once:
            return 0
        project = load_text(project_root / "PROJECT.md").strip()

    raise WorkflowError(f"Stopped at max_tasks={max_tasks}; raise the limit only after checking STATE.md")


PROJECT_ROOT_ARG: Path = ROOT
CHAT_MODE = False  # --chat: models stay loaded after the turn; the launcher unloads them when the app closes


# Models this run loaded itself (path -> client). A model the user loaded in Unsloth Desktop is not in
# here, unless this run reloads it for a role's load settings; then it is added and unloaded at exit.
LOADED_BY_RUN: dict[str, "ApiClient"] = {}


def unload_models() -> None:
    """Free VRAM/RAM: unload only the models this run loaded, ignoring failures."""
    for path, client in LOADED_BY_RUN.items():
        try:
            client._request_at(client.management_url, "POST", "/api/inference/unload", {"model_path": path})
            print(f"[{client.title or client.role}] unloaded {path}")
        except WorkflowError:
            pass  # already unloaded or server unreachable
    LOADED_BY_RUN.clear()


def main() -> int:
    try:
        return _run()
    finally:
        if not CHAT_MODE:
            unload_models()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nStopped by user.", file=sys.stderr)
        raise SystemExit(130)
    except (WorkflowError, json.JSONDecodeError, OSError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)
