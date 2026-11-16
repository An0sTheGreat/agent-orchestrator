---
name: unreal
description: "Use when the user calls unreal, /unreal or $unreal, or asks to script, inspect, compile, test, cook or package an Unreal Engine 5 project from the command line."
---

# /unreal

## Purpose
Work on an Unreal Engine 5 project without the editor window: run editor Python against the project, list and change assets, run automation tests, and build, cook or package.

## Find the engine
- The project's engine: `EngineAssociation` in the `.uproject`. A version like `"5.4"` is a launcher install; a `{GUID}` is a source or custom install registered in `HKCU\Software\Epic Games\Unreal Engine\Builds`.
- Launcher installs: `C:\Program Files\Epic Games\UE_5.x\`; also check other drives (`dir /s /b UnrealEditor-Cmd.exe` in likely folders such as `X:\Unreal Engine\UE_5.x`). Use the matching version; opening a project with a newer engine converts it.
- Tools: `Engine\Binaries\Win64\UnrealEditor-Cmd.exe` (headless editor, prints to the console) and `Engine\Build\BatchFiles\RunUAT.bat` (build, cook, package).

## Is the editor open?
`tasklist /fi "imagename eq UnrealEditor.exe"`. Running a second editor on the same project works for read-only scripts but can conflict on saves: when it is open, ask before changing assets, and prefer having the user save first.

## Editor Python (shell `cmd`)

```
"<UE>\Engine\Binaries\Win64\UnrealEditor-Cmd.exe" "<project>.uproject" -run=pythonscript -script="<abs path>\tools\do_it.py" -EnablePlugins=PythonScriptPlugin -unattended -nop4 -nosplash -NullRHI -stdout -FullStdOutLogOutput > Saved\py.log 2>&1
```

- `-EnablePlugins=PythonScriptPlugin` turns Python on for this run even when the project does not list the plugin.
- `-NullRHI`: no GPU or window; drop it for anything that renders (thumbnails, screenshots).
- Takes about 30 s to start on a small project, much longer on a big one or the first time (shaders, DDC): use a long timeout.
- A Python exception makes the exit code non-zero. Prefix your prints (`print("OUT ...")`) and search the log for them and for `LogPython: Error`.
- Useful API: `unreal.EditorAssetLibrary` (`list_assets("/Game", recursive=True)`, `load_asset`, `duplicate_asset`, `rename_asset`, `save_asset`), `unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([...])` for FBX/texture import, `unreal.EditorLevelLibrary` / `unreal.EditorActorSubsystem` for actors in a level, `unreal.SystemLibrary.get_engine_version()`.

## Tests, builds and packages
- Automation tests: `UnrealEditor-Cmd.exe "<project>.uproject" -ExecCmds="Automation RunTests <Filter>;Quit" -unattended -nop4 -NullRHI -stdout -log` (filter such as `Project.` or a test name); read `Test Completed. Result={Success|Fail}` lines.
- Compile C++ (projects with a `Source` folder): `"<UE>\Engine\Build\BatchFiles\Build.bat" <Project>Editor Win64 Development -Project="<project>.uproject" -WaitMutex`. Needs Visual Studio's C++ tools.
- Package a Windows build: `"<UE>\Engine\Build\BatchFiles\RunUAT.bat" BuildCookRun -project="<project>.uproject" -platform=Win64 -clientconfig=Development -build -cook -stage -pak -archive -archivedirectory="<out>" -unattended -nop4 -utf8output`. Long (tens of minutes on the first cook): run it once, with a long timeout, and read the end of the output for `BUILD SUCCESSFUL`.

## Rules
- Unreal writes `Saved/`, `Intermediate/` and `DerivedDataCache/` in the project and reads the user's AppData: run it with `outside_sandbox` and say why, unless Full access is on.
- Never delete `Content/` assets or `Config/` files to fix a problem without asking; `Intermediate/`, `Saved/` and `DerivedDataCache/` are safe to clear only when the user agrees (it forces a long rebuild).
- `.uasset`/`.umap` are binary: change them through the editor Python API, never by text edits.
- Prove results with the log lines you printed, test results or the build's final line.

## Output
- The engine version and commands used, and the evidence (log lines, test results, the archive path).
