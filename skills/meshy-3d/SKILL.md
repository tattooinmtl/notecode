---
name: meshy-3d
description: "Make 3D models with Meshy: generate from text or images, texture, rig, animate, remesh, convert, check printability, then hand the result to Blender or Unity. Use when the user asks for a 3D model, a mesh, a texture on a model, a rigged or animated character, a printable file, or wants Meshy connected to Blender or Unity."
---

# Meshy 3D

Meshy reaches notecode through two different servers. They do different jobs
and mixing them up is the usual way this goes wrong.

| Server | Transport | What it is | Tools |
|---|---|---|---|
| `meshy` | stdio | The Meshy **cloud API**. Generation, texturing, rigging, animation, conversion. Needs `MESHY_API_KEY`. | 28 |
| `meshy-dcc` | http `127.0.0.1:19765/mcp` | The Meshy **DCC gateway**. The live Blender and Unity instances that have registered themselves. | 4 |

The cloud API makes models. The gateway puts them where the user can see them.

## Before anything else

1. `meshy_check_balance` - free, and it tells you the credit balance for the
   key in use. Most generation calls cost credits, and some of them cost a lot
   (see the cost table below). Check first rather than discovering it from a
   402 error.
2. `meshy_get_task_status` on a task id you already have - the gateway's
   `search` tool tells you which Blender and Unity instances are live.

If `meshy_check_balance` fails with an authentication error, the key is
missing or wrong. It lives in `meshy/meshy-mcp-server/.env` as
`MESHY_API_KEY`, and it is read from that file when the server starts. The
server needs a Meshy **Pro plan or above** - the free plan has no API.

## Generation is three steps, not one

Every generation call is asynchronous. It returns a task id, not a model.

1. **Create** - `meshy_text_to_3d`, `meshy_image_to_3d`, `meshy_multi_image_to_3d`.
2. **Wait** - `meshy_get_task_status` with the task id. `wait=true` (the
   default) blocks until the task finishes; pass `wait=false` to poll once.
   Text-to-3D takes 2-3 minutes.
3. **Download** - `meshy_download_model` saves the output under `meshy_output/`
   (project folders with history) or at `save_to` if you give one.

`meshy_get_task_status` reports the task's **real** `task_type`. Pass that to
the download and cancel tools - do not assume it matches the tool you called,
because a refined preview and its source are different types.

## Text to 3D is two steps, and the second one costs money

`meshy_text_to_3d` produces an **untextured preview**. Texturing it is a
separate call, `meshy_text_to_3d_refine`, and it costs 10 credits (15 at 8K).

**Get the user's approval before you refine.** The preview exists so the user
can judge the shape; refining something they do not want spends their credits
on a model they will not keep.

`meshy_image_to_3d` is different - it is textured by default, so there is no
refine step.

## What things cost

Free: `meshy_check_balance`, `meshy_list_usage`, `meshy_list_animations`,
`meshy_analyze_printability`, `meshy_send_to_slicer`, `meshy_list_tasks`,
`meshy_list_models`.

1 credit: `meshy_convert`, `meshy_resize`.
3 credits: `meshy_animate` per action.
5 credits: `meshy_remesh`, `meshy_rig`, `meshy_uv_unwrap`.
10 credits: `meshy_retexture`, `meshy_text_to_3d_refine`,
`meshy_repair_printability`, `meshy_process_multicolor`, `meshy_auto_split`,
`meshy_text_to_motion` (3 with swift).

`meshy_list_usage` only works on a Studio or Enterprise team key; anything else
gets a 403. For a single key, `meshy_check_balance` is the one to use.

## Three tools the vendor's own instructions never mention

The Meshy server ships instructions to the agent, and those instructions name
25 of its 28 tools. They never mention `meshy_check_balance`,
`meshy_list_usage` or `meshy_cancel_task`. They are all real, they are all
registered, and they are the ones you want before an expensive call or when a
task is stuck. Do not conclude a tool is missing because the instructions are
silent about it.

## Getting a model into Blender

The `meshy` server writes files to disk. The `meshy-dcc` gateway drives the
Blender and Unity instances that are running.

1. Generate and download with the `meshy` server.
2. Find the live instances - `search` on the gateway, with `kind` set to
   instances rather than tools. An instance only exists while its host
   application is running.
3. `describe` the tool you want, to get its exact `tool_slug` and its
   `input_schema`. Do not guess a slug.
4. `call` it. `call` also takes a batch of up to 25 calls in `calls`.

`load_skill` is how you pull a skill into a live instance - the Blender
adapter alone carries 42 of them (blender-scene, blender-render,
blender-animation, blender-texture-bake and the rest).

The gateway deliberately publishes only four tools. It never fans out every
backend action, because the full set is far too large to hand to a model. Use
`search` to find things rather than expecting a tool per action.

If `search` returns no instances, the host application is not running. Start
Blender or open the Unity project, then search again.

## Unity

The Unity adapter is an editor package (`ai.meshy.dcc`) inside the project's
`Packages` folder. It is only live once Unity has opened the project - the
package publishes a bridge and the sidecar connects to that. A Unity project
with the package installed but Unity never opened still has no instance.

## Printing

`meshy_analyze_printability` is the authoritative answer to "is this
printable?" - it is free and checks watertightness, non-manifold edges, holes,
degenerate faces and volume. `meshy_repair_printability` fixes those but costs
10 credits and **removes the texture**, so retexture afterwards if it matters.

For a print workflow: call `meshy_send_to_slicer` at the **start** and keep
the result, then at the end replace `{file}` in a `launch_command` with the
downloaded file's path and run it.

## Pairs that are easy to confuse

- `meshy_remesh` changes polygon count or topology. `meshy_convert` changes
  file format. `meshy_resize` changes real-world size. Three different tools,
  three different credits.
- `meshy_list_tasks` lists tasks of any status. `meshy_list_models` lists
  finished 3D models only.
- `meshy_animate` needs a rigged character and takes `rig_task_id`. Rig it
  with `meshy_rig` first, or generate the motion with `meshy_text_to_motion`.
- `meshy_text_to_motion` is a skeleton motion, not a model.
- `meshy_animate` takes exactly one of `action_id`, `action_ids` or
  `motion_task_id`. `meshy_resize` takes exactly one of `resize_height`,
  `resize_longest_side` or `auto_size`.
