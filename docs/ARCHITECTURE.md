# Architecture

CSV Tool has three assemblies:

```text
CsvTool.Editor
  -> CsvTool.Core
  -> CsvTool.Schema
  -> Configuration, Window, Grid, Search, Index, Record, Recovery

CsvTool.Schema
  -> explicit schema DTOs, resolution, and configuration validation
```

`CsvTool.Core` contains the Unity-free CSV document, parser, records, history,
and conflict-aware atomic save implementation. `CsvTool.Schema` is also
Unity-free and holds explicit typed/reference metadata. `CsvTool.Editor`
contains the workspace `ScriptableObject`, table controllers, editor window,
grid, quick inspector, indexes, navigation, and recovery integration.

The workspace stores direct `TextAsset` references and presentation metadata
only. CSV files remain canonical. Schema metadata is explicit: it never guesses
relationships from column names. Columns are discovered from the first CSV
record and retain their physical zero-based references.

The window loads only the selected table. The grid emits physical cell edits to
the table controller; the controller is the only editor-side writer and uses
the core document for undo/redo and atomic saves. Dataset validation and the
separate full-record mode are intentionally excluded; the quick inspector
remains available alongside the grid.
