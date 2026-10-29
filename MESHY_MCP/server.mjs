#!/usr/bin/env node

// The Meshy MCP server, as installed by meshy_installer.exe.
//
// The server is Meshy's own (@meshy-ai/meshy-mcp-server, MIT). It is not part
// of notecode: it is installed separately, so the app stays small and the
// tools can be taken away without touching it. This file is the entry point
// the MCP client starts.
//
// Meshy's server reads MESHY_API_KEY from a .env file with dotenv, and dotenv
// looks for .env in the *current* folder. notecode starts MCP servers in its
// own folder, so the key would never be found. This file moves into its own
// folder first, where the installer keeps the .env, and then starts the
// server in this same process.
//
// Nothing is written to stdout here. Stdout is the MCP conversation, and a
// line that is not part of it breaks the client.

import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const server = join(here, "dist", "index.js");

if (!existsSync(server)) {
  process.stderr.write(
    `The Meshy server is missing. It should be at:\n  ${server}\n\n` +
      "Run meshy_installer.exe again to put it back.\n",
  );
  process.exit(1);
}

process.chdir(here);
await import(pathToFileURL(server).href);
