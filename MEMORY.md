# Project memory

Last reviewed: 2026-09-01

## Current state

CSV Tool is a Unity 6 editor extension, not a runtime CSV importer. It opens
from **Window > CSV Tool** and edits CSV files directly. The default workspace
asset is [`Assets/Settings/CsvWorkspace.asset`](Assets/Settings/CsvWorkspace.asset),
rooted at `Assets/Data`, with unconfigured tables included by default.

The implementation already includes:

- loss-minimizing parsing and serialization, including BOM/newline preservation;
- physical-row editing with filtering and structural-row classification;
- grouped undo/redo, atomic saves, and external-change conflict detection;
- recovery journals under `Library/CsvTool/Recovery`;
- physical row and column insertion with undo/redo, Grid toolbar controls, and spreadsheet-style Grid context menus;
- Grid and Record views sharing a document, selection, history, recovery, and
  save path;
- contextual Ctrl/Cmd+F cell finding, Ctrl/Cmd+G column navigation, and a
  compact Record inspector beside the Grid, all using physical coordinates;
- explicit workspace/table/column metadata, typed validation, enum values,
  token extraction, and cross-table references;
- lazy value indexes and autocomplete;
- EditMode coverage for Core, Schema, Editor, Index, Record, Recovery, and
  workspace configuration behavior.

The editor requires a `CsvWorkspaceAsset`; the workspace is the explicit
source for the CSV root and optional table/column metadata. Ad-hoc folder mode
is not exposed in the window.

## Important decisions

1. CSV files are the canonical data. Workspace assets contain metadata only.
2. Physical column indices take precedence over header names. Name-only lookup
   reports duplicate headers as ambiguous.
3. The first parsed row is the header row. Headerless schemas are not exposed
   yet.
4. **All rows** controls visibility of comments, sections, and blank rows;
   **Edit non-data rows** separately unlocks edits to those existing rows. It
   does not add or remove rows or columns, and headers remain read-only.
5. References and list syntax are opt-in. The editor does not infer that a
   column name or separator implies a relationship.
6. Core and Schema are `noEngineReferences` assemblies so their tests and
   configuration model remain reusable outside Unity.
7. Navigation results may use fuzzy/display-name matching, but every result
   resolves to a physical record and column before selection or editing.
8. Cell navigation carries an explicit viewport policy. Column-only navigation
   preserves the selected row's screen position; same-table row jumps reveal
   minimally or center distant targets, and cross-table references center both
   axes. Deferred navigation must retain physical coordinates and report any
   filter/row-visibility state it changes.
8. Full Record mode composes a record navigator with a shared `CsvRecordForm`;
   the Grid inspector reuses only the form so it does not duplicate record browsing.
9. Grid and Record editing have explicit, view-local edit sessions. When selection moves
   between surfaces, the outgoing session is finalized before the mirrored physical selection
   changes; autocomplete overlays may only exist for their owning session.

## Known limitations

- The editor currently uses the first parsed row as the header; headerless CSV
  schemas need a future explicit configuration path.
- `dotnet test` is not a supported validation route here: the generated Unity
  projects target .NET Framework/Unity assemblies and this machine has no
  .NET SDK installed.
- Unity batch tests require the project not to be open in another Unity
  process. Use the Unity Test Runner when the editor is already open.
- Recovery journal format 1 intentionally does not persist structural insertions;
  the editor warns that row/column insertions are not crash-recoverable until saved.

At this review, Unity `6000.3.10f1` was already running against the project, so
the batch invocation exited before creating a result XML. No code changes were
made during this setup pass; the existing editor state was left untouched.

## Next useful work

- Add a deliberate headerless-schema model if the data corpus needs it.
- Add more end-to-end UI tests around Grid/Record mode switching and reference
  navigation.
- Keep this file current whenever save semantics, workspace configuration, or
  test workflow changes.
