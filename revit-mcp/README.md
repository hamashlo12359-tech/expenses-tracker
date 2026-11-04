# Revit 2025 MCP Bridge

Lets Claude (Claude Desktop or Claude Code) read and edit your open Autodesk Revit 2025 model.

```
Claude ──MCP/stdio──▶ server/index.js ──HTTP localhost:8765──▶ RevitMcpAddin (inside Revit) ──▶ Revit API
```

- **`RevitMcpAddin/`**: a C# (.NET 8) Revit 2025 add-in. On startup it listens on `http://localhost:8765` (local machine only) and runs each request on Revit's main thread through an `ExternalEvent`.
- **`server/`**: a Node.js MCP server that exposes Revit tools to Claude and forwards each call to the add-in.

Everything runs on **your Windows PC**. Nothing is sent anywhere except between Claude and Revit.

## Quick install (recommended)

1. Unzip or clone this folder anywhere on the Windows PC that has Revit 2025, for example `C:\RevitMcp`.
2. Double-click **`install.cmd`**. It:
   - installs the .NET 8 SDK and Node.js with winget if they're missing
   - builds the Revit add-in and installs it into `%AppData%\Autodesk\Revit\Addins\2025`
   - installs the MCP server
   - registers the server with Claude Desktop (backing up your existing config) and with Claude Code, if installed
3. Fully quit Claude Desktop and reopen it, then start Revit and click **Always Load**.

Don't move the folder after installing: Claude starts the server from its current location. If you do move it, run `install.cmd` again.

The manual steps below do the same thing.

## Requirements (on the Windows PC with Revit)

- Revit 2025
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), to build the add-in (it downloads the Revit 2025 API reference packages from NuGet, so the build doesn't depend on where Revit is installed)
- [Node.js 18+](https://nodejs.org)
- Claude Desktop or Claude Code

## 1. Build and install the Revit add-in

Close Revit first, then:

```powershell
git clone -b claude/mcp-revit-2025-hr54we https://github.com/hamashlo12359-tech/expenses-tracker.git
cd expenses-tracker\revit-mcp\RevitMcpAddin
dotnet build -c Release
```

The build copies files to `%AppData%\Autodesk\Revit\Addins\2025\`:
- `RevitMcp.addin`
- `RevitMcp\RevitMcpAddin.dll`

Start Revit 2025. The first time, Revit asks whether to load the add-in. Click **Always Load**.

To check that it's running, open http://localhost:8765/health in a browser. You should see `{"ok":true}`.

## 2. Install the MCP server

```powershell
cd ..\server
npm install
```

## 3. Connect Claude

**Claude Desktop**: edit `%AppData%\Claude\claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "revit": {
      "command": "node",
      "args": ["C:\\path\\to\\expenses-tracker\\revit-mcp\\server\\index.js"]
    }
  }
}
```

Then fully quit and reopen Claude Desktop.

**Claude Code**:

```powershell
claude mcp add revit -- node C:\path\to\expenses-tracker\revit-mcp\server\index.js
```

## 4. Try it

With a project open in Revit, ask Claude, for example:
- "Ping Revit and tell me what project is open."
- "List all levels and how many walls are on each."
- "Show me all rooms with their areas."
- "Create a 5 m wall from (0,0) to (5000,0) on Level 1."
- "Set the Comments parameter of the selected doors to 'Check fire rating'."

## Tools

| Tool | What it does |
|---|---|
| `ping` | Shows the Revit version and the active document |
| `get_project_info` | Project name, number, client, address, path |
| `get_active_view` / `list_views` | View info |
| `list_levels` | Levels with their elevations (mm) |
| `list_categories` | Model categories in use, with element counts |
| `get_elements` | Elements of a category, e.g. `OST_Walls`, `OST_Doors` |
| `get_selection` / `select_elements` | Read or set the UI selection |
| `get_element_parameters` | All parameters of an element |
| `set_element_parameter` | Set a parameter (lengths in mm, or strings like `"3000 mm"`) |
| `list_wall_types` / `create_wall` | Create straight walls (mm) |
| `create_level` | Add a level at an elevation (mm) |
| `delete_elements` | Delete elements by id |
| `get_rooms` | Rooms with area (m²) and volume (m³) |

Each change runs in its own named transaction (`MCP: ...`), so you can undo it with **Ctrl+Z** in Revit.

## Configuration

| Variable | Where | Default |
|---|---|---|
| `REVIT_MCP_PORT` | Environment of Revit (add-in) | `8765` |
| `REVIT_MCP_URL` | Environment of the MCP server | `http://localhost:8765` |
| `REVIT_MCP_TIMEOUT_MS` | Environment of the MCP server | `60000` |

## Troubleshooting

- **"Cannot reach Revit"**: Revit isn't running, the add-in didn't load (check for a startup dialog), or port 8765 is already in use.
- **"Revit is busy"**: Revit runs requests only when it's idle. Close any open dialog or finish the active command, such as wall placement, then try again.
- **Build can't download packages**: the build needs internet access to nuget.org the first time.

## Security

The add-in only listens on `localhost`, so other computers can't reach it. It also rejects requests from web browsers, so a website you visit can't send commands to Revit.
