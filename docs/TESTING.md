# Testing

The Unity EditMode suites cover the Core document plus Schema, workspace
configuration, editor navigation/clipboard behavior, indexing, record
projection, and recovery. Dataset validation is intentionally not part of this
test surface. Use Unity `6000.3.10f1` when the project is not already open:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.10f1\Editor\Unity.exe' `
  -batchmode -nographics -quit `
  -projectPath 'C:\Unity Projects\CSV Tool' `
  -runTests -testPlatform editmode `
  -testResults 'Temp\csvtool-editmode-results.xml' `
  -logFile 'Temp\csvtool-editmode.log'
```

The generated Unity solution and project files are for IDE navigation only.
