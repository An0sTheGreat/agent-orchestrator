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
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import time
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parent
WORKFLOW_FILES = {"PROJECT.md", "STATE.md", "TASK.md", "RESULT.md", "REVIEW.md", "ISSUES.md", "config.json"}


class WorkflowError(RuntimeError):
    pass


def load_text(path: Path) -> str:
    return path.read_text(encoding="utf-8") if path.exists() else ""


def save_text(path: Path, text: str) -> None:
    path.write_text(text.rstrip() + "\n", encoding="utf-8")


def extract_json(text: str) -> dict[str, Any]:
    """Accept plain JSON or one fenced JSON object; reject ambiguous prose."""
    stripped = text.strip()
    if stripped.startswith("```"):
        lines = stripped.splitlines()
        if len(lines) >= 3 and lines[-1].strip() == "```":
            stripped = "\n".join(lines[1:-1])
            if stripped.lstrip().startswith("json"):
                stripped = stripped.lstrip()[4:].lstrip("\r\n")
    try:
        value = json.loads(stripped)
    except json.JSONDecodeError as exc:
        raise WorkflowError(f"Model did not return valid JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise WorkflowError("Model response must be a JSON object")
    return value


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
    if normalized in WORKFLOW_FILES or not any(fnmatch.fnmatch(normalized, pattern) for pattern in allowed):
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
        models = payload.get("data", [])
        if not models:
            raise WorkflowError("The API returned no loaded models")
        return str(models[0]["id"])

    def ensure_model(self, role: str, interactive: bool) -> str:
        load_error = ""
        while True:
            try:
                model = self.model_id()
            except WorkflowError:
                model = ""
            if model and (not self.expected or self.expected in model.lower()):
                print(f"[{role}] {model}")
                emit("model", role=role, id=model)
                return model
            if self.management_url and self.auto_load:
                try:
                    print(f"[{role}] loading {self.auto_load['model_path']} ...")
                    emit("loading", role=role, path=self.auto_load["model_path"])
                    self._request_at(self.management_url, "POST", "/api/inference/load", self.auto_load)
                    model = self.model_id()
                    if not self.expected or self.expected in model.lower():
                        print(f"[{role}] {model}")
                        emit("model", role=role, id=model)
                        return model
                    load_error = f"load completed but API reports {model!r}"
                except WorkflowError as exc:
                    load_error = str(exc)
            expected = self.expected or "the configured model"
            if not interactive:
                raise WorkflowError(f"{role}: expected {expected!r}, loaded {model or 'nothing'!r}; {load_error}")
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
        started = time.time()
        streamed = False
        if EVENTS:  # the launcher renders output as it is generated
            payload = self._stream_chat(body)
            streamed = payload.pop("_streamed", False)
        else:
            payload = self._request("POST", "/chat/completions", body)
        try:
            message = payload["choices"][0]["message"]
        except (KeyError, IndexError, TypeError) as exc:
            raise WorkflowError(f"Unexpected API response: {payload}") from exc
        if EVENTS:
            self._emit_turn(message, payload, started, streamed)
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
        return {"choices": [{"message": message}], "usage": usage, "_streamed": True}

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


def role_settings(config: dict[str, Any], role: str) -> tuple[ApiClient, dict[str, Any]]:
    settings = config["roles"][role]
    sampling = dict(settings.get("sampling", {}))
    sampling.update(sampling.pop("extra_body", {}))
    client = ApiClient(settings)
    client.role = role
    return client, sampling


def ask_once(config: dict[str, Any], role: str, system: str, user: str, interactive: bool) -> str:
    client, sampling = role_settings(config, role)
    model = client.ensure_model(role, interactive)
    message = client.complete(model, [{"role": "system", "content": system}, {"role": "user", "content": user}], sampling)
    return str(message.get("content") or "")


def extract_json_with_repair(
    config: dict[str, Any], role: str, response: str, interactive: bool, max_repairs: int = 2
) -> tuple[dict[str, Any], str]:
    for repair in range(max_repairs + 1):
        try:
            return extract_json(response), response
        except WorkflowError as exc:
            if repair == max_repairs:
                raise
            print(f"[{role}] invalid JSON; requesting correction ({repair + 1}/{max_repairs}) ...")
            response = ask_once(
                config,
                role,
                "Repair malformed JSON. Return only one valid JSON object with the same intended data. Do not add commentary or markdown fences.",
                f"PARSER ERROR\n{exc}\n\nMALFORMED RESPONSE\n{response[-20_000:]}",
                interactive,
            )


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
            return "ERROR: file does not exist"
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


def run_agent(
    config: dict[str, Any],
    role: str,
    system: str,
    user: str,
    project_root: Path,
    allowed: list[str],
    writable: bool,
    interactive: bool,
) -> str:
    client, sampling = role_settings(config, role)
    model = client.ensure_model(role, interactive)
    messages: list[dict[str, Any]] = [{"role": "system", "content": system}, {"role": "user", "content": user}]
    tools = IMPLEMENTER_TOOLS if writable else REVIEWER_TOOLS
    max_steps = int(config.get("max_tool_steps", 30))
    validations = config.get("validation_commands", {})
    tool_names = {tool["function"]["name"] for tool in tools}
    ran_tools = False
    nudges = 0
    for _ in range(max_steps):
        response = client.complete(model, messages, sampling, tools)
        messages.append(response)
        calls = response.get("tool_calls") or text_tool_calls(str(response.get("content") or ""), tool_names)
        if not calls:
            if writable and not ran_tools and nudges < 2:
                nudges += 1
                print(f"[{role}] no tool was executed; requesting real tool calls ({nudges}/2) ...")
                messages.append({"role": "user", "content": "No file was changed. Use the provided tool-calling interface to write the files; do not print JSON."})
                continue
            if writable and not ran_tools:
                raise WorkflowError(f"{role} never executed a tool; no files were written")
            return str(response.get("content") or "")
        native = bool(response.get("tool_calls"))
        results = []
        for call in calls:
            try:
                function = call["function"]
                arguments = json.loads(function.get("arguments") or "{}")
                result = execute_tool(function["name"], arguments, project_root, allowed, validations, writable)
                emit("tool", role=role, name=function["name"], args=function.get("arguments") or "", result=result[:6000])
                ran_tools = ran_tools or (function["name"] in {"write_file", "replace_text"} and result == "OK")
            except (KeyError, TypeError, json.JSONDecodeError, WorkflowError) as exc:
                result = f"ERROR: {exc}"
                emit("tool", role=role, name=str(call.get("function", {}).get("name", "?")), args="", result=result)
            if native:
                messages.append({"role": "tool", "tool_call_id": call.get("id", "tool"), "content": result})
            else:
                results.append(f"{call['function']['name']}: {result}")
        if not native:
            messages.append({"role": "user", "content": "TOOL RESULTS\n" + "\n".join(results)})
    raise WorkflowError(f"{role} exceeded max_tool_steps={max_steps}")


def validate_task(task: dict[str, Any], project_root: Path) -> None:
    required = ["id", "outcome", "allowed_files", "required_work", "validation", "stop_condition"]
    missing = [name for name in required if not task.get(name)]
    if missing:
        raise WorkflowError(f"Planner task is missing: {', '.join(missing)}")
    allowed = task["allowed_files"]
    if not isinstance(allowed, list) or not 1 <= len(allowed) <= 3:
        count = len(allowed) if isinstance(allowed, list) else "a non-list value"
        raise WorkflowError(f"Planner must allow 1-3 files; received {count}")
    for raw in allowed:
        path_is_allowed(str(raw), [str(raw)], project_root)
    steps = task["required_work"]
    if not isinstance(steps, list) or not 1 <= len(steps) <= 7:
        raise WorkflowError("Planner must provide 1-7 required-work items")


def request_plan(
    config: dict[str, Any], system: str, user: str, project_root: Path, interactive: bool, max_repairs: int = 2
) -> dict[str, Any]:
    response = ask_once(config, "planner", system, user, interactive)
    for repair in range(max_repairs + 1):
        plan, response = extract_json_with_repair(config, "planner", response, interactive)
        try:
            if plan.get("status") == "COMPLETE":
                return plan
            if plan.get("status") != "TASK" or not isinstance(plan.get("task"), dict):
                raise WorkflowError("Planner must return status TASK with a task, or status COMPLETE")
            validate_task(plan["task"], project_root)
            return plan
        except WorkflowError as exc:
            if repair == max_repairs:
                raise WorkflowError(f"Planner contract remained invalid after {max_repairs} corrections: {exc}") from exc
            print(f"[planner] invalid task: {exc}; requesting correction ({repair + 1}/{max_repairs}) ...")
            response = ask_once(
                config,
                "planner",
                system,
                f"{user}\n\nCORRECTION REQUIRED\nThe previous JSON task was rejected: {exc}\n"
                f"Return a corrected response that follows every schema and scope limit.\n\nPREVIOUS RESPONSE\n{response[-20_000:]}",
                interactive,
            )
    raise AssertionError("unreachable")


def extract_review_with_repair(
    config: dict[str, Any], system: str, user: str, response: str, interactive: bool, max_repairs: int = 2
) -> tuple[dict[str, Any], str]:
    for repair in range(max_repairs + 1):
        review, response = extract_json_with_repair(config, "reviewer", response, interactive)
        verdict = str(review.get("verdict", "")).upper()
        if verdict in {"PASS", "FAIL"}:
            return review, response
        if repair == max_repairs:
            raise WorkflowError(f"Reviewer verdict remained invalid after {max_repairs} corrections")
        print(f"[reviewer] invalid verdict; requesting correction ({repair + 1}/{max_repairs}) ...")
        response = ask_once(
            config,
            "reviewer",
            system,
            f"{user}\n\nCORRECTION REQUIRED\nThe previous response must contain verdict PASS or FAIL.\n"
            f"Return corrected JSON only.\n\nPREVIOUS RESPONSE\n{response[-20_000:]}",
            interactive,
        )
    raise AssertionError("unreachable")


def task_markdown(task: dict[str, Any]) -> str:
    def bullets(items: list[Any]) -> str:
        return "\n".join(f"- {item}" for item in items)
    return (
        f"# {task['id']}\n\n## Outcome\n{task['outcome']}\n\n"
        f"## Allowed files\n{bullets(task['allowed_files'])}\n\n"
        f"## Required work\n{bullets(task['required_work'])}\n\n"
        f"## Validation\n{bullets(task['validation'])}\n\n"
        f"## Stop condition\n{task['stop_condition']}"
    )


def snapshot(project_root: Path, allowed: list[str]) -> dict[str, str | None]:
    result: dict[str, str | None] = {}
    for pattern in allowed:
        matches = list(project_root.glob(pattern)) if any(ch in pattern for ch in "*?[") else [project_root / pattern]
        for path in matches:
            if path.is_file():
                relative = path.resolve().relative_to(project_root.resolve()).as_posix()
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


def append_passed_state(task: dict[str, Any], result: str, project_root: Path) -> None:
    state = load_text(project_root / "STATE.md").rstrip()
    stamp = dt.datetime.now().astimezone().isoformat(timespec="seconds")
    entry = f"\n\n## Passed: {task['id']}\n- Time: {stamp}\n- Outcome: {task['outcome']}\n- Implementer: {result.strip()}"
    save_text(project_root / "STATE.md", state + entry)


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
            if relative not in WORKFLOW_FILES and len(inventory) < 300:
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
    response = ask_once(config, "planner", load_text(ROOT / "prompts" / "configurator.txt"), user, interactive)
    _, response = extract_json_with_repair(config, "planner", response, interactive)
    configured = apply_project_configuration(config, response)
    configured["configuration_fingerprint"] = fingerprint
    save_text(project_root / "config.json", json.dumps(configured, indent=2))
    names = ", ".join(configured["validation_commands"]) or "none"
    print(f"[configurator] saved validation commands: {names}")
    return configured


def _run() -> int:
    # Piped stdout defaults to the ANSI codepage on Windows; model names can contain non-cp1252 characters.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=ROOT, help="Project code root (default: this folder)")
    parser.add_argument("--non-interactive", action="store_true", help="Fail instead of waiting for model swaps")
    parser.add_argument("--once", action="store_true", help="Stop after one task passes")
    parser.add_argument("--events", action="store_true", help="Emit structured @@AO event lines for the launcher UI")
    args = parser.parse_args()
    global PROJECT_ROOT_ARG, EVENTS
    PROJECT_ROOT_ARG = args.project.resolve()
    EVENTS = args.events

    project_root = args.project.resolve()
    if not project_root.is_dir():
        raise WorkflowError(f"Project folder does not exist: {project_root}")
    missing = sorted(name for name in WORKFLOW_FILES if not (project_root / name).is_file())
    if missing:
        raise WorkflowError(f"Project is not initialized; missing: {', '.join(missing)}")
    config = json.loads(load_text(project_root / "config.json"))
    project = load_text(project_root / "PROJECT.md").strip()
    if not project or "REPLACE THIS" in project or "[Describe the project" in project:
        raise WorkflowError("Describe the real goal in PROJECT.md before running")
    prompts = {role: load_text(ROOT / "prompts" / f"{role}.txt") for role in ("planner", "implementer", "reviewer")}
    interactive = not args.non_interactive
    max_tasks = int(config.get("max_tasks", 20))
    max_review_rounds = int(config.get("max_review_rounds", 2))

    for _ in range(max_tasks):
        config = configure_project(config, project_root, project, interactive)
        planner_input = (
            f"PROJECT\n{project}\n\nVERIFIED STATE\n{load_text(project_root / 'STATE.md')}\n\n"
            f"KNOWN ISSUES\n{load_text(project_root / 'ISSUES.md')}"
        )
        plan = request_plan(config, prompts["planner"], planner_input, project_root, interactive)
        if plan.get("status") == "COMPLETE":
            print(f"\nCOMPLETE: {plan.get('summary', 'Project goal complete')}")
            return 0
        task = plan["task"]
        save_text(project_root / "TASK.md", task_markdown(task))
        emit("task", id=str(task["id"]), outcome=str(task["outcome"]))
        allowed = [str(item) for item in task["allowed_files"]]
        before = snapshot(project_root, allowed)
        reviewer_feedback = "None; this is the first attempt."

        for review_round in range(max_review_rounds + 1):
            implementer_input = (
                f"TASK\n{load_text(project_root / 'TASK.md')}\n\nVERIFIED STATE\n{load_text(project_root / 'STATE.md')}\n\n"
                f"REVIEWER FEEDBACK\n{reviewer_feedback}"
            )
            result = run_agent(config, "implementer", prompts["implementer"], implementer_input, project_root, allowed, True, interactive)
            save_text(project_root / "RESULT.md", result)
            after = snapshot(project_root, allowed)
            diff = make_diff(before, after)
            reviewer_input = (
                f"ORIGINAL TASK\n{load_text(project_root / 'TASK.md')}\n\nIMPLEMENTER RESULT\n{result}\n\n"
                f"CHANGED FILE DIFF\n{diff}\n\nReview round: {review_round + 1}"
            )
            review_text = run_agent(config, "reviewer", prompts["reviewer"], reviewer_input, project_root, allowed, False, interactive)
            review, review_text = extract_review_with_repair(
                config, prompts["reviewer"], reviewer_input, review_text, interactive
            )
            save_text(project_root / "REVIEW.md", review_text)
            verdict = str(review.get("verdict", "")).upper()
            emit("verdict", id=str(task["id"]), verdict=verdict, round=review_round + 1)
            if verdict == "PASS":
                append_passed_state(task, result, project_root)
                print(f"\n{task['id']}: PASS")
                if args.once:
                    return 0
                break
            reviewer_feedback = review_text
            if review_round == max_review_rounds:
                issues_path = project_root / "ISSUES.md"
                save_text(issues_path, load_text(issues_path) + f"\n\n## {task['id']} blocked\n{review_text}")
                raise WorkflowError(f"{task['id']} failed after {max_review_rounds + 1} reviews")
        project = load_text(project_root / "PROJECT.md").strip()

    raise WorkflowError(f"Stopped at max_tasks={max_tasks}; raise the limit only after checking STATE.md")


PROJECT_ROOT_ARG: Path = ROOT


def unload_models() -> None:
    """Free VRAM/RAM: ask Unsloth to unload every role's model, ignoring failures."""
    try:
        config = json.loads(load_text(PROJECT_ROOT_ARG / "config.json"))
    except (OSError, ValueError):
        return
    seen = set()
    for role, settings in config.get("roles", {}).items():
        path = (settings.get("auto_load") or {}).get("model_path")
        if not path or path in seen or not settings.get("management_url"):
            continue
        seen.add(path)
        try:
            ApiClient(settings)._request_at(settings["management_url"].rstrip("/"), "POST", "/api/inference/unload", {"model_path": path})
            print(f"[{role}] unloaded {path}")
        except WorkflowError:
            pass  # not loaded or server unreachable


def main() -> int:
    try:
        return _run()
    finally:
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
