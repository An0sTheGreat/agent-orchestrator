# Agent Orchestrator — local three-model orchestrator

## Introduction

Agent Orchestrator lets a set of small local language models build software together, one verified step at a time. Instead of asking one large model to write a whole project, it splits the work across three specialised roles served by [Unsloth](https://unsloth.ai) on your own machine, and it refuses to move forward until an independent reviewer approves each step.

## Description

You describe the goal, constraints, and completion criteria in a `PROJECT.md`. **Start project** (`python orchestrator.py --project <folder> [--once]`) then runs the **lead project run**: the lead agent (the Planner role, `prompts/lead_project.txt`) works through `PROJECT.md` on its own with the Code tools (list/read/search/write/replace files in the project root, `todo`, `run_checks`, `delegate` to fresh Implementer/Reviewer sub-agents), verifies with `run_checks` (which returns the error details: file, line, message, command output, clipped to about 3,000 characters) and by reading files, and records each finished item with `record_progress(item, outcome)`, which appends `## Done: <item>` (time, outcome) to `STATE.md` and sets `Last done` in `HANDOFF.md`. `finish(summary)` ends the run with exit code 0 and the `## Project complete` section in `STATE.md`. The lead's input is `PROJECT.md`, `STATE.md` and a file inventory, so a new run resumes after the recorded items. `--once` stops after the first recorded item. `max_lead_steps` (default 200) caps the lead's model calls per run (each delegation keeps its own `max_tool_steps`); hitting it, or a fatal model error, prints an `ERROR:` line and exits 1. Long runs are kept workable by the in-request context compaction. The project folder may not overlap this folder. Its file tools reach the whole project root except `api_key.txt` and `.git`. Run output shows the lead and its workers on their cards (the usual `loading`, `model`, `delta`, `usage`, `tool` events); each recorded item is a `task {id: item, outcome}` event followed by `task_end` and `task_start`, and `finish` emits `complete {summary}`.

`--pipeline` runs the legacy bounded loop instead:

`planner -> implementer tools -> reviewer -> fix/review -> verified state -> planner`

- **Planner** reads the project and the verified state, then picks one feature-sized task: one user-visible feature or behaviour across the files it needs, up to 5 files and 10 required-work items by default.
- **Implementer** writes code with file tools limited to those files, and can run only the validation commands you have approved.
- **Reviewer** is read-only. It judges the diff and validation evidence and returns `PASS` or `FAIL`.
- **Verified state** (`STATE.md`) advances only after a `PASS`, so the next task always builds on reviewed work.

The sections below on task limits, the configurator, the contract, the behaviour and code checks as gates, checkpoints and review rounds describe this legacy pipeline.

Models are loaded one at a time through Unsloth's local API, so the three roles can share a single GPU, and they are unloaded again when a run ends. A Windows launcher (`dist\Agent Orchestrator.exe`) wraps the Python engine with project setup, a live model-output view, per-model token statistics, and an execution log.

The engine uses only Python's standard library and Unsloth's OpenAI-compatible API. The implementer can only touch the files named by the planner; the per-task limits are `"task_limits": {"max_allowed_files": N, "max_required_work": M}` in the project's `config.json` (defaults 5 and 10; set them in the launcher under Project > Task limits). The reviewer is read-only. Shell access is not general-purpose: models can only run commands you explicitly name in `validation_commands`.

**Requirements:** Windows, Python 3.11+ on `PATH`, Unsloth Desktop running with an API key, and the three models installed in Unsloth.

## Windows application

Double-click `dist\Agent Orchestrator.exe` for the graphical launcher. Enter the Unsloth API key under **Options**, then start a chat in the **Code** view (see Code mode below): pick a folder, optionally add a `PROJECT.md` from the **Project** menu, and press **Start project**, which adds the workflow files automatically. **Project > Run one reviewed task, then stop** gives `--once` behavior. The key is passed only to the child process and is not saved.

The window follows the Image Parser design language: selectable themes (Dark Parchment is the default; Parchment is a light theme; Flat, Track Gold, Liquid Glass, OLED and Graphite are dark) with per-theme accent colours, a left navigation panel, and a two-column workspace with a draggable divider.

- The Code header's start button reads **Start project** until the folder has the workflow files, then *Start Task* for a project with no tasks yet, and otherwise **Resume Project — <task>**, naming the active task (from `TASK.md`) or the last one that passed (Ctrl+Enter from Run output).
- **Model output** (left column) shows each agent's thinking, tool calls with results, and responses in execution order. Thinking and tool sections are collapsible.
- **Execution log** is a collapsible panel anchored to the bottom of the left column. It is collapsed by default, scrolls on its own, and **Copy full log** copies the complete log.
- **Model details** (right column) has one card per role with the model name, weights, quantization, live tokens/sec, and total tokens for the current turn. Speed and token counts come from Unsloth's own `/api/inference/monitor` data.
- While a reply or a run is using the models, a stop icon appears to the left of **Send** (Ctrl+Shift+X). It stops the run, or the reply; stopping a run or closing the window also unloads the models from Unsloth.

The launcher starts the engine with `--events`, which makes `orchestrator.py` print structured `@@AO {json}` lines alongside its normal log output. Plain command-line runs do not emit them.

**Context compaction.** Before each model call the engine estimates the prompt (characters ÷ 3.5). When the model's context size is known and the estimate passes 90% (`COMPACT_AT`) of what the reply's `max_tokens` leaves (at least half the window), or when the API rejects a request as too long for the context (once per call, then it retries), the history is compacted: the system message, the task and the most recent steps stay, and the steps in between are summarized by one quiet call to the same model (falling back to a list of the tool calls made) and appended to the task message under `CONTEXT SUMMARY`. The run, Chat and Code then carry on with the shorter history. The engine logs `[role] context compacted: <before> → <after> tokens` and emits `{"e":"compacted","role","before","after"}`, which the launcher shows as a muted line in the reply and a note in Run output. A history too short to drop anything from still fails with the context-length error.

**Options > Model runtime** picks where local models run: **Unsloth Studio (API)** (the default) or **Local — llama.cpp (llama-server)**. **Download llama-server** fetches the latest llama.cpp Windows build from GitHub into `%APPDATA%\Agent Orchestrator\llama.cpp` (CUDA with its runtime when `nvidia-smi.exe` is found, else Vulkan, else CPU; **Update llama-server** replaces it). With the local runtime no Unsloth API key is needed; GGUF files are found in the Hugging Face cache (where Unsloth downloads them) and in the optional **Models folder**. The engine starts one llama-server on `127.0.0.1:8899`; the launcher stops it when the app closes and, with **Keep models loaded** off, after each chat turn. The runtime cannot be switched while a reply or run is in progress.

For automatic key entry, create `api_key.txt` beside the EXE or in the Agent Orchestrator root. It must contain exactly one nonblank line: the API key. A file beside the EXE takes precedence. The launcher masks the detected value and never prints it. `api_key.txt` is excluded from Git, but it is still an unencrypted credential on disk.

Initialization adds these project-local files without overwriting existing files: `PROJECT.md`, `STATE.md`, `TASK.md`, `RESULT.md`, `REVIEW.md`, `ISSUES.md`, and `config.json`. Edit the initialized project’s `PROJECT.md`; the workflow maintains the handoff files.

Worker presets, **Add worker** and each role's **Settings** work without a project: with no project `config.json` they edit the launcher's own worker config (`%APPDATA%\Agent Orchestrator\worker-config.json`, first copied from this folder's template). With one they edit the project's `config.json` and mirror the change into the worker config, and new projects start from the worker config. The template `config.json` here is never written. **Copy full log** copies the complete output buffer, including lines outside the visible scroll area.

On the first run, the Planner reads `PROJECT.md`, a bounded project-file inventory, and recognized build manifests. It then fills only `validation_commands` in the project’s `config.json` and marks the project configured. If no commands can yet be justified, the workflow checks again after project files change. Model endpoints and loading settings are copied from the protected Agent Orchestrator template and are not generated by the model. To regenerate non-empty commands after changing build systems, set `project_configured` to `false` and run again.

The EXE is self-contained for .NET, but the workflow engine still requires Python 3.11 or newer on `PATH`.

## Setup

1. Put this workflow folder anywhere. Your code may be this folder or a different folder.
2. Replace the placeholder in the target project’s `PROJECT.md` with the full desired outcome and acceptance criteria.
3. In Unsloth Desktop, create an API key under Settings > API Keys. This installation returns `401 Not authenticated` without one.
4. In the PowerShell window that will run the controller, set the key without saving it in this folder:

   ```powershell
   $env:UNSLOTH_API_KEY = 'paste-your-key-here'
   ```

5. Keep Unsloth Desktop open. The supplied config targets your installed Studio backend at port 8888 and the three model paths detected in your installation logs.
6. Review the generated project-local `validation_commands` after the first run. Each command is an argument array, not a shell string.

Run the legacy pipeline against code in this folder:

```powershell
python .\orchestrator.py --pipeline
```

Run against another initialized project (the lead project run; add `--pipeline` for the legacy pipeline); its workflow records stay in that project:

```powershell
python .\orchestrator.py --project 'Y:\path\to\project'
```

Use `--once` to complete one reviewed task. Use `--non-interactive` for three independently served endpoints; set each role's `base_url` accordingly first.

## Automatic model switching

The default config calls this installed Unsloth build's `POST /api/inference/load` route. It sequentially swaps the Planner, Implementer, and Reviewer on the same 16 GB GPU, using the settings already proven by your Unsloth logs. No three-model simultaneous load is attempted.

If automatic loading is rejected, the controller reports the API error and falls back to asking you to load the role in Desktop. `--non-interactive` instead fails immediately. To disable automatic swaps, remove `management_url` or `auto_load` from each role. When a role's model is already loaded, it is kept unless it cannot serve the role: a smaller `max_seq_length` than the role's `auto_load`, or a different nonzero `reasoning_budget` (a role with `reasoning_budget` 0 is served by a thinking load with thinking turned off per request). Sampling is sent with each request and never reloads. A model that does need it is reloaded with the role's settings, and a model reloaded this way is unloaded at exit like any other this run loaded.

Set `roles.<role>.sampling.enable_thinking` to `true` or `false` to switch thinking on or off for that role's requests; leave it out to use the model's default. If a reply is cut off by the output limit while still thinking, it is retried once with thinking off before the run fails.

### Local models (llama.cpp)

With `AGENT_ORCHESTRATOR_BACKEND=llamacpp` (the launcher sets it), every role with an `auto_load` runs on llama.cpp's `llama-server.exe` instead of Unsloth Studio; remote API roles are unchanged. No Unsloth API key is needed for these roles.

- `AGENT_ORCHESTRATOR_LLAMA_SERVER`: full path to `llama-server.exe` (download it in **Options**). Required with this backend.
- `AGENT_ORCHESTRATOR_GGUF_DIRS`: optional `;`-separated extra folders holding GGUF files.

The model file is `auto_load.gguf_file` when set and present. Otherwise it is looked up, in order, in each extra folder (recursively, files whose path names the model, for example `Qwen3.5-4B` for `unsloth/Qwen3.5-4B-GGUF`) and in the Hugging Face cache (`HF_HUB_CACHE`, else `HF_HOME\hub`, else `~\.cache\huggingface\hub`, under `models--<org>--<name>\snapshots`). A file whose name contains `gguf_variant` is preferred, `mmproj` files are skipped, and a split model starts from its `-00001-of-0000N` part. If nothing is found, download the model in Unsloth or add its folder in Options.

One server runs at a time on `127.0.0.1:8899` (top-level `"llama_port"` in `config.json` overrides), started with the role's `max_seq_length`, `gpu_layers` (`-1` = all), `cache_type_kv`, `n_batch`, `n_ubatch`, `reasoning_budget` (only `0` is passed) and `llama_extra_args`. A server already running with arguments that can serve the role is reused: the same arguments, except that a larger `-c` is fine and a thinking server serves a `reasoning_budget` 0 role with thinking off per request. A different model or other setting restarts it. Its state is in `%TEMP%\agent-orchestrator-llama.json` (`pid`, `port`, `signature`, `args`, `model_file`, `log`) and its output in `%TEMP%\agent-orchestrator-llama.log`. A workflow run stops the server when it ends; in chat and code mode it stays up until the launcher closes. `python orchestrator.py --unload-local` stops the recorded server and deletes the state file.

## Remote models per role

Any role can use a remote OpenAI-compatible API (OpenRouter, a cloud provider's OpenAI-compatible endpoint, another local server) while the others stay on Unsloth. Give the role `base_url`, a `model` id and `api_key_env`, and no `management_url` or `auto_load`:

```json
"planner": {
  "base_url": "https://openrouter.ai/api/v1",
  "model": "openai/gpt-4o-mini",
  "api_key_env": "OPENROUTER_API_KEY",
  "timeout_seconds": 600,
  "sampling": { "temperature": 0.6, "max_tokens": 8192 }
}
```

`model` is sent as the request's model id as is (no `/models` check), and the role is never loaded, unloaded or monitored. The key is read from the named environment variable and never written to config, logs or prompts; set it before starting the launcher. Errors name the role and URL (for example `planner: API 401 from https://…/chat/completions (check the key in OPENROUTER_API_KEY)`). Keep `sampling` to parameters the API accepts: some reject Unsloth extras such as `top_k` or `enable_thinking`. In the launcher, a role's **Settings** has a **Remote API** mode that writes these keys and keeps the Unsloth settings in `local_settings`; switching back to **Local** restores them.

## Chat mode

Role fallback: only the `planner` role (the base model) is required in `config.json`. An `implementer` or `reviewer` that is missing from `roles`, or set to `{"same_as": "planner"}`, uses the planner's settings (model, sampling, `auto_load`) everywhere, the project run included; the same model is not reloaded.

`python orchestrator.py --project <folder with config.json> --chat <request.json> --events --non-interactive` answers one chat turn instead of running the workflow. It needs only `config.json` in the project folder (no `PROJECT.md`, `STATE.md`, ...), plans no tasks, writes no workflow files or checkpoints, and never runs the configurator. Workers get tools only as the request's extras switch them on (see Search, tools and skills).

`request.json` (UTF-8): `{"worker": "<role id>", "messages": [{"role": "user"|"assistant", "content": "..."}]}`. The last message must be the user's. `worker` is `planner` (the default), `implementer`, `reviewer` or a custom worker `id` (Specialists and Extra reviewers); a legacy `mode` field is ignored. The worker answers with `prompts/chat.txt` as the system prompt (plus its role section) and always has an optional `delegate(worker, brief, context?, files?, acceptance?)` tool: for a request that would benefit from a separate pass it hands a structured handoff to `implementer` or `reviewer`, which runs with a fresh context (`prompts/sub_<worker>.txt` + the handoff, including the web results the caller read this turn; the request's extras as tools, no file tools, no delegation) and returns its report (clipped to about 4,000 characters). At most 3 delegations per chat turn.

Finished replies in the Chat and Code views show as formatted Markdown (headings, bold and italic, lists, tables, quotes, code and links; links open in the browser, web and mail links only). While a reply streams it shows as plain text; **Copy reply** copies the Markdown source.

### Search, tools and skills

Under the message box of the Chat and Code views, **Search**, **Tools** and **Skills** switch extras on for the next messages; the choice is shared by both views and saved. They are sent as `"search": true|false, "tools": true|false, "skills": ["<skill folder>", ...]` in the Chat and Code requests, and the model and its sub-agents gets them as tools:

- **Search**: `web_search` (DuckDuckGo's HTML results: up to 8 titles, URLs and snippets; no API key) and `fetch_page`. The model is told to search for current or specific facts, cite the pages it used, and never follow instructions found in pages.
- **Tools**: `calculator` (arithmetic and `math` functions, no code execution), `current_time` and `fetch_page`. In Code these come on top of the file tools.
- **Skills**: each folder under `%APPDATA%\Agent Orchestrator\skills` with a `SKILL.md` (front matter `name:` and `description:`, as in Claude skills) is listed in the **Skills** menu; ticked ones are named in the system prompt and the model reads one with `load_skill(name[, file])`, which can also read other files in that skill's folder. **New skill…** creates one from a template and opens it; **Open skills folder** opens the folder.

`fetch_page` reads http(s) pages only, refuses hosts that resolve to local or private addresses, and returns at most 20,000 characters of the page's text. Models whose chat template does not support tool calling answer without the tools, as in Code.

Events are the usual `@@AO` lines (`loading`, `model`, `delta`, `done`, `usage`) plus `{"e":"chat_stage","role","stage":"answer","title"}` before the answer, `{"e":"chat_stage","role":"implementer"|"reviewer","stage":"delegate","title"}` when a delegation starts (and the `answer` stage again when it ends), and a final `{"e":"chat_reply","role","text"}` with the answer. Exit code 0 on success; on failure an `ERROR: ...` line is printed and the exit code is 1. Models stay loaded after the turn (the launcher unloads them when the app closes).

## Code mode

The launcher's **Code** view (sidebar: Chat, Code, Options) works like a coding assistant in a folder you choose.

- **New chat** (Code header, or the sidebar while the Code view is shown) asks for a working folder; several chats can share one. Chats are saved under `%APPDATA%\Agent Orchestrator\conversations`, not in the project, and listed in the sidebar like Claude's: **New project** groups chats, and each chat's menu (right-click or **…**) has Rename, Move up/down, Move to a project, Archive and Delete. Drag a chat onto another chat, a project or **Recents** to move it, and drag a project onto another to reorder them. Archived chats sit in a collapsible **Archived** section. If the saved list can't be read, it is shown read-only so nothing is overwritten.
- Conversations in the **Chat** view are saved in the same list once they get a reply (marked with a speech bubble) and reopen in the Chat view with their worker.
- The Code header's **Project** menu holds the per-folder options: **Use a PROJECT.md…** (an existing one is backed up), **Edit PROJECT.md**, **Folders…** and **Stop after the first finished item**. The lead project run ignores task limits; they apply only to the legacy `--pipeline`.
- The workers start as one: **MAIN** (the Planner role, the base model) covers the Planner, Implementer and Reviewer roles, and its tab lists what it covers. **+** adds a separate Implementer or Reviewer (its own tab and model) or a custom worker; removing an added role runs it on MAIN's model again (`"same_as": "planner"` in `config.json`), and its activity shows on MAIN's card.
- Each Code message goes to the **lead agent** (MAIN); the **Lead:** chip names its model and opens its Options. It is the primary agent: like a frontier coding agent it plans with a short checklist (`todo`), does the work itself whatever its size, researches with the web tools when Search is on, verifies with `run_checks` and by reading files, and finishes with a short report of what changed and what is left. Delegating to the Implementer (edits files) or the Reviewer (reads files and runs the checks) is optional, for an independent check or a separate piece, with a structured handoff. It can list, read and search files, and create or edit files directly in the project folder.
- In Chat, the **To:** chip picks the worker to message (MAIN, an added role or a custom worker) and opens its Options; that worker can delegate parts to the others.
- **Project > Folders… > Add folder…** grants extra folders, each one **Read only** or **Full edit**. Nothing outside the granted folders can be read or written. `api_key.txt` and `.git` are always refused, and links or junctions that point outside are skipped.
- No `PROJECT.md` is needed to start. Discuss the project over as many messages as you like, then say `init-project` (or `/init-project`, or ask for a PROJECT.md): the lead loads the bundled `init-project` skill (always offered in Code) and writes `PROJECT.md` itself from the conversation, its research and the folder, then stops. You can also bring your own `PROJECT.md`.
- `PROJECT.md` is the plan, not a mode: say "use PROJECT.md and begin work" (or "continue") and the same lead reads it, checks the files and implements the next work directly in the conversation.
- **Start project** is the separate autonomous run: the workflow files are added (an existing `PROJECT.md` is kept), the view switches to **Run output**, and the lead project run starts. Only the button starts it.

Command line: `python orchestrator.py --project <folder with config.json> --events --code <request.json>`.

- `request.json` is `{"action": "chat"|"write_project", "worker": "<role or custom id>", "messages": [...], "folders": [{"path": "<absolute>", "access": "write"|"read"}]}`. Action `chat` always runs the lead (`worker` and a legacy `mode` are ignored); `write_project` uses `worker` (default `planner`).
- `folders[0]` is the project and is always writable.
- The lead's tools are `list_files`, `read_file`, `search_files`, `write_file`, `replace_text`, `todo`, `run_checks` (syntax, JavaScript code check, page load and `validation_commands` on the project root), `delegate(worker, brief, context?, files?, acceptance?)`, `load_skill` (the bundled `init-project` plus the enabled skills) and the enabled Search/Tools extras. A sub-agent starts fresh (`prompts/sub_<worker>.txt`, the handoff with the lead's web results of the turn, the folders) and inherits the extras; the Implementer gets the first five tools, the Reviewer the first three plus `run_checks`; neither can delegate. `max_tool_steps` limits each loop and `max_delegations` (default 6) the delegations per turn.
- Events: every tool call is a `tool` event (sub-agents' carry their own role), `chat_stage {role:"planner",stage:"lead",title}` when the lead starts and after each delegation, `chat_stage {role:"implementer"|"reviewer",stage:"delegate",title}` when one starts, then `code_project {role,path}` (action `write_project`) and the final `code_reply {role:"planner",text}`.
- Chat and Code turns share the GPU, so only one runs at a time and none during a project run.

### Command sandbox (run_command)

Both Code engines, and their Implementer and Reviewer, have `run_command(command, shell?, timeout_seconds?, request_network?, request_write?, reason?)`: cmd or PowerShell commands (build, tests, scripts, git) started by the engine — never by the model's process — inside a Windows **AppContainer** (`sandbox.py`). Windows enforces it, and every child process inherits it. `run_checks` runs the project's `validation_commands` the same way, so no model-written code runs with your own rights in a Code turn:
- **Files.** One container per conversation (the request's `session`); the Reviewer gets its own read-only container and cannot ask for more. The engine grants the container exactly the turn's folders (write, or read for read-only folders) plus a private temp folder, as inheritable ACEs for its SID. Grants that are no longer wanted are removed, and the record (`<data>\sandbox\grants`) is written before every change, so a crash never leaves an unrecorded grant. The rest of the disk is unreadable, or read-only for Windows' own folders. A folder that is, holds or lies in the Agent Orchestrator folder is never granted.
- **`.git` stays read-only.** Its hooks and config run later as you. Windows ignores deny entries for an AppContainer's own SID, so every `.git` in a write folder stops inheriting instead, with its ACL saved and restored exactly when the grant goes. That covers nested repositories and the `.git` files of worktrees and submodules (dependency and build folders are skipped). A repository a command creates is checked after the command: if it has hooks, config that names programs, or is a `.git` file, you are asked to keep it or quarantine it (its `.git` is renamed). From then on it is read-only too.
- **Network.** Off: the container has no network capability, so loopback (the model servers) is closed too. Settings > **Command sandbox** can allow outbound internet (`internetClient`: no LAN, never loopback) for every command; otherwise `request_network`, or output that looks like a blocked network call, asks you in a dialog.
- **Extra write folders.** `request_write` asks before granting a folder for one command. It takes plain `C:\...` paths only, compared by their real path (no junction, link, UNC or `\\?\` spelling). It refuses drive roots, your profile or anything above it, AppData, Desktop and the dot-folders of your profile (`.vscode`, `.config`, ...), and anything that is, holds or lies in: the Agent Orchestrator folder, the launcher's data folder, Python's installs and site-packages, Windows, the program folders, ProgramData, the Startup and PowerShell profile folders, or a folder on PATH. An approval holds for that one command, and the command still runs in the container. Commands run one at a time.
- **Secrets.** Variables whose names contain KEY, TOKEN, SECRET, PASSWORD or AUTH are not passed to the command. Their values, the secret-named values of `HKCU\Environment` and `api_key.txt` are replaced by `[secret removed]` in the output before the model sees it. Windows lets every AppContainer read parts of HKCU, so while a user environment variable has a secret-looking name, **commands are refused until the setup has closed `HKCU\Environment`**, where user environment variables live. PowerShell 5.1 reads that key as it starts, so while it is closed commands use cmd. To get PowerShell back, move the secret out of your user environment variables (the launcher reads `api_key.txt`), then run `sandbox.py --unprotect-environment --state <data>\sandbox`. The rest of `HKCU\Software` stays readable as Windows ships it, so keep secrets out of the registry. Redaction is a plain text replacement: an encoded or split secret gets through. Fully closing the registry would need commands to run under a separate Windows account.
- **Limits.** A command runs at most `timeout_seconds` (default 120, at most 1800) in a kill-on-close Job, so a timeout ends its whole process tree. Output is clipped to 30,000 characters.
- **Setup.** **Set up sandbox access…** does two things:
  1. As you, without elevation (`sandbox.py --protect-environment`): it removes the AppContainer entries from `HKCU\Environment`'s ACL, saved so it can be restored, which lets commands run.
  2. As administrator, once (`sandbox.py --setup`, a UAC prompt): every container carries this app's capability (`agentOrchestratorCommandSandbox`), and that capability may then list the fixed drives' roots and `C:\Users` (names only, written without walking the drive) and read the folders of the developer tools on PATH (Python, Node.js, Git, .NET, Java, ...). Only this app's containers gain that access. PowerShell needs the drive roots, so until this part has run, commands use cmd. FAT32 and exFAT drives keep no permissions, so they are skipped; the sandbox cannot open them anyway.

  `sandbox.py --unprotect-environment` and `--undo-setup` (with `--state <data>\sandbox`) reverse them. The engine also lets the capability list the folders above each granted folder that you own.
- The approval protocol: the engine emits `{"e":"approval","id","kind":"network"|"write","detail","reason"}` and waits for one stdin line `{"id","allow"}`. Without a launcher the answer is no.

### Pi Code engine (optional)

Settings > **Code engine** chooses who runs a Code turn's agent loop: **Built-in** (above) or **Pi** ([pi.dev](https://pi.dev), `@earendil-works/pi-coding-agent`). **Install Pi** runs `npm install --ignore-scripts` of the tested version (1.4.0) into `<data>\pi`; Node.js 22.19+ must be on PATH. The launcher passes the installed `cli.js` as `AGENT_ORCHESTRATOR_PI`; without it the engine falls back to a 1.x `pi` on PATH and refuses older ones.

With `"engine": "pi"` in the Code request the engine loads the lead's model as usual, then runs `pi --mode rpc` in the project root on that model (provider `ao`: the role's endpoint, sampling, thinking switch and `max_tokens`, written to a per-turn `models.json`), with only Pi's `read`, `edit` and `write` tools and none of its project-local configuration, extensions, MCP or shell. `pi/agent_orchestrator.ts` sends every `read`/`edit`/`write` path through a localhost gateway in the engine, which applies the same folder grants and protected files as the built-in tools (a refused path blocks the call; an accepted one is replaced by the absolute path that was checked; spellings Pi would rewrite — a leading `@` or `~`, `file:` URLs, `/c/` paths, Unicode spaces — are refused, and `read`/`edit` need an existing file), and registers the engine's own `list_files`, `search_files`, `todo`, `run_checks`, `delegate` and Search/Tools extras, which run in `orchestrator.py`. Skills, the bundled `init-project` included, load as Pi skills. The system prompt adds `prompts/lead_pi.txt` and the FOLDERS block to Pi's own. Each Code conversation keeps one Pi session (`<data>\pi\home\sessions`, the request's `session` id), so later turns see earlier tool calls and results; a session's first turn gets the earlier messages as text once. Pi compacts the session itself (reserve `min(n_ctx/2, max_tokens+4096)`, keep `n_ctx/4`). Pi's events become the usual `delta`, `done`, `usage`, `context`, `tool` and `compacted` events; delegation works as in the built-in engine, and the lead's model is ensured again after a worker. Chat, Start project and the pipeline do not use Pi.

## Custom workers

`config.json` may hold an optional top-level `custom_workers` array. Absent or empty, the run behaves exactly as before.

```json
"custom_workers": [{
  "id": "8f0c...", "title": "Tester", "kind": "specialist",
  "description": "Writes and runs unit tests for a feature.",
  "system_prompt": "You are a meticulous test engineer...",
  "base_url": "...", "management_url": "...", "api_key_env": "UNSLOTH_API_KEY",
  "expected_model_contains": "...", "display_name": "...", "timeout_seconds": 600,
  "auto_load": {}, "sampling": {}
}]
```

`id` is stable and unique and is the `role` of every event the worker emits. `title` is unique (case-insensitive) and never `Planner`, `Implementer` or `Reviewer`. The model fields (`base_url`, `expected_model_contains` or `auto_load.model_path`, `auto_load`, `sampling`, ...) have the same meaning as in `roles.<role>`, and the worker is loaded, reloaded and unloaded the same way. An entry with an unknown `kind`, a reserved or duplicate `id`/`title`, a missing `base_url`, model or `system_prompt`, or (for a specialist) a missing `description` is ignored with a `[workers]` warning.

- **Specialist** (`"kind": "specialist"`): listed in the Planner's TEAM section with its description. The Planner may add `"worker": "<title>"` to a task (case-insensitive; an unknown name triggers the usual correction turn, and the choice is stored in the task, so it survives a resume). The Specialist then does that task instead of the Implementer, with the same tools, file scope, syntax check and unverified-report handling. Its system prompt is the standard implementer prompt followed by a `YOUR ROLE: <title>` section holding `system_prompt`.
- **Extra reviewer** (`"kind": "reviewer"`): after the Reviewer passes a task and the syntax check passed, each Extra reviewer (config order, read-only) reviews it with the standard reviewer prompt followed by its own `YOUR ROLE` section. The task passes only when the Reviewer and every Extra reviewer pass. If any fail, the round fails: their defects, prefixed with the reviewer's title, go to the next round as feedback and into `REVIEW.md`. All Extra reviewers run each round so one round carries all their feedback; none run when the Reviewer fails. A run stopped while they check resumes there (checkpoint phase `extra_reviewing`) without re-running the Implementer or the Reviewer.

## Behaviour check

For web projects (an `index.html` in the project root, or `"behaviour_check": {"page": "<relative path>"}` in `config.json`) the page is loaded headlessly before planning and after each implementer round, right after the syntax check: the project is served on `127.0.0.1` and opened in a fresh browser profile (empty localStorage), and uncaught errors, unhandled promise rejections, `console.error` calls and missing scripts/stylesheets are recorded. Any error fails the round without calling the Reviewer and goes back to the implementer as feedback; a clean load gives the Reviewer a `BEHAVIOUR CHECK` block with a visible-text sample, and errors found before planning make the Planner schedule a fix first. It needs Edge or Chrome (standard install paths or on `PATH`); set `behaviour_check.browser` to a Chromium executable to choose one. Without a browser the check is skipped with a `[behaviour]` note. `"behaviour_check": false` turns it off.

For web tasks the Planner adds `"acceptance"` steps (at most 12) that the check runs in order on the fresh page after it loads, so a task passes only when its feature works in the browser, not merely when the page loads. Actions: `{"do": "click"|"fill"|"select"|"check", "selector": "#id", "value": …}` (`fill`/`select` fire `input` and `change`; `select` matches an option's value or text; `check` takes `true`/`false`), `{"do": "goto", "hash": "#view"}`, `{"do": "wait", "ms": 300}` (max 2000). Checks: `{"expect": "text", "selector": "#list", "contains": "Gaming PC"}` (visible text), `{"expect": "visible"|"hidden", "selector": "#id"}` (the element must exist), `{"expect": "count", "selector": "#list li", "min": 1, "max": 5}`, `{"expect": "storage", "key": "builds", "contains": "Gaming PC", "json_length_min": 1}` (localStorage), `{"expect": "no_errors"}`. Selectors are CSS strings of at most 200 characters; malformed steps are a contract error that goes back to the Planner. The steps are listed in `TASK.md`, so the Implementer builds to their selectors. The first failing step, or a page error raised during a step, fails the round with its detail (for example `acceptance step 3 failed: {…} — expected text 'Gaming PC' in #build-list; got ''`) and the steps after it are not run. When every step passes, the Reviewer sees each one ticked in the `BEHAVIOUR CHECK` block; a web task without steps gets the load check only, and the block says so.

## Code check

For projects with `.js`/`.mjs` files a static check runs after the syntax check and before the behaviour check (and in the implementer's own self-check). It reads the project's scripts and pages (skipping `node_modules`, dot folders and files over 1 MB) and flags calls such as `DataStore.addBuild(...)` or guarded references (`DataStore.fn ? ...`, `X && X.fn`, `typeof X.fn`) to members a project namespace object does not define, naming close matches, and `getElementById`/`querySelector('#id')` lookups of ids no page or script creates. Namespaces it cannot resolve confidently (spread, computed keys, `Object.assign`, other dynamic code) are skipped. On lines the task added it also flags stubs (`would go here`, `TODO`/`FIXME`/`placeholder` comments, `not implemented`, always-true assertions) and existence checks around the project's own functions. New issues fail the round like syntax errors; contract issues that were already there are listed to the Reviewer as pre-existing, and before planning they are given to the Planner so a task fixes them. `"code_check": false` turns it off.

## Project contract

Before the first task the Planner writes the project contract (`prompts/contract.txt`) from PROJECT.md and the current files, and the orchestrator saves it as `CONTRACT.md`: a short header plus one ```json block with `files` (path, purpose and, for pages, the scripts/stylesheets it `loads`), `ids`, `functions`, `storage_keys` and `notes`. Models cannot edit it; a task's optional `"contract_changes"` (same form, merged by path/id/name/key) updates it before the task runs. The Planner, Implementer and Reviewer all see it as `PROJECT CONTRACT`. It is enforced: acceptance steps selecting `#id` must use contract ids (the plan is corrected otherwise), and the code check fails a round when a contract page stops referencing an existing file it `loads` or a script looks up an element id the contract does not list (pre-existing issues are only reported). A missing or unreadable `CONTRACT.md` is regenerated, so Restart Project rebuilds it. `"contract": false` turns the whole feature off.

Whenever the contract is written or amended, the orchestrator scaffolds the web files it names that do not exist yet, so the page wiring is never left to a model: each missing `.html`/`.htm` page gets a minimal HTML5 page with a `<link rel="stylesheet">` for every `.css` it `loads`, a `<script src>` for every `.js`/`.mjs` (`type="module"` for `.mjs`) at the end of `<body>`, and an empty element for each contract id on that page (ids without a `file` go to the only page, if there is one) inside `<main>`; each missing `.js`/`.mjs`/`.css` file is created holding a one-line comment naming its purpose. Existing files are never changed, and other file types (`.py`, `.cpp`, `.h`, ...) are left to the tasks. It prints `[scaffold] created ...`. `"scaffold": false` turns off only the scaffolding.

## Safety and recovery

- Paths are resolved beneath `--project`; absolute paths and `..` escapes are rejected.
- Workflow records cannot be edited by the models.
- Only planner-approved files are exposed per task.
- A task can ask for more files once: the Implementer ends its report with `NEEDS_FILES: a.html, b.js — reason`, or Reviewer/check defects name the same file outside the allowed files in two consecutive rounds. The Planner approves or rejects; an approval adds at most the requested files (never workflow records, at most two over the task limit) from the next round on, without extra rounds, and survives a resume. A rejection's reason goes into the feedback and the ISSUES.md entry.
- Failed review gets at most two repair attempts by default.
- Malformed JSON, invalid Planner task contracts, and invalid Reviewer verdicts get at most two format/scope correction attempts.
- Model JSON is read tolerantly but never guessed: think blocks and surrounding prose are ignored, and only lossless slips (comments, trailing commas, smart quotes, raw newlines in strings) are fixed. A reply cut off or without JSON is re-asked from its original prompt with thinking off instead of being "repaired"; repairs also run with thinking off and keep the role's output format, followed by one fresh attempt. A Reviewer verdict that still cannot be read counts as a FAIL round (never PASS) and the loop continues; a Planner reply that cannot be read stops the run, which can simply be restarted.
- `STATE.md` changes only after reviewer PASS.
- `TASK.md`, `RESULT.md`, and `REVIEW.md` preserve the latest handoff.
- An implementer that prints tool calls as JSON text instead of calling the tools has them executed anyway; one that never writes a file is stopped rather than passed to the reviewer.
- Unreviewed changes left by an interrupted attempt (resume) or an earlier round are flagged to the implementer as PRIOR WORK; if it reports them complete without writing, the reviewer still checks the full diff against the task start instead of the task being blocked.
- A reply cut off at the output limit is logged; a truncated tool call is never executed and the model is sent back to write in smaller pieces, then the run stops asking to raise `sampling.max_tokens`. A prompt over the context window stops with a request to raise `auto_load.max_seq_length`.
- Models are unloaded from Unsloth when a run ends, whether it passed, failed, or was stopped.
- Stop with Ctrl+C. Re-run after inspecting the latest workflow records.

This is not a security sandbox. A local model allowed to edit project files can still write harmful source code. Use version control or a disposable copy of valuable projects.
