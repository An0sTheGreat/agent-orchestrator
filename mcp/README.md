# Agent Orchestrator images for Claude

`ao_images.py` is an MCP server that lets Claude make pictures with AO's local image models and show them in its reply.
It uses AO's local API, so turn that on first: **Settings > Connections > Local API for my apps**. AO must be running.

Tools:
- `generate_image(prompt, negative_prompt?, size?, n?, steps?, cfg_scale?, seed?, model?)`: makes the pictures, returns
  them to Claude (a JPEG copy when the PNG is large) and saves the PNGs in `Pictures\Agent Orchestrator`.
- `list_image_models()`: the ids for `model`.

The key and port are read from AO's own files on each call, so **New key** in AO needs no change here. Standard library
only; Pillow, if installed, shrinks large pictures before they are sent to Claude.

## Claude desktop app
In `%APPDATA%\Claude\claude_desktop_config.json`, under `mcpServers`:

```json
"agent-orchestrator-images": {
  "command": "C:\\Python314\\python.exe",
  "args": ["-u", "Y:\\Programs\\Agent Setup\\mcp\\ao_images.py"]
}
```

Then quit Claude fully (tray icon > Quit) and open it again.

## Claude Code
```powershell
claude mcp add agent-orchestrator-images --scope user -- C:\Python314\python.exe -u "Y:\Programs\Agent Setup\mcp\ao_images.py"
```
