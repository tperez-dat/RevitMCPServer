# Revit MCP Server

An MCP server that lets an AI assistant read and modify an open Autodesk Revit model.

It exposes **42 tools** over the Model Context Protocol: 24 read/query tools, 3 selection and view
interaction tools, 11 modelling and editing tools, 2 document operations, and 2 export/integration
tools.

Supports **Revit 2025, 2026 and 2027**. Writes are off until you enable them in Revit.

## How it works

Revit's API can only be called from Revit's own main thread, inside a valid API context. A pipe
listener runs on a worker thread, where touching a `Document` crashes Revit rather than throwing. So
this is two processes:

```
  MCP client                MCP server                    Revit.exe
  (VS Code)                 (RevitMCPServer.exe)          (RevitMCPBridge add-in)
      |                            |                              |
      |  MCP over stdio            |   named pipe                 |
      |<-------------------------->|   \\.\pipe\RevitMCPBridge    |
      |                            |<---------------------------->|
      |                            |   length-prefixed JSON       |
                                                                  |
                                         pipe thread ──► ExternalEvent ──► Revit main thread
                                                                            (API context,
                                                                             one transaction
                                                                             per write)
```

Every request is queued and marshalled onto Revit's main thread through an
`IExternalEventHandler`; the pipe thread blocks on the result with a timeout. Each model write runs
in a single transaction named after the command, so one Ctrl+Z in Revit reverses it.

| Project | What it is |
| --- | --- |
| `src/RevitMCP.Contracts` | Wire protocol: command catalogue, request/response envelope, framing, paging |
| `src/RevitMCPBridge` | The Revit add-in: pipe server, dispatcher, 41 command handlers |
| `src/RevitMCPServer` | The MCP server: stdio transport, 42 tool definitions, pipe client |
| `tests/RevitMCP.Tests` | 52 tests covering framing, paging, the catalogue, and the pipe round-trip |

## Requirements

| | |
| --- | --- |
| Revit | 2025, 2026, or 2027 (Windows only) |
| .NET SDK | **8** for Revit 2025/2026, **10** for Revit 2027 — Revit 2027 moved to .NET 10 |
| Python | 3.9+ with `pymupdf`, only for `EXTRACT_PDF_GEOMETRY` |

The Revit API assemblies come from reference-only NuGet packages, so you do not need to copy
`RevitAPI.dll` out of your Revit install, and nothing Autodesk-licensed is redistributed here.

## Build and install

```powershell
# 1. Build and install the Revit add-in (close Revit first — it locks the assembly)
.\build\deploy-addin.ps1                    # all three versions
.\build\deploy-addin.ps1 -RevitVersions 2026 # or just one

# 2. Publish the MCP server
.\build\build-server.ps1
```

Start Revit. You should see an **MCP Bridge** ribbon tab with two buttons: **Allow MCP Writes** and
**Bridge Status**. The add-in writes a session file with a fresh token to
`%LOCALAPPDATA%\RevitMCPBridge\session.json` at startup, and logs to `bridge.log` beside it.

## Connect an MCP client

The MCP server talks to Revit over a **local** named pipe, so it must run on the same machine as
Revit. That rules some clients in and others out:

| Client | Works | Why |
| --- | --- | --- |
| **VS Code** (Copilot agent mode, or the Claude Code extension) | Yes | Launches a local stdio MCP server |
| Claude Desktop | Yes | Same, if you are permitted to install it |
| **claude.ai / Claude in the browser** | **No** | Cloud clients reach only remote MCP servers over HTTPS. Making this one reachable would mean exposing your workstation's Revit bridge to the internet — don't |
| **VIKTOR.AI** | **No** | It is a platform for building engineering web apps, not an MCP client. It could call this server's logic through its own code, but not as MCP |

For VS Code, add to `.vscode/mcp.json` in your workspace (or your user `mcp.json`):

```jsonc
{
  "servers": {
    "revit": {
      "type": "stdio",
      "command": "C:\\path\\to\\artifacts\\server-publish\\RevitMCPServer.exe",
      "env": {
        // Only needed if python is not on PATH, or you want a specific interpreter for PDF work.
        "REVIT_MCP_PYTHON": "C:\\Python312\\python.exe"
      }
    }
  }
}
```

Then ask the assistant to call `BRIDGE_STATUS` — it answers even when Revit is busy, so it is the
right first check.

## Write safety

Any process running as your user can connect to a local named pipe. Reading a model is one thing;
silently changing it is another. So:

- **Reads always work.** Queries need no opt-in.
- **Writes are refused until you turn them on**, per Revit session, from the ribbon toggle. Nothing
  persists it — every Revit restart is back to read-only.
- Each write is one named transaction (`MCP: CREATE_WALL`), so it appears as a single, labelled,
  undoable step in Revit.
- The pipe is ACL'd to your user account, and every request must carry the per-session token from
  the session file.

Write tools tell the model exactly what to do when refused, so it asks you rather than retrying.

## Tools

Lengths and coordinates are **decimal feet** throughout — Revit's internal unit, and the imperial
unit this project reports. Parameters come back with both the raw stored value and Revit's own
formatted display string (`8.5` and `8' - 6"`), so you can compute or quote.

Every list tool is paged: pass the returned `cursor` back for the next page.

### Query / read

| Tool | Returns |
| --- | --- |
| `GET_PROJECT_INFO` | Name, number, client, address, status, path, Revit version, phase |
| `LIST_ELEMENTS` | Element instances in a category |
| `COUNT_ELEMENTS` | Count only, without listing |
| `GET_ELEMENT_PROPERTIES` | Every instance parameter, plus type, level, workset, design option |
| `GET_ELEMENT_TYPE_PROPERTIES` | Type-level parameters (accepts an instance id or a type id) |
| `GET_ELEMENT_LOCATION` | Point, curve, or bounding box |
| `FIND_BY_PARAM` | Elements matching a parameter value |
| `GET_MODEL_WARNINGS` | Warnings with severity and failing element ids |
| `LIST_CATEGORIES` | Categories, optionally with element counts |
| `LIST_LEVELS` | Levels in elevation order |
| `LIST_PHASES` | Phases in sequence order |
| `LIST_WORKSETS` | Worksets (says so plainly if the model is not workshared) |
| `LIST_LINKED_MODELS` | Linked models with load status and path |
| `GET_ACTIVE_VIEW` | The view the user is looking at, and its sheet |
| `LIST_VIEWS` | Views, filterable by type or name |
| `LIST_SHEETS` | Sheets with revision and view count |
| `GET_SHEET_CONTENTS` | Views and schedules placed on a sheet |
| `GET_SCHEDULES` | Schedules with their columns (excludes titleblock revision schedules) |
| `LIST_FAMILIES` | Loadable families, with type counts |
| `LIST_FAMILY_TYPES` | Element types, loadable and system |
| `LIST_MATERIALS` | Materials with class and colour |
| `GET_MATERIAL_PROPERTIES` | One material in full, including structural and thermal assets |
| `GET_SELECTION` | What is selected in Revit |
| `BRIDGE_STATUS` | Bridge health — answers even while Revit is blocked |

### Selection and view interaction — *requires write mode*

| Tool | Does |
| --- | --- |
| `SET_SELECTION` | Selects elements, optionally scrolling them into view |
| `OPEN_VIEW` | Activates a view by id or name |
| `ISOLATE_IN_VIEW` | Temporary isolate, or clears one with `reset` |

### Modelling — *requires write mode*

| Tool | Creates |
| --- | --- |
| `CREATE_WALL` | A wall from two points, a level, a height and a type |
| `CREATE_GRID` | A grid line, optionally named |
| `CREATE_STRUCTURAL_COLUMN` | A column, with base/top levels and offsets |
| `CREATE_STRUCTURAL_FRAMING` | A beam between two points |
| `CREATE_FLOOR` | A floor from a boundary polygon |
| `CREATE_DRAFT_DETAIL` | Detail lines, arcs, circles, text, filled regions and detail components, from a JSON payload |
| `CREATE_LEVEL` | A level, optionally with a matching floor plan |
| `CREATE_SHEET` | A sheet, with a titleblock or as a placeholder |
| `PLACE_VIEW_ON_SHEET` | A view or schedule placed on a sheet |

### Editing — *requires write mode*

| Tool | Does |
| --- | --- |
| `SET_ELEMENT_PARAMETER` | Sets one parameter on one or more elements, reporting the value Revit actually stored |
| `DELETE_ELEMENT` | Deletes elements, reporting the full cascade. `dryRun` shows the blast radius first |

### Document operations — *requires write mode*

| Tool | Does |
| --- | --- |
| `SAVE_MODEL` | Saves in place. Never does a Save As |
| `SYNC_WITH_CENTRAL` | Synchronises a workshared model, relinquishing borrowed elements by default |

### Export / integration

| Tool | Does |
| --- | --- |
| `EXPORT_SCHEDULE_TO_CSV` | Exports a schedule. Sets the delimiter explicitly — Revit's exporter writes tab-separated text by default |
| `EXTRACT_PDF_GEOMETRY` | Pulls linework and positioned text off a PDF. Needs no Revit at all |

## Things worth knowing

- **Family types must already be loaded.** The modelling tools place what is in the project; they
  cannot load a family that is not there. Call `LIST_FAMILY_TYPES` first. When a name does not match,
  the error lists what is available, so the assistant can correct itself in one step.
- **`FIND_BY_PARAM` needs a category.** Parameter names cannot be pushed into a Revit filter, so it
  walks elements. Scope it, or pass `allCategories` and accept a slow, capped scan.
- **Walls ignore the z you pass** — a wall sits on its level. Use `offset`. Beams do keep their z.
- **`CREATE_DRAFT_DETAIL` coordinates default to the view plane**, so a detail reads the same in a
  plan, a section or a drafting view. Pass `coordinateSpace: "model"` for raw model coordinates.
- **A full circle is two arcs.** Revit has no closed arc curve, so `circle` returns two ids.
- **`EXTRACT_PDF_GEOMETRY` returns PDF points** (72/inch). Divide by 864 for feet.
- **`SET_ELEMENT_PARAMETER` has two value forms.** `value` is text and goes through Revit's own
  parser, so it honours display units — `"8' 6\""` works. `rawValue` is a number in raw internal
  units (feet, radians). Give exactly one. For Yes/No parameters, `rawValue: 1` or `0` is reliable.
  To change a **type** parameter, pass the type's id — which affects every instance of that type.
- **`DELETE_ELEMENT` cascades.** Deleting a wall deletes its hosted doors and windows. Call it with
  `dryRun: true` first to see the dependents; the response always lists every id actually removed.
- **`SAVE_MODEL` and `SYNC_WITH_CENTRAL` run outside a transaction**, because Revit refuses a save
  while one is open. On a workshared model, `SAVE_MODEL` writes the local file only.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| "No Revit bridge session file" | Revit is not running, or the add-in did not load. Check `%LOCALAPPDATA%\RevitMCPBridge\bridge.log` |
| "changes the model or the UI, and write mode is off" | Enable **Allow MCP Writes** on the ribbon |
| "Revit did not reach an API context" | Revit is showing a modal dialog or running a long command. Dismiss it and retry |
| "Timed out connecting to the pipe" | Revit closed, or another client holds the single allowed connection |
| No **MCP Bridge** ribbon tab | The add-in failed to load. The log and the Revit add-in error dialog say why |
| Tools missing in VS Code | The server could not start. Its logs go to stderr; check the MCP output panel |

## Licensing

- **Revit API**: referenced from NuGet reference assemblies only. Autodesk's licence does not permit
  redistributing `RevitAPI.dll`, and this repo does not contain it.
- **PyMuPDF** (`EXTRACT_PDF_GEOMETRY` only): **AGPL-3.0, or a commercial licence from Artifex**. It
  is invoked as a subprocess rather than linked, which keeps that obligation at the script boundary —
  but if you ship this to clients, read the AGPL terms or buy a commercial licence. Every other tool
  works without it.

## Development

```bash
dotnet build                                    # whole solution (defaults to Revit 2026)
dotnet build src/RevitMCPBridge -p:RevitVersion=2027
dotnet test                                     # 39 tests, no Revit required
```

The add-in cross-compiles on Linux (`EnableWindowsTargeting`) and the test suite runs there, because
.NET implements named pipes over Unix domain sockets — so the transport, framing and handshake are
all testable off-Windows. Only actually driving Revit needs Windows.
