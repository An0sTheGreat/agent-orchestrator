---
name: godot
description: "Use when the user calls godot, /godot or $godot, or asks to import, check, run, test or export a Godot 4 project from the command line, or to change scenes and resources with an editor script."
---

# /godot

## Purpose
Work on a Godot 4 project headless: import assets, syntax-check GDScript, run the game or a scene briefly, run editor/tool scripts and tests, and export builds.

## Find Godot
Godot is a portable exe, rarely on PATH. Look for `Godot_v*_win64*.exe` near the project, in `%LOCALAPPDATA%\Programs`, Downloads, or a Steam `steamapps\common\Godot Engine` folder (`dir /s /b Godot_v*.exe` in likely folders). Prefer the `_console.exe` twin: it prints to the terminal and returns the exit code. Match the project's version: `config/features=PackedStringArray("4.x", ...)` in `project.godot`. A `_mono` build is needed for C# projects (and needs the .NET SDK); GDScript works in either.

## Run (shell `cmd`, from anywhere)
`set G="<path>\Godot_v4.x-stable_win64_console.exe"`
- Import (first run on a fresh checkout, or after adding assets): `%G% --headless --path "<project>" --import` → exit 0.
- Syntax-check a script: `%G% --headless --path "<project>" --check-only --script res://path/file.gd` → exit 1 and `SCRIPT ERROR: Parse Error ... ` on a mistake.
- Run the game for a few seconds: `%G% --headless --path "<project>" --quit-after 600` (frames), or a scene: append `res://scenes/x.tscn`. Read the output for `ERROR`/`SCRIPT ERROR` lines.
- Editor/tool script: a script that `extends SceneTree` (or `MainLoop`) with its work in `_init()` and `quit(code)` at the end: `%G% --headless --path "<project>" --script res://tools/do_it.gd`. It can `load()` scenes and resources, change them and `ResourceSaver.save()` them.
- Arguments for your own scripts: after `--`, read with `OS.get_cmdline_user_args()`.
- Tests: with the GUT addon, `%G% --headless --path "<project>" -s addons/gut/gut_cmdln.gd -gdir=res://test -gexit`; with GdUnit4, its `addons/gdUnit4/runtest.cmd`. Use what the project already has.
- Export: `%G% --headless --path "<project>" --export-release "Windows Desktop" "build/game.exe"` (the preset name from `export_presets.cfg`; `--export-debug` for debug). It needs the export templates for that exact version in `%APPDATA%\Godot\export_templates\<version>`; if they are missing, tell the user to install them from the editor (Editor > Manage Export Templates) rather than downloading them yourself.

## Looking at the game
`--headless` renders nothing. To get a picture, run without it (a window opens for a moment): `%G% --path "<project>" --resolution 1280x720 --script res://tools/shot.gd` with

```gdscript
extends SceneTree
func _init():
	root.add_child(load("res://scenes/main.tscn").instantiate())
	for i in 10: await process_frame          # let it settle
	await RenderingServer.frame_post_draw
	var err = root.get_texture().get_image().save_png("res://shot.png")
	quit(err)                                  # 0 (OK) on success
```

then open `shot.png`. A scene whose `_ready()` quits the game needs its script removed first (`node.set_script(null)`). `user://` paths are `%APPDATA%\Godot\app_userdata\<project name>`.

## Rules
- Edit `.tscn`/`.tres` text carefully (they are text, but ids and `load_steps` must stay consistent); prefer a tool script that loads, changes and saves the resource when the change is more than a value.
- Do not delete the `.godot/` folder or change `config/features` (the engine version) unless the user asks.
- Godot can run fully in the command sandbox (no network, writes stay in the project), except anything writing `user://` (AppData): ask for `outside_sandbox` with that reason, or write to `res://` instead.
- Prove results with the exit code and the printed output (`ERROR` lines), or an image you opened.

## Output
- The Godot version and commands used, and the evidence (exit codes, error lines, test counts, image).
