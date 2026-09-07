# Removal ledger

This simplification removed the following project surface:

- the independent Schema assembly and all schema DTOs, resolution, validation,
  and schema documentation;
- table identity/display configuration and all typed column options, enum
  values, ranges, visibility, read-only overrides, descriptions, grouping,
  ordering, token extraction, and regular-expression parsing;
- cross-table reference metadata, reference resolution, reference navigation,
  reference autocomplete, and value indexes;
- Record view, compact Record inspector, contextual find, column navigation,
  row/column insertion menus, recovery journals, dataset diagnostics, and
  workspace folder scanning;
- all tests that covered the removed editor features; and
- the large feature-heavy workspace asset representation.

The remaining editor surface is the workspace asset, table sidebar, grid,
physical frozen row/column lists, automatic header column discovery, and the
core CSV document's editing, undo/redo, byte-preserving serialization, atomic
save, and external-change protection. Core EditMode tests remain.
