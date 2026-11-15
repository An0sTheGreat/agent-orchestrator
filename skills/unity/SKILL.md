---
name: unity
description: "Use when the user calls unity, /unity or $unity, or asks to compile, test, build, inspect or change a Unity project from the command line: batch mode when the Editor is closed, the Unity CLI when it is open."
---

# /unity

## Purpose
Work on a Unity project without clicking through the Editor: compile, run tests, build a player, read the console, capture the game view, and run editor code.

## First, find out
1. The project root: the folder holding `Assets/`, `Packages/` and `ProjectSettings/`.
2. Its Editor version: the first line of `ProjectSettings/ProjectVersion.txt` (`m_EditorVersion: 6000.3.26f1`).
3. That Editor: `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Unity.exe` (or the Hub's install path; `unity editors` lists them when the Unity CLI is installed). Never open a project with another version: Unity upgrades it.
4. Whether the Editor already has the project open: `Temp\UnityLockfile` exists and a `Unity.exe ... -projectpath "<root>"` process is running (`wmic process where "name='Unity.exe'" get CommandLine`).
5. The project's own docs (README, Docs/): many projects already list their exact build and test commands. Use those first.

Unity needs the user's licence (AppData) and, through the CLI, a localhost connection to the Editor; neither works in the command sandbox. Run Unity commands with `outside_sandbox` and say why, unless Full access is on.

## Editor closed: batch mode (shell `cmd`)
- Compile check (imports and compiles, then quits):
  `"<Unity.exe>" -batchmode -nographics -quit -projectPath "<root>" -logFile "<root>\Logs\compile.log"`
  Exit 0 and no `error CS` in the log means it compiled. The first run on a fresh checkout imports every asset and can take many minutes: give it a long timeout.
- Tests (no `-quit`: the test runner quits itself):
  `"<Unity.exe>" -batchmode -nographics -projectPath "<root>" -runTests -testPlatform EditMode -testResults "<root>\TestResults\editmode.xml" -logFile "<root>\TestResults\editmode.log"`
  Use `PlayMode` for play-mode tests (drop `-nographics` if they render). Exit 2 = a test failed; read the `<test-case result="Failed">` entries in the XML.
- Build or any editor code: `-executeMethod Namespace.Class.StaticMethod` with `-quit` and `-buildTarget Win64`. Look for an existing build script (`Assets/**/Editor/*Build*.cs`) before writing one; a new one must be a static method in an `Editor` folder.
- Always read the `-logFile`: batch mode prints almost nothing to the console. Search it for `error`, `Exception` and `Aborting batchmode`.

## Editor open: the Unity CLI
Batch mode cannot open a project the Editor has open. If the Unity CLI is installed (`%LOCALAPPDATA%\Unity\bin\unity.exe`), drive the running Editor instead:

```
unity --no-banner --non-interactive --json command --project-path "<root>" --result-only <group> <name> [--param value]
```

- Discover: `unity command --project-path "<root>" --query <term>` (for example `test`, `console`, `capture`, `build`, `scene`); `--detail full` shows each command's parameters.
- Useful ones: `console status` (compile-failure flag and counts), `console get` (filter by level, tail), `test run --mode editor|playmode --filter <name>`, `capture game_view --output-path <png>`, `scene open`, `editor.playmode start` / `stop`, `build run` then `build status`, `script eval "<C# expression>"` for one-off editor code.
- Commands marked destructive need `--confirm true`; ask the user first.
- The user may be working in that Editor. Do not open other scenes, enter play mode, start builds or change assets unless the task needs it, and say what you did.

## Rules
- Check outcomes from the log, the test XML, the console or a captured image, never from the exit code alone.
- Keep editor helpers you add in an `Editor` folder (or an editor-only assembly) so they never ship in a player.
- Do not delete `Library/` or change the project's Editor version, render pipeline or build target unless the user asks.
- Large binary assets are usually in Git LFS: do not rewrite them with text tools.

## Output
- The commands run (batch mode or CLI), the result with its evidence (log lines, test counts, image), and anything left for the user to do in the Editor.
