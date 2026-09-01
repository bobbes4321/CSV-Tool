# CSV Tool

A fast, loss-minimizing CSV editor for Unity. Open it from **Window > CSV Tool**.

## Quick start

Create **Assets > Create > CSV Tool > Workspace** and select the asset in the
field at the top-left of the window. A workspace defines the CSV root and can
add metadata for tables and columns that need richer behavior.

In a workspace inspector, **Populate Missing Tables From Root** adds table entries for CSV files that are not configured yet. `Include Unconfigured Tables` keeps newly added files visible even before metadata is supplied.

## Everyday controls

- Click a cell to select it; double-click, Enter, or F2 to edit.
- Enter commits, Escape cancels, and Tab commits then moves to the next cell.
- Shift-click or Shift+arrow selects a range. Copy and paste use spreadsheet-compatible TSV text.
- Ctrl/Cmd+Z and Ctrl/Cmd+Shift+Z undo and redo grouped edits.
- The header and leading configured columns stay frozen while scrolling.
- The toolbar **Filter** narrows visible records. Ctrl/Cmd+F finds properties and values, prioritizing the selected record.
- Ctrl/Cmd+G opens **Go to Column**, including configured names, groups, and the selected record's current values.
- Toggle **Grid / Record** for a spreadsheet or ScriptableObject-style form projection over the same row.
- Toggle **Inspector** in Grid mode to edit the selected record in a compact panel without losing grid context.
- Comments, sections, and blank separators can be hidden with **All rows**.
- While editing, autocomplete appears as you type. Ctrl/Cmd+Space opens it explicitly.
- Ctrl/Cmd-click a configured reference cell to navigate to its target row.

Grid and Record views share one document, selection model, undo history, recovery journal, and save path.

## Configuring columns

A table entry identifies a CSV by a path relative to the workspace root. Its identity and display selectors control record labels and reference presentation.

A column entry can configure:

- physical index and/or header name;
- display name, help text, grouping, and order;
- Text, Integer, Decimal, Boolean, Enum, List, Reference, or Multiline Text behavior;
- hidden, read-only, required, minimum, and maximum constraints;
- explicit enum choices;
- token extraction and one or more cross-table references.

Physical indices are zero-based and authoritative. Use an index for blank or duplicate headers. A name-only selector that matches duplicate headers is reported as ambiguous instead of silently editing the wrong column.

Typed controls validate values but do not normalize existing CSV text. For example, `0012`, intentional whitespace, and a user's decimal spelling remain unchanged unless the user explicitly chooses a replacement value.

## Configuring a reference

For a source column such as `abilities`:

1. Set its value kind to **Reference**.
2. Choose a token mode. Use **Whole Cell** for one key or **Delimited** plus the exact separator for a list.
3. Add a reference with the target table name and target key header.
4. Optionally provide a target display header for friendlier navigation labels.

Target table names must match workspace table names. If a target key header is blank or duplicated, add a target column configuration with that name and an explicit physical index. Missing and ambiguous targets are never guessed.

Autocomplete combines configured enum values, configured reference keys, and cached values already present in the same column. Delimited reference completion replaces only the active final token, preserving earlier tokens and separators.

## Data safety

- An unchanged document serializes byte-for-byte identically, including BOM and newline style.
- Saves are atomic and refuse to overwrite a file that changed externally after it was opened.
- Unsaved cell edits are journaled under `Library/CsvTool/Recovery` and offered after a reload or editor restart.
- Header rows are always read-only. Comment, section, and blank rows require the explicit **Edit structure** toggle.

The current parser treats the first parsed row as a header. Headerless CSV schemas are not yet exposed in the workspace inspector.

For project-level developer guidance, see the root [`README.md`](../../README.md),
[`AGENTS.md`](../../AGENTS.md), and [`MEMORY.md`](../../MEMORY.md).

## Performance model

Only visible grid cells are drawn. CSV parsing, filtering, and reference/value indexes use physical rows and columns; autocomplete indexes are lazy and invalidated per edited column. The tool does not create a Unity asset per record and does not import the CSV into a second source of truth.
