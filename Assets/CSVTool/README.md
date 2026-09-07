# CSV Tool

CSV Tool is an editor-only Unity window for editing CSV files in place. Open it
from **Window > CSV Tool**. The CSV file remains the source of truth.

Create or select a `CSV Tool/Workspace` asset. Add table entries and assign each
one its CSV `TextAsset`. A table has only a display name, a CSV file reference,
frozen physical column indices, frozen physical row indices, and columns
automatically copied from the CSV header as name/reference pairs.

The first column and header row are the defaults for the frozen lists. Select a
table in the window sidebar to open its grid. Double-click a body cell to edit,
then use Save. Header cells are read-only.

Saving remains atomic and refuses to overwrite a file that changed externally.
Unchanged files retain their original bytes, including encoding and newline
style.
