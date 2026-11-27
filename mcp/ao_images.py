"""Agent Orchestrator images for Claude: an MCP server (stdio) with a generate_image tool that sends the prompt to AO's
local API (Settings > Connections > Local API for my apps) and returns the pictures themselves, so Claude shows them in
its reply. Each picture is also saved under Pictures\\Agent Orchestrator.

The key and port are read from AO's own files each call (keys.json, DPAPI for this Windows user; settings.json), so
AO's New key needs no change here. Standard library only; Pillow, when installed, makes the copy sent to Claude a JPEG
(a PNG can be too large for a tool result). Run: python -u ao_images.py  (Claude starts it; see mcp/README.md).
"""
from __future__ import annotations

import base64
import ctypes
import ctypes.wintypes
import io
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

DATA = Path(os.environ.get("AGENT_ORCHESTRATOR_DATA") or Path(os.environ["APPDATA"]) / "Agent Orchestrator")
OUT = Path(os.environ.get("AO_IMAGES_DIR") or Path.home() / "Pictures" / "Agent Orchestrator")
KEY_NAME = "AO_LOCAL_API_KEY"
SEND_LIMIT = 900_000  # bytes of picture per image in the tool result; larger ones are re-encoded smaller

TOOLS = [
    {
        "name": "generate_image",
        "description": "Generate pictures on this PC with Agent Orchestrator's local image model and show them. "
                       "Write a detailed visual prompt. Takes from seconds to a few minutes.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "prompt": {"type": "string", "description": "What to draw: subject, style, lighting, composition."},
                "negative_prompt": {"type": "string", "description": "What to keep out of the picture."},
                "size": {"type": "string", "description": "WIDTHxHEIGHT, each 64-4096, e.g. 1024x1024 or 832x1216."},
                "n": {"type": "integer", "minimum": 1, "maximum": 4, "description": "How many pictures (default 1)."},
                "steps": {"type": "integer", "minimum": 1, "maximum": 150},
                "cfg_scale": {"type": "number", "minimum": 0, "maximum": 30},
                "seed": {"type": "integer", "description": "Fixed seed to repeat a picture; omit for a random one."},
                "model": {"type": "string", "description": "An image model id from list_image_models; omit for AO's default."},
            },
            "required": ["prompt"],
        },
    },
    {
        "name": "list_image_models",
        "description": "List the image models Agent Orchestrator can generate with (ids for generate_image's model).",
        "inputSchema": {"type": "object", "properties": {}},
    },
]


class _Blob(ctypes.Structure):
    _fields_ = [("cbData", ctypes.wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


def unprotect(data: bytes) -> bytes:
    """Windows DPAPI (CurrentUser), as AO's ApiKeys.Protect stores keys."""
    blob_in = _Blob(len(data), ctypes.cast(ctypes.create_string_buffer(data, len(data)), ctypes.POINTER(ctypes.c_char)))
    blob_out = _Blob()
    if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out)):
        raise RuntimeError("could not read AO's API key (keys.json belongs to another Windows user?)")
    try:
        return ctypes.string_at(blob_out.pbData, blob_out.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(blob_out.pbData)


def api_key() -> str:
    try:
        root = json.loads((DATA / "keys.json").read_text(encoding="utf-8"))
        var = (root.get("vars") or {}).get(KEY_NAME) or {}
        stored = (var.get("keys") or {}).get(var.get("active") or "")
    except (OSError, ValueError) as exc:
        raise RuntimeError(f"could not read {DATA / 'keys.json'}: {exc}") from exc
    if not stored:
        raise RuntimeError("AO has no local API key yet: turn on Settings > Connections > Local API for my apps")
    return unprotect(base64.b64decode(stored)).decode("utf-8")


def base_url() -> str:
    try:
        settings = json.loads((DATA / "settings.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        settings = {}
    if settings.get("LocalApi") is False:
        raise RuntimeError("AO's local API is off: turn on Settings > Connections > Local API for my apps")
    return f"http://127.0.0.1:{int(settings.get('LocalApiPort') or 8830)}"


def call(method: str, path: str, body: dict | None = None, timeout: float = 60) -> dict:
    request = urllib.request.Request(base_url() + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                     headers={"Authorization": f"Bearer {api_key()}", "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as reply:
            return json.loads(reply.read())
    except urllib.error.HTTPError as exc:
        try:
            detail = json.loads(exc.read())["error"]["message"]
        except (ValueError, KeyError, TypeError):
            detail = exc.reason
        raise RuntimeError(f"AO answered {exc.code}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"AO is not reachable at {base_url()} (is Agent Orchestrator running?): {exc.reason}") from exc


def for_claude(png: bytes) -> tuple[str, str]:
    """(base64, mime type) of the copy shown in the reply: the PNG when small enough, else a JPEG (Pillow) at most 1536 px."""
    if len(png) <= SEND_LIMIT:
        return base64.b64encode(png).decode(), "image/png"
    try:
        from PIL import Image
    except ImportError:
        return base64.b64encode(png).decode(), "image/png"
    image = Image.open(io.BytesIO(png)).convert("RGB")
    image.thumbnail((1536, 1536))
    for quality in (90, 80, 70, 60):
        out = io.BytesIO()
        image.save(out, "JPEG", quality=quality)
        if out.tell() <= SEND_LIMIT:
            break
    return base64.b64encode(out.getvalue()).decode(), "image/jpeg"


def generate(args: dict) -> list[dict]:
    prompt = str(args.get("prompt") or "").strip()
    if not prompt:
        raise RuntimeError("the prompt is empty")
    body = {"prompt": prompt, "response_format": "b64_json"}
    for name in ("negative_prompt", "size", "n", "steps", "cfg_scale", "seed", "model"):
        if args.get(name) not in (None, ""):
            body[name] = args[name]
    started = time.time()
    reply = call("POST", "/v1/images/generations", body, timeout=1800)
    OUT.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    content: list[dict] = []
    saved = []
    for i, item in enumerate(reply.get("data") or []):
        png = base64.b64decode(item["b64_json"])
        path = OUT / f"{stamp}-{i + 1}.png"
        path.write_bytes(png)
        saved.append(f"{path} (seed {item.get('seed')})")
        data, mime = for_claude(png)
        content.append({"type": "image", "data": data, "mimeType": mime})
    if not content:
        raise RuntimeError("AO returned no picture")
    content.append({"type": "text", "text": f"Made {len(content)} picture(s) in {time.time() - started:.0f} s. Saved: " + "; ".join(saved)})
    return content


def list_models() -> list[dict]:
    models = [{"id": m.get("id"), "name": m.get("name")} for m in call("GET", "/v1/models").get("data") or [] if m.get("kind") == "image"]
    return [{"type": "text", "text": json.dumps(models, indent=1) if models else "AO has no image models."}]


def handle(message: dict) -> dict | None:
    method, ident = message.get("method"), message.get("id")
    if ident is None:  # a notification: nothing to answer
        return None
    if method == "initialize":
        result = {"protocolVersion": (message.get("params") or {}).get("protocolVersion") or "2025-06-18",
                  "capabilities": {"tools": {}}, "serverInfo": {"name": "agent-orchestrator-images", "version": "1.0.0"}}
    elif method == "ping":
        result = {}
    elif method == "tools/list":
        result = {"tools": TOOLS}
    elif method == "tools/call":
        params = message.get("params") or {}
        try:
            if params.get("name") == "generate_image":
                result = {"content": generate(params.get("arguments") or {})}
            elif params.get("name") == "list_image_models":
                result = {"content": list_models()}
            else:
                return {"jsonrpc": "2.0", "id": ident, "error": {"code": -32602, "message": f"unknown tool {params.get('name')!r}"}}
        except Exception as exc:  # the model sees the reason and can say it
            result = {"content": [{"type": "text", "text": f"Image generation failed: {exc}"}], "isError": True}
    else:
        return {"jsonrpc": "2.0", "id": ident, "error": {"code": -32601, "message": f"method not found: {method}"}}
    return {"jsonrpc": "2.0", "id": ident, "result": result}


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            answer = handle(json.loads(line))
        except ValueError:
            answer = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "parse error"}}
        if answer is not None:
            sys.stdout.write(json.dumps(answer) + "\n")
            sys.stdout.flush()


if __name__ == "__main__":
    main()
