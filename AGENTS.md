# Agent instructions

## Project

CSV Tool is an editor-only Unity package for editing CSV files in place. The
project targets Unity `6000.3.10f1`. The main entry point is **Window > CSV
Tool**. The CSV files remain the source of truth; do not introduce a second
asset-backed copy of each record.

Read [`MEMORY.md`](MEMORY.md) for durable project context and
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) before making cross-cutting
changes.

## Working rules

- Preserve the existing loss-minimizing behavior: unchanged files must retain
  their original bytes, including BOM and newline style.
- Keep physical row and column indices distinct from filtered/visual indices.
  UI filtering must never change which CSV row an edit targets.
- Keep Core and Schema free of Unity dependencies. Editor integration belongs
  in the Editor assemblies.
- Treat configured paths, identity columns, duplicate headers, and references
  as explicit configuration. Do not add name-based guessing when an index or
  target is ambiguous.
- Keep saves atomic and conflict-aware. Never silently overwrite a file changed
  externally after it was opened.
- Do not hand-edit generated `*.csproj`/`*.sln` files; regenerate them from
  Unity when needed.
- Do not commit Unity-generated folders such as `Library`, `Temp`, `Logs`,
  `obj`, or `UserSettings`.
- Avoid changing sample CSV data while testing. Use temporary files for tests
  that write to disk.

## Validation

The authoritative test runner is Unity Test Runner in EditMode. From a shell,
the equivalent command is:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe' `
  -batchmode -nographics -quit `
  -projectPath 'C:\Unity Projects\CSV Tool' `
  -runTests -testPlatform editmode `
  -testResults 'Temp\csvtool-editmode-results.xml' `
  -logFile 'Temp\csvtool-editmode.log'
```

Do not start a second Unity process against an already open project. If the
batch command exits before producing a result file, close the interactive
Unity editor and retry. See [`docs/TESTING.md`](docs/TESTING.md) for the test
assembly map and fallback checks.

## Unity MCP

The project includes MCP for Unity through the `com.coplaydev.unity-mcp`
package dependency in `Packages/manifest.json`. Codex uses the local HTTP
endpoint configured in `%USERPROFILE%\\.codex\\config.toml`:

```toml
[mcp_servers.unityMCP]
url = "http://127.0.0.1:8080/mcp"
```

In Unity, open **Window > MCP for Unity**, use **Auto-Setup**, and start the
bridge if it is stopped. Do not start a separate Unity instance for this
project just to activate MCP; use the already-open editor.

For a read-only connectivity check, confirm that the MCP endpoint accepts an
initialize request, then inspect these resources in order:

- `mcpforunity://instances` — must list the `CSV Tool` editor instance and
  Unity `6000.3.10f1`.
- `mcpforunity://project/info` — must report project root
  `C:/Unity Projects/CSV Tool`.
- `mcpforunity://editor/state` — should report `ready_for_tools=true` and no
  `stale_status` blocking reason.

An additional safe round-trip check is the `read_console` tool with
`action="get"`. A stale editor-state snapshot can persist briefly; retry it
before treating the bridge as disconnected. Never use MCP mutations during a
connectivity check, and do not clear or alter sample project data.

## Change hygiene

Before editing, inspect the current working tree and preserve unrelated user
changes. After editing, report the files changed and the validation actually
performed. Update `MEMORY.md` when an architectural decision, limitation, or
workflow changes.
