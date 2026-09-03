# CSV Tool

CSV Tool is a fast, loss-minimizing CSV editor for Unity 6. It is an
editor-only tool: CSV files stay as ordinary project files and remain the
single source of truth.

## Open the tool

1. Open the project in Unity `6000.3.10f1`.
2. Choose **Window > CSV Tool**.
3. Assign the existing [`Assets/Settings/CsvWorkspace.asset`](Assets/Settings/CsvWorkspace.asset)
   in the workspace field at the top-left of the window.

Every session uses a workspace asset. The included workspace points to
`Assets/Data` and currently includes unconfigured CSV files automatically. For
another CSV root, create or edit a workspace asset with **Assets > Create > CSV
Tool > Workspace**, then configure its root, tables, columns, types, enums,
and references.

## Everyday editing

Click a cell to select it; double-click, Enter, or F2 edits it. Enter commits,
Escape cancels, and Tab commits then moves to the next cell. Copy/paste uses
spreadsheet-compatible TSV. Use Ctrl/Cmd+Z and Ctrl/Cmd+Shift+Z for grouped
undo/redo. Grid and Record views operate on the same document and history.

The toolbar **Filter** narrows visible records. Ctrl/Cmd+F opens a contextual
cell finder which ranks properties and values on the selected record first;
Ctrl/Cmd+G opens **Go to Column**. The optional **Inspector** keeps a compact
Record view beside the Grid. **All rows** includes comments, sections, and
blank separators. Enable **Edit non-data rows** before changing those existing
rows; it does not add or remove rows or columns, and the header remains read-only.
Ctrl/Cmd-click a configured reference to navigate to its target row.

## Project map

| Area | Responsibility |
| --- | --- |
| `Assets/CSVTool/Core` | Parser, document model, records, edit history |
| `Assets/CSVTool/Schema` | Unity-independent workspace and column schema |
| `Assets/CSVTool/Editor` | Window, grid, record view, indexing, recovery, workspace asset |
| `Assets/Editor/EditorUI` | Reusable IMGUI controls and styling |
| `Assets/CSVTool/**/Tests` | Unity EditMode tests |
| `Assets/Data` | Sample CSV corpus |
| `Assets/Settings/CsvWorkspace.asset` | Default workspace metadata |

More detail is in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), and test
commands are in [`docs/TESTING.md`](docs/TESTING.md). Future coding sessions
should start with [`AGENTS.md`](AGENTS.md) and [`MEMORY.md`](MEMORY.md).

## Data-safety guarantees

- An unchanged document serializes byte-for-byte identically.
- Saves use a temporary file and refuse to overwrite an externally changed
  source file.
- Unsaved edits are journaled under `Library/CsvTool/Recovery`.
- Duplicate or unnamed headers are addressed explicitly by physical index;
  ambiguous name-only selectors are rejected.

## Current scope

The first parsed row is always the header. Headerless CSV schemas are not yet
available through the workspace inspector. The tool is for editing in the
Unity Editor and does not import CSV data into runtime assets.
