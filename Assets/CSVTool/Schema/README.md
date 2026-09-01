# CSV Tool schema configuration

This folder contains the project-independent configuration primitives used by
the CSV editor. A `CsvWorkspaceSchema` names the CSV tables in a workspace;
each `CsvTableSchema` can declare identity/display columns and optional
`CsvColumnSchema` overrides. It is a separate `noEngineReferences` assembly,
so tests and non-Unity consumers can reuse the same configuration model.

Identity and display selectors support both a header name and a zero-based
column index (`IdentityColumnIndex` / `DisplayColumnIndex`). Either selector is
optional. If both forms are supplied, the index takes precedence; the name is
still useful as a label and diagnostic hint. Index selectors are the reliable
way to address unnamed or duplicate headers. The validator checks only that an
index is `-1` or non-negative; the document layer checks whether it falls within
the parsed header row.

## Why configuration is explicit

CSV has no type, relationship, or list syntax metadata. The editor should not
guess that a column named `abilities` points at `abilities.csv`, or that every
semicolon-separated value is a list. Add a column override and a
`CsvReferenceSpec` when a relationship matters:

```csharp
CsvWorkspaceSchema workspace = new CsvWorkspaceSchema
{
    Name = "Game data",
    RootDirectory = "Assets/Data"
};

CsvTableSchema abilities = new CsvTableSchema("abilities", "abilities.csv")
{
    IdentityColumn = "name",
    DisplayColumn = "name"
};
abilities.Columns.Add(new CsvColumnSchema("name") { ValueKind = CsvValueKind.Text });
workspace.Tables.Add(abilities);

CsvTableSchema units = new CsvTableSchema("units", "units.csv")
{
    IdentityColumn = "name",
    DisplayColumn = "name"
};
CsvColumnSchema unitAbilities = new CsvColumnSchema("abilities")
{
    ValueKind = CsvValueKind.List,
    TokenSyntax = new CsvTokenSyntax(";", true, true)
};
unitAbilities.References.Add(new CsvReferenceSpec("abilities", "name"));
units.Columns.Add(unitAbilities);
workspace.Tables.Add(units);
```

Run `CsvSchemaValidator.Validate(workspace)` before opening the workspace. It
checks table/column selectors, duplicate names or paths, ranges, token syntax,
list/reference extraction modes, and that explicit reference target tables
exist. Identity/display names and reference display names are intentionally not
required to have column overrides here: headers may be inferred from the CSV.
Header-dependent checks (such as a configured name actually appearing in a
parsed file) belong to the CSV document layer and are intentionally not
performed here.

## Extraction modes

`CsvTokenSyntax` is explicit so the editor does not need to guess a project's
embedded value grammar:

* `WholeCell` — one scalar token containing the entire cell (the default).
* `Delimited` — split on `Separator`, such as `;` or `|`; used by `List`.
* `FirstWhitespaceToken` — take the text before the first whitespace, useful
  for values such as `Morita_MkI 0`.
* `RegexCapture` — apply `RegexPattern` and use `RegexCaptureGroup` (group 0
  is the complete match; positive numbers select capture groups).

For example, a scalar reference whose cell contains `Morita_MkI 0` can use:

```csharp
CsvColumnSchema weapon = new CsvColumnSchema("weapon")
{
    ValueKind = CsvValueKind.Reference,
    TokenSyntax = new CsvTokenSyntax(CsvTokenExtractionMode.FirstWhitespaceToken)
};
weapon.References.Add(new CsvReferenceSpec("weapons", "name"));
```

The API has no `UnityEngine` dependency. This is intentional: a future editor
assembly can expose the same data through a `ScriptableObject`, JSON, or another
configuration UI without coupling parsers and tests to Unity serialization.
