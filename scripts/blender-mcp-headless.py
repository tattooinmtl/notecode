# Brings the Blender MCP server up in a headless Blender.
#
#   blender.exe --background --python scripts\blender-mcp-headless.py
#
# Blender is a desktop application of its own and its add-on serves the MCP
# endpoint on 127.0.0.1:8765 only while Blender is open - and only after the
# user has pressed "Start Server" in the add-on's preferences. That is fine
# for a person who works with Blender, and it is a problem for a test: the
# Blender checks in scripts\ui-blender-skill.ps1 need the server to be there.
#
# This starts the server without a window. The add-on is enabled for this
# session only, so the user's own Blender preferences are not touched, and
# nothing appears on their screen.
#
# The tools that touch bpy need Blender's main thread, so this is enough for
# the catalogue - which is what the test reads - and not for driving a scene.
# For that, open Blender and press Start.

import addon_utils

addon_utils.enable("blender_mcp_addon", default_set=False, persistent=True)

import blender_mcp_addon

server = blender_mcp_addon.get_server()
server.start(host="127.0.0.1", port=8765, auth_token="")
print("[notecode] blender mcp server up on", server.endpoint, flush=True)

import time

while True:
    time.sleep(1)
