#!/usr/bin/env node
// MCP server for Autodesk Revit 2025.
// Speaks MCP over stdio to Claude, and forwards each tool call as JSON over
// HTTP to the RevitMcp add-in running inside Revit (default http://localhost:8765).

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const REVIT_URL = (process.env.REVIT_MCP_URL || "http://localhost:8765").replace(/\/+$/, "");
const TIMEOUT_MS = Number(process.env.REVIT_MCP_TIMEOUT_MS || 60000);

async function callRevit(command, args = {}) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), TIMEOUT_MS);
  try {
    const res = await fetch(`${REVIT_URL}/command`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ command, args }),
      signal: ctrl.signal,
    });
    const body = await res.json().catch(() => ({ ok: false, error: `HTTP ${res.status}` }));
    if (!res.ok || body.ok === false) throw new Error(body.error || `HTTP ${res.status}`);
    return body.result;
  } catch (err) {
    if (err.name === "AbortError") throw new Error(`Revit did not respond within ${TIMEOUT_MS} ms (is a modal dialog open?)`);
    if (err.cause?.code === "ECONNREFUSED" || /fetch failed/.test(err.message)) {
      throw new Error(
        `Cannot reach Revit at ${REVIT_URL}. Make sure Revit 2025 is open with a project loaded and the RevitMcp add-in is installed.`
      );
    }
    throw err;
  } finally {
    clearTimeout(timer);
  }
}

const server = new McpServer({ name: "revit-2025", version: "1.0.0" });

function tool(name, description, inputSchema, command = name) {
  server.registerTool(name, { description, inputSchema }, async (args) => {
    try {
      const result = await callRevit(command, args ?? {});
      return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
    } catch (err) {
      return { isError: true, content: [{ type: "text", text: String(err.message || err) }] };
    }
  });
}

const id = z.number().int().describe("Revit ElementId (integer)");

tool("ping", "Check that Revit is reachable and return the Revit version and active document title.", {});

tool("get_project_info", "Get the active project's info: name, number, client, address, file path, units.", {});

tool("get_active_view", "Get the active view's name, type, level, and scale.", {});

tool("list_views", "List views in the project.", {
  viewType: z.string().optional().describe("Optional ViewType filter, e.g. FloorPlan, ThreeD, Section, Sheet, Schedule"),
});

tool("list_levels", "List all levels with their elevations (in millimetres).", {});

tool("list_categories", "List model categories that have at least one element in the project, with counts.", {});

tool(
  "get_elements",
  "Get elements of a category (e.g. OST_Walls, OST_Doors, OST_Windows, OST_Rooms, OST_Floors, OST_StructuralColumns). Returns id, name, type, level.",
  {
    category: z.string().describe("BuiltInCategory name, e.g. OST_Walls"),
    viewOnly: z.boolean().optional().describe("Only elements visible in the active view"),
    limit: z.number().int().optional().describe("Max elements to return (default 200)"),
  }
);

tool("get_selection", "Get the elements currently selected in Revit.", {});

tool("select_elements", "Select (and highlight) the given elements in the Revit UI.", {
  ids: z.array(id),
});

tool("get_element_parameters", "Get all parameters (name, value, storage type, read-only) of an element.", {
  id,
});

tool(
  "set_element_parameter",
  "Set a parameter value on an element. Numbers for length parameters are interpreted in millimetres unless raw=true.",
  {
    id,
    parameter: z.string().describe("Parameter name as shown in Revit properties"),
    value: z.union([z.string(), z.number()]),
    raw: z.boolean().optional().describe("If true, numeric values are written in Revit internal units (feet)"),
  }
);

tool("list_wall_types", "List available wall types (id and name).", {});

tool("create_wall", "Create a straight wall between two points (millimetres) on a level.", {
  startX: z.number(), startY: z.number(),
  endX: z.number(), endY: z.number(),
  levelId: id.optional().describe("Level id; defaults to the lowest level"),
  wallTypeId: id.optional().describe("Wall type id; defaults to the document default"),
  heightMm: z.number().optional().describe("Unconnected height in mm (default 3000)"),
  structural: z.boolean().optional(),
});

tool("create_level", "Create a new level at an elevation (millimetres).", {
  elevationMm: z.number(),
  name: z.string().optional(),
});

tool("delete_elements", "Delete elements by id. This cannot be undone from Claude (use Ctrl+Z in Revit).", {
  ids: z.array(id),
});

tool("get_rooms", "List rooms with number, name, level, area (m²) and volume (m³).", {});

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`revit-mcp-server running on stdio → ${REVIT_URL}`);
