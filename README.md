# CSV Tool

CSV Tool is a fast, loss-minimizing CSV editor for Unity 6. It is editor-only;
CSV files remain the single source of truth.

Open **Window > CSV Tool**, then assign
[`Assets/Settings/CsvWorkspace.asset`](Assets/Settings/CsvWorkspace.asset).
The workspace lists configured tables. Each table points to a CSV `TextAsset`
and contains only its name, frozen physical row/column lists, and columns
automatically populated from the CSV header as name/reference pairs. Defaults
are column `0` and row `0`.

Select a table in the sidebar to open its grid. Double-click a body cell to edit
and use Save. Header cells are read-only. A lightweight filter changes visible
rows without changing physical edit coordinates.

The core document preserves unchanged bytes, including encoding and newline
style. Saves are atomic and refuse to overwrite a source file that changed
externally.

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and
[`docs/TESTING.md`](docs/TESTING.md) for project details.
