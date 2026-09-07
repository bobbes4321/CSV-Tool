# Project memory

Last reviewed: 2026-09-07

## Current state

CSV Tool is a Unity 6 editor extension, not a runtime CSV importer. It opens
from **Window > CSV Tool** and edits CSV files directly. The workspace asset is
[`Assets/Settings/CsvWorkspace.asset`](Assets/Settings/CsvWorkspace.asset).

The workspace contains only explicitly configured table entries. Each has a
direct CSV `TextAsset` reference, physical frozen-column metadata, and
optional explicit schema metadata (types, enums, references, and identity
columns). The first parsed row supplies the displayed column labels.

The window exposes a table sidebar, a virtualized grid, and an optional
quick-record inspector for the selected row. It also supports range selection,
clipboard editing, autocomplete, contextual find, column navigation, and
explicit cross-table reference navigation. The selected table is loaded on
demand. The grid emits physical cell edits; the core document owns values,
undo/redo, byte-preserving serialization, atomic saves, and external-change
protection.

## Important decisions

1. CSV files are canonical. The workspace stores metadata and file references,
   never a second copy of records.
2. Column and row references are physical zero-based indices.
3. Schema metadata is optional presentation/reference metadata; it never blocks
   edits or runs dataset validation.
4. Core and editor code remain separated; `CsvTool.Core` has no Unity dependency.
5. Only configured tables are shown. There is no folder scan or implicit table
   discovery.
6. The quick inspector is retained; the separate full record-view mode and
   dataset validation/Issues panel are intentionally not exposed.

## Performance validation

`CsvPerformanceTests.ProfileImportantCsvActions` measures load, filtering,
distinct-value indexing, contextual search, dirty-change inspection, and
one-cell serialization against `units.csv` plus a temporary synthetic
20k-by-20 fixture. It also measures virtualized grid range calculation,
physical-cell reveal/visibility checks, edit commit, and representative TSV
parse/format/paste-preflight work. `CsvGridRenderProfileWindow` additionally
profiles real IMGUI repaint frames; the grid render path was measured at about
32.6 ms before rendering changes and about 24.8 ms after batching gridlines,
caching the selection range, and using direct `GUIStyle.Draw` calls. The same
repaint profile on the real `units.csv` measured about 9.8 ms. It is in the
`Performance` category so the normal EditMode suite remains fast; run it
separately through the already-open Unity editor or the batch runner when no
Unity process is using the project.

## Validation

The Unity EditMode suites cover Core, Schema, workspace configuration, grid
navigation/clipboard, indexing, record projection, and recovery. The project
must not already be open in another Unity process when using batch mode. See
[`docs/TESTING.md`](docs/TESTING.md).
