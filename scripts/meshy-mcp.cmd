@echo off
rem ---------------------------------------------------------------------------
rem Launches the Meshy MCP server for notecode.
rem
rem Why a launcher instead of pointing the config straight at node:
rem
rem   - The Meshy server reads its .env with dotenv, and dotenv resolves .env
rem     against the *current working directory*. notecode starts MCP servers
rem     with its own working directory, so a .env next to the server would
rem     never be found and MESHY_API_KEY would arrive empty.
rem   - Notecode's McpServerDef has no working-directory field, so the cd
rem     happens here.
rem
rem Nothing may be written to stdout by this script. This process speaks
rem JSON-RPC on stdout and a single stray line would corrupt the stream.
rem ---------------------------------------------------------------------------
cd /d "%~dp0..\meshy\meshy-mcp-server"
node dist\index.js
