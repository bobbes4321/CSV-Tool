# Architecture

## Runtime boundaries

All CSV Tool assemblies are editor-only. The project separates pure data logic
from Unity-facing UI so parsing, schema validation, and most edit behavior can
be tested without drawing the window.

```text
CsvTool.Editor
  -> CsvTool.Core       document, parser, records, history
  -> CsvTool.Schema     workspace metadata and schema validation
  -> Neo.EditorUI       shared IMGUI controls

CsvTool.Editor
  -> Configuration      ScriptableObject workspace bridge
  -> Window              table/workspace controllers and editor window
  -> Grid / Record      two projections over one document
  -> Index               autocomplete and reference-value lookup
  -> Recovery            on-disk journal for unsaved grouped edits
```

`CsvTool.Core` and `CsvTool.Schema` use `noEngineReferences`. Keep Unity APIs
out of those assemblies; put project path resolution, `ScriptableObject`
serialization, `EditorPrefs`, and `EditorWindow` code in `CsvTool.Editor`.

## Data flow

1. `CsvWorkspaceController` resolves CSV files from a configured
   `CsvWorkspaceAsset`.
2. `CsvTableController` loads one file into `CsvDocument`, applies schema
   resolution, and maintains search/visible-row state.
3. `CsvGrid` or `CsvRecordView` renders the same physical document and emits
   edit events.
4. The controller applies edits through the document/history layer, updates
   indexes and views, and writes a recovery journal while dirty.
5. `CsvDocument.Save` serializes atomically after checking the original file
   fingerprint; a successful save clears the journal.

## Schema and references

Schema is intentionally explicit because CSV has no type or relationship
metadata. A column can select a physical index, a header name, or both. If both
are present, the index is authoritative and the name is retained as a
diagnostic/display hint.

Reference resolution is also explicit: source value kind, token extraction,
target table, target key column, and optional display column are configured in
the workspace. Missing and ambiguous targets are reported rather than guessed.

## Invariants to preserve

- Physical record/column coordinates remain stable while a search filter is
  active.
- No-op edits do not dirty the document or rewrite bytes.
- Grouped edits are one undoable operation and apply atomically when protected
  or read-only cells are involved.
- Structural rows are never edited accidentally.
- Workspace path resolution cannot escape its configured project/root folder.
