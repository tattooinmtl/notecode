---
name: blender-scene
description: "Drive the Blender instance that is already running, through its MCP server: inspect the scene, build and edit objects, set materials and textures, render, and look up the bpy API in the vendored Blender manuals. Use when the user asks about a Blender scene, a .blend file, a 3D model, a render, or bpy code."
---

# Blender scene work

Blender is already running on this machine and its MCP server is already
connected to notecode. The tools below are the live ones - they talk to the
Blender that is open right now, not to a file.

## Before anything else

1. `blender_get_addon_info` - the handshake. It gives the add-on name, the
   version, the protocol and the Blender and Python versions. If it fails,
   Blender is not listening and nothing else will work either. Say so instead
   of guessing.
2. `blender_scene_info` - a JSON summary of the active scene: object counts by
   type, the frame range, the render engine, the resolution.

Then look before you touch. `blender_list_objects` lists the objects, and
`blender_get_object` gives the full descriptor of one: its transform, its
parent, its modifiers, its materials, its dimensions.

**Never assume a value you have not read.** Scene contents, the active object,
the selection and the current mode are all things that change between turns.
A scene that is wrong in the model's head is worse than no scene at all.

## The tools

| What you want | The tool |
|---|---|
| The handshake, versions | `blender_get_addon_info` |
| A summary of the scene | `blender_scene_info` |
| The objects, by type | `blender_list_objects` |
| One object in full | `blender_get_object` |
| A new mesh, empty, camera or light | `blender_create_object` |
| Remove an object and its orphan mesh | `blender_delete_object` |
| Move, rotate, scale | `blender_transform_object` |
| A material on an object | `blender_set_material` |
| An image on a material | `blender_set_texture` |
| Add a modifier | `blender_add_modifier` |
| Remove a modifier | `blender_remove_modifier` |
| Bring a model in from disk | `blender_import_asset` |
| Write the scene out to a file | `blender_export_asset` |
| Render the frame to a file | `blender_render_image` |
| A picture of the viewport | `blender_screenshot_viewport` |
| The current frame | `blender_frame_get` |
| Go to a frame | `blender_frame_set` |
| Run Python against the live `bpy` | `blender_execute_python` |

There is also a set of purpose-built builders on this server:
`blender_dominator_health_check`, `blender_dominator_setup_scene`,
`blender_dominator_build_unit`, `blender_dominator_list_units`,
`blender_dominator_build_building`, `blender_dominator_list_buildings`,
`blender_dominator_apply_nation_palette` and `blender_dominator_render_asset`.
They build DomiNations units and buildings procedurally. Use them when the user
is after that look, and read the list tools first to see what is on offer.

## Running Python

`blender_execute_python` runs a string of Python against the live `bpy`
module. It is AST-validated, so it is not a free-for-all shell, and it is
**the last resort**: when one of the tools above does the job, use that one.
The specific tools are safer, they keep the scene consistent, and they leave a
trace in the transcript that says what happened.

When you do need Python:

- Prefer operators (`bpy.ops`) for standard actions - adding primitives,
  applying modifiers, setting origins. They handle the defaults and the
  context. Use the data API (`bpy.data`) when you need precise control or want
  to avoid the side effects that come with the context.
- **Return structured data, not print output.** Give the function a `return`
  of a dict or a list. Print output is swallowed and the model gets nothing
  back to reason about.
- A run that raises is reported back as an error, and the scene is left as it
  was when the error happened - not as it was before.

## Things that bite in Blender

- **Objects and data are separate.** An Object references a Mesh, a Material,
  a Camera. Deleting the object does not delete the data, and the data with
  zero users is purged when the file is saved and reloaded. Editing a shared
  datablock changes every object that uses it - make it single-user first if
  that is not what you want.
- **The active object and the selection are different things.** Most operators
  want both, and they set them as a side effect. Set them explicitly before
  each operator call rather than assuming the state carried over.
- **Mode matters.** Many operators only work in Object mode, others only in
  Edit or Sculpt mode. In the wrong mode an operator either fails or does
  nothing at all. Check the mode first.
- **In Edit mode, use bmesh**, not the mesh data API, and flush the bmesh back
  into the mesh. Forgetting to flush loses the edit silently.
- **Update the dependency graph** after a change before reading a computed
  property - world matrices, modifier results, evaluated geometry.
- **Rotation mode decides the property.** Euler, quaternion and axis-angle are
  different properties on the same object. Writing to the one the object is
  not using is silently ignored.
- **Apply scale before geometry-dependent work.** Booleans and physics do
  surprising things under a non-uniform scale.
- **Names collide.** Blender renames a new object to `Cube.001` when `Cube` is
  taken. Capture the reference the moment the object is created; do not look it
  up by the name you expected.
- **Visibility has three separate states**: hidden in the viewport, disabled in
  the view layer, and disabled for render. An object can be in the scene and
  still be invisible, and an operator can skip it. Check all three when
  something is "missing".
- **Units are metres by default.** Check the scene's unit settings before
  building something dimensioned.
- **Objects created through the data API are not in the scene** until they are
  linked to a collection. Link it, or it will not render and will not appear.

## The manuals are here

The Blender API reference and the user manual are bundled in this workspace as
plain text:

- `vendor/blender-reference/api` - the Python API, one file per module and
  class (`bpy.types.Object.rst`, `bmesh.ops.rst`, and so on).
- `vendor/blender-reference/manual` - the user manual, in the same layout as
  the official manual.
- `vendor/blender-reference/addon` - the server's own Python, which is the
  ground truth for what the tools do: what `execute_python` allows through
  (`weak_sandbox.py`), how a long call is kept from blocking Blender
  (`deferred_tool.py`), and the handlers themselves
  (`mcp_to_blender_server.py`).

Look things up there instead of guessing an operator name. `grep_search` and
`file_glob_search` find a file or a line in them; with a provider that has no
such tool, `notecode_list_files` walks the folder and `notecode_read_file`
reads the file.

Check the name of the file first: the API folder is one file per class, so a
question about `Object` is answered by `bpy.types.Object.rst`.

## Working in notecode

- Save the result with `notecode_save_as` when the user wants a file, and open
  it in the editor with `notecode_open_in_editor` so they can see it.
- A render is a file on disk. Tell the user where it is, with
  `notecode_post_to_chat` if the path would otherwise be lost in the
  transcript.
- If you need to run something outside Blender - converting a file, checking a
  render - `notecode_run_command` runs it and brings back the output.
- When you are done, say what changed in the scene and what is still open.

## Do not

- Do not modify a scene destructively without saying what you are about to do.
- Do not delete objects that were already there because they are in the way.
- Do not render at full resolution "to check" - the user pays for the time.
- Do not guess at an API that you could look up in the manuals above.