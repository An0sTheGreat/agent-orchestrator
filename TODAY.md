# Session handoff — 2026-10-26

Everything changed in this session, in order, written so a fresh chat can continue. Project root: `C:\Programs\Agent Orchestrator` (not a git repo; no history to diff — this file is the record). Test workspace: `C:\Programs\Test Program`.

## Quick start for the next chat

- Python tests: `python -m unittest test_orchestrator` → **58 tests, OK** at end of session. `python -m py_compile orchestrator.py` clean.
- Launcher (now buildable here — .NET 8 SDK 8.0.427 was installed via winget; `dotnet` is at `C:\Program Files\dotnet`, prepend to PATH if a shell doesn't see it):
  - Build: `dotnet build .\launcher\AgentOrchestrator\AgentOrchestrator.csproj -c Release -r win-x64 --self-contained true --no-restore`
  - Self-test: run `bin\Release\net8.0-windows\win-x64\Agent Orchestrator.exe --self-test` (exit 0 = pass; must run from inside the workflow tree).
  - UI smoke: same exe with `--ui-smoke` (exit 0 = pass; failures print `ui-smoke: check failed at MainWindow.xaml.cs:<line>` to stderr). Set `AGENT_ORCHESTRATOR_SNAPSHOTS=<folder>` to save PNGs of each UI state (idle, running, compact, collapsed, themes).
  - Publish: `dotnet publish .\launcher\AgentOrchestrator\AgentOrchestrator.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\dist` — **close the app first** (it can't overwrite a running exe).
- `dist\Agent Orchestrator.exe` was republished at the end of the session with everything below; self-test and ui-smoke pass on the published exe.
- `orchestrator.py` runs in place (the launcher calls it from this folder), so Python changes are live immediately — no publish step.
- MSBuild gotcha: if you restore a source file from a backup with `Copy-Item`, set its `LastWriteTime` to now before rebuilding, or MSBuild skips the rebuild and you test a stale exe.
- Known flaky check: the ui-smoke "toasts leave on their own" timing check fails roughly 1 run in 8–12, on the old exe too. Not a regression.

## 1. Original problem: selected models produced no output

Models at the time: Planner Qwen3.5-4B (Q4_K_M), Implementer Qwen2.5-Coder-3B (Q8_0), Reviewer Phi-4-mini (Q5_K_M), all via a local model server at `http://127.0.0.1:8888`. Previous primary models (AgentWorld 35B, Coder 14B, Qwen3.5 9B) are referenced only in `.ai_documents/readme.md`; no config still points at them.

Root causes found and fixed in `orchestrator.py` (`run_agent` / `ApiClient`):
1. **Implementer looped until `max_tool_steps`.** Coder-3B prints tool calls as JSON text instead of native `tool_calls`. `text_tool_calls` recovered them every turn, so `run_agent` never reached its "finished" branch and repeated identical turns ~30 times, then raised. Fix: if a text turn's recovered calls are identical (name + parsed arguments) to the immediately previous text turn's, treat it as "no calls"; a native turn resets the comparison.
2. **Reviewer rejected by the server.** Phi-4-mini's GGUF template has no tool support; the server returns `API 400 ... "param":"tools"`. Fix: on that specific error, retry the same turn without tools and continue the agent run without tools. Other errors propagate unchanged.
3. **Wrong model treated as loaded.** The server's `/v1/models` lists every installed model (most recently used first) with a `loaded` flag; `model_id()` took entry 0 even if unloaded → "No model loaded" 400s. Fix: skip entries with `"loaded": false` (entries without the flag still count, for other servers).
4. **Silent stop.** When the Implementer never uses a tool, its last reply is now saved to RESULT.md and included in the error, so the reason is visible.
5. **Implementer prompt** (`prompts/implementer.txt`): added "Functionality the task asks you to add is the work itself, never a blocker; a blocker is only something you cannot do with the allowed files and tools." (Coder-7B had been replying "BLOCKED" because the feature didn't exist yet.)

## 2. Planner knows the team and task size is configurable

- New `TEAM` section in the planner input (`team_context`): Implementer and Reviewer model name, quant, context length, max output tokens, plus a "Task limits" line.
- Optional per-project config key `"task_limits": {"max_allowed_files": N, "max_required_work": M}`; defaults 3 and 7 when absent (old behaviour). Validated at startup (`task_limits`), enforced in `validate_task`, stated to the planner. Bad plans go through the existing correction loop.
- `prompts/planner.txt`: hard-coded "1-3"/"1-7" replaced by "stay within the Task limits given in TEAM", plus "Size each task so the implementer can complete all of it in a single reply; smaller models need smaller tasks."
- Recommended for small local models: `{"max_allowed_files": 1, "max_required_work": 3}`. **Not yet added to Test Program's config.json** (it currently has no `task_limits`).

## 3. Launcher: per-worker generation settings

- **Settings** button on every built-in worker card (full and compact), next to **Change**. Opens an editor for temperature, max tokens, repetition penalty (empty = remove), reasoning budget, context length; writes only those keys in the project's `config.json` (`LauncherLogic.ReadRoleSettings` / `WriteRoleSettings`, covered by `--self-test`). Empty fields leave values unchanged; invalid input writes nothing; never creates `auto_load` (context length / reasoning budget rejected if the role has no `auto_load.model_path`). Disabled during runs; hidden on custom workers.
- `LauncherLogic.IsInsideButton` lets buttons on the compact card be clicked without starting a card drag (fixed a crash when pressing on text runs).

## 4. Launcher: UI changes

- **Streaming pulse:** only a streaming section's title pulses; its preview line and body are held at 0.55 opacity and return to full when the reply ends (`SetPulse`). Clearing output / resetting mid-stream stops the animation.
- **Compact cards:** **Change** button above **Settings** (bottom right).
- **Collapsible left pane:** ‹ button (top right) animates the pane 260 → 54 px icon rail (240 ms); › or Ctrl+B expands. Rail icons: Workspace, Options, project folder, Edit PROJECT.md, run-once. State persisted (`AppSettings.SidebarCollapsed`). Rail folder/run-once are locked during runs like the full pane. Accessible names on icon buttons. ui-smoke checks collapse/expand and the running-state locking.
- ui-smoke `Fail()` now uses `[CallerLineNumber]` (line numbers in failures are accurate).

## 5. Stop → handoff → resume

Python (`orchestrator.py`):
- `.ao_checkpoint.json` (version 1: task, pre-task "before" snapshot, 0-based round, feedback/result text, phase `implementing|reviewing|passed|blocked`, timestamp; completion phases also carry a unique `completion` token). Written atomically (`write_atomic`: temp file + `os.replace`) after a fresh task is saved, at each implementer round, before each reviewer call, and before recording completion.
- `HANDOFF.md` regenerated with each checkpoint change: task, allowed files, phase, round N of M, last feedback; or "No task in progress." + last passed task. A stale one is refreshed at the start of each run iteration.
- **Resume:** a valid checkpoint skips planning and continues the same task at the stored round with the stored before-snapshot and feedback (reviewer diff still covers the whole task). Completion-phase checkpoints just record the result (no model calls, don't count against `max_tasks`). Invalid checkpoint → warning, deleted, replan. Log shows 1-based round; the `resume` event carries the 0-based round.
- **Exactly-once recording:** each pass/block entry in STATE.md / ISSUES.md carries a `<!-- ao-completion: <token> -->` marker; a rerun after a kill skips if its token is already there. Reused task IDs are always recorded.
- STATE.md, ISSUES.md and config.json (configurator) are written atomically.
- `PROTECTED_FILES` = workflow files + HANDOFF.md + checkpoint + every `.tmp` name: models can't write them, they're excluded from the planner inventory and from allowed-file snapshots (incl. globs). `WORKFLOW_FILES` (required files) unchanged.
- To discard an interrupted task and plan fresh: delete `.ao_checkpoint.json` in the project folder.

Launcher:
- Stop (and closing the window during a run) kills the orchestrator, waits up to 2 s for exit, then `LauncherLogic.RecordStop` appends "## Stopped by user" (time, active worker + status, which task the next run resumes) to `HANDOFF.md`; logs and toasts "Handoff saved".
- New `resume` event shows a "RESUMED <id>" note in the output pane.

## Test coverage added today

`test_orchestrator.py` grew from 15 to 58 tests (repeat-loop, no-tools fallback, model_id filtering, saved last reply, task limits/TEAM, checkpoint lifecycle, resume paths, kill-point idempotency, protected files, reused IDs, tokens). Each fix was confirmed to fail its test when the fix is removed. Launcher `--self-test` and `--ui-smoke` were extended for the settings editor, RecordStop, IsInsideButton, the rail, and the resume note.

## Current state of Test Program

Re-initialized by the user during the session. Has passed tasks in STATE.md, last run ~16:04. Config currently: Planner Qwen3.5-4B Q4_K_M, Implementer Qwen2.5-Coder-3B Q8_0 (repetition_penalty 1.05 still set), Reviewer Phi-4-mini Q5_K_M; no `task_limits`; no checkpoint present.

## Open items (not done)

- `HANDOFF.md` "Last passed task" can be fooled if an implementer result quotes an old STATE.md entry including its marker (informational only; resume and recording unaffected). Fix idea: indent/quote implementer result lines in `append_passed_state`.
- A second Stop press within the 2 s wait can append a second "Stopped by user" note (guard on `_stopRequested` / disable Stop before the await).
- Collapsed rail: run-once toggle uses the NavToggle checked style, so it looks like a second selected view.
- Pre-existing launcher issues spotted, not fixed: `WriteRoleModel` (Change) can create `auto_load` without `model_path` and doesn't catch `UnauthorizedAccessException`/`ArgumentException`; `OnClosing` blocks on `UnloadModelsAsync().Wait(4s)` on the UI thread; `Stop_Click`/`OnClosing` don't catch all `Kill` exceptions.
- Model-quality limits remain: small models can still produce broken JSON (e.g. Coder-7B's missing closing brace) or fail review on large tasks. Ideas discussed but not built: tolerate a missing trailing `}` in `text_tool_calls`; lower implementer temperature (0.1–0.2) and drop `repetition_penalty` for code models.
- Docs are stale: `README.md` and `.ai_documents/handoff.md` still describe fixed 1–3 file / 1–7 item limits and don't mention TEAM, task_limits, Settings, the rail, or checkpoint/resume.
- `MainWindow.xaml` and `MainWindow.xaml.cs` were converted from CRLF to LF line endings during the session (harmless).
- Rollback copy of the launcher from before today's launcher changes: `C:\Users\TCSUser\AppData\Local\Temp\claude\C--Programs-Agent-Orchestrator\8a28a280-5270-4e7a-a51a-2461fb484e25\scratchpad\Agent Orchestrator.previous.exe` (temp folder; may be cleaned up).
