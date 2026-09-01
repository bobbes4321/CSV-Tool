# Testing

## Unity Test Runner

Use Unity `6000.3.10f1` and open **Window > General > Test Runner**. Run the
EditMode tests. The test assemblies are split by concern:

- `CsvTool.Core.Tests`
- `CsvTool.Schema.Tests`
- `CsvTool.Editor.Tests`
- `CsvTool.Editor.Index.Tests`
- `CsvTool.Editor.Record.Tests`
- `CsvTool.Recovery.Tests`
- `CsvTool.Configuration.Tests`

The tests cover CSV quoting and multiline values, byte-preserving round trips,
undo/redo, external conflicts, structural rows, schema validation and
resolution, clipboard parsing, autocomplete, references, recovery journals,
and workspace path/configuration conversion.

## Batch mode

When no Unity editor is using the project, run:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe' `
  -batchmode -nographics -quit `
  -projectPath 'C:\Unity Projects\CSV Tool' `
  -runTests -testPlatform editmode `
  -testResults 'Temp\csvtool-editmode-results.xml' `
  -logFile 'Temp\csvtool-editmode.log'
```

Inspect the XML result file and the log after the process exits. If the
project is already open, Unity can terminate before creating the result file;
close the interactive editor and retry. Batch logs/results under `Temp` are
generated artifacts and are ignored by the repository rules.

## Fallback checks

The generated `CSV Tool.sln` and `*.csproj` files are for IDE navigation and
are not an independent build system. `dotnet test` is not expected to work in
this environment because no .NET SDK is installed and Unity references are
resolved by the Unity editor. If Unity is unavailable, limit validation to
static inspection and document that the EditMode suite was not run.
