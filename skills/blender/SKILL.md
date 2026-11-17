---
name: blender
description: "Use when the user calls blender, /blender or $blender, or asks to model, render, convert or batch-process 3D files (.blend, .glb/.gltf, .fbx, .obj) with Blender, or to change the scene open in the user's Blender."
---

# /blender

## Purpose
Drive Blender with Python (`bpy`): change the scene the user has open, or work headless to build scenes, render, and convert or clean up 3D files.

## The scene the user has open
When the user means the Blender on their screen ("my scene", "the open file", "the open blender scene", "what I'm looking at"), do NOT start `blender.exe`, do NOT open Blender with the computer tool, and do NOT save a copy to work on: each of those is a new, empty or separate scene. "Headless" together with "the open scene" means exactly this: scripts run inside the open Blender through `live.py`, no clicking. Send code to the open Blender instead, through the "MCP for Blender" add-on's server (localhost:9876), with `live.py` from this skill's folder (the folder this SKILL.md is in):

```
python "<skill folder>\live.py" --ping
python "<skill folder>\live.py" tools\edit.py
python "<skill folder>\live.py" -c "import bpy; print(bpy.data.filepath, [o.name for o in bpy.data.objects])"
```

- Run it with `outside_sandbox` (reason: "talks to the open Blender on localhost"): the command sandbox cannot reach localhost. Not needed when commands already run outside the sandbox (full access).
- The code runs inside the open Blender, in its live scene, and changes show on screen right away; `print()` output is the reply. Python errors come back with a traceback.
- If ping fails: ask the user to start the server in Blender (3D view > N panel > BlenderMCP > Connect) or to enable the "MCP for Blender" add-on. Never fall back to a headless run on a copy without saying so.
- Inspect first (`bpy.data.filepath`, object names, the active object, the mode), then change. Many operators need a mode or a selection: set them explicitly. Prefer the data API over `bpy.ops` for edits.
- Do not save (`bpy.ops.wm.save_mainfile`) unless the user asks; they can undo or save in Blender themselves.
- To see the result, render to a file under the project (`scene.render.filepath`, `bpy.ops.render.render(write_still=True)`) or take a screenshot of the screen, and look at it.

## Headless (no window)
For new files, renders, conversions and batches, run Blender itself in the background:

### Find Blender
Blender is usually not on PATH. Look, newest version first:
- `C:\Program Files\Blender Foundation\Blender <version>\blender.exe` (list the folder with `dir`)
- `%LOCALAPPDATA%\Programs\Blender Foundation\...`, or a Steam copy under `...\steamapps\common\Blender\blender.exe`
Use the newest unless the project names a version. Say which one you used.

### Run
Write the script into the project, then run (shell `cmd`):

```
"C:\Program Files\Blender Foundation\Blender 5.3\blender.exe" -b --factory-startup -noaudio --python-exit-code 1 -P tools\make.py -- out
```

- `-b`: no window. `--factory-startup`: ignore the user's settings and add-ons (repeatable runs, and the sandbox cannot write Blender's AppData).
- `--python-exit-code 1`: a Python error fails the command (otherwise Blender exits 0).
- Everything after `--` is the script's own arguments: `sys.argv[sys.argv.index("--") + 1:]`.
- To change an existing file, put it before `-P`: `blender.exe -b scene.blend -P edit.py`.
- Render a saved file's animation without a script: `blender.exe -b scene.blend -o //render/frame_#### -F PNG -a` (`-f 10` renders one frame).
- "Failed to remove ... extensions" lines come from the user's extensions folder; they are harmless.
- Rendering on the GPU (Cycles with OptiX or CUDA) may not work in the command sandbox; if a render is very slow or fails there, ask for `outside_sandbox` with that reason.

### Script template

```python
import bpy, os, sys
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
out = os.path.abspath(args[0] if args else "out")
os.makedirs(out, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)       # a clean, empty scene
scene = bpy.context.scene

bpy.ops.mesh.primitive_uv_sphere_add(radius=1, location=(0, 0, 1))
subject = bpy.context.object
bpy.ops.object.shade_smooth()
mat = bpy.data.materials.new("Base")
mat.use_nodes = True
mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.8, 0.3, 0.2, 1)
subject.data.materials.append(mat)

bpy.ops.mesh.primitive_plane_add(size=20)               # ground
bpy.ops.object.light_add(type="SUN", location=(5, -5, 8))
bpy.context.object.data.energy = 3

bpy.ops.object.camera_add(location=(0, -7, 3))
cam = bpy.context.object
aim = cam.constraints.new("TRACK_TO")                    # always frames the subject
aim.target, aim.track_axis, aim.up_axis = subject, "TRACK_NEGATIVE_Z", "UP_Y"
scene.camera = cam

world = bpy.data.worlds.new("World")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.5
scene.world = world

engines = bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items.keys()
scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"  # or "CYCLES"
scene.render.resolution_x, scene.render.resolution_y = 1280, 720
scene.render.filepath = os.path.join(out, "render.png")
bpy.ops.render.render(write_still=True)

bpy.ops.export_scene.gltf(filepath=os.path.join(out, "scene.glb"))
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "scene.blend"))
print("DONE", bpy.app.version_string)
```

## Common jobs
- Convert: `bpy.ops.import_scene.fbx` / `bpy.ops.wm.obj_import` / `bpy.ops.import_scene.gltf`, then `bpy.ops.export_scene.gltf(filepath=..., export_format="GLB")` or `bpy.ops.export_scene.fbx`.
- Batch: loop over files in one Blender run (reset with `read_factory_settings(use_empty=True)` between files) instead of one run per file.
- Inspect: print object names, vertex counts (`len(o.data.vertices)`), materials and bounding boxes (`o.dimensions`) and read the output.
- Decimate / clean: add a `DECIMATE` modifier and apply it with `bpy.ops.object.modifier_apply`, `bpy.ops.mesh.remove_doubles` in edit mode.
- Thumbnails: a small `resolution_percentage` and Eevee keep renders fast.

## Rules
- Check the result: open the rendered image (the browser tool or a screenshot) or print facts about the exported file. A zero exit code alone does not prove the scene looks right.
- Keep scripts in the project (for example `tools/`) so the user can run them again; write outputs under the project, never next to Blender.
- Never overwrite a user's `.blend` in place: save a copy unless they asked for the change.
- Use `bpy` operators that work without a window. Anything that needs `bpy.context.area` or a viewport (viewport screenshots, most UI operators) fails in `-b` mode; use the data API or a context override instead.

## Output
- The command run, the Blender version, and the files written, with a look at the main result.
