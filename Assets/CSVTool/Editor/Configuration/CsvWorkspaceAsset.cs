using System;
using System.Collections.Generic;
using System.IO;
using CsvTool.Schema;
using UnityEditor;
using UnityEngine;

namespace CsvTool.Editor.Configuration
{
    /// <summary>
    /// Unity-serializable token extraction settings.  Keeping this DTO separate
    /// from the schema assembly means the schema remains usable outside Unity.
    /// </summary>
    [Serializable]
    public sealed class CsvWorkspaceTokenSyntaxConfig
    {
        public CsvTokenExtractionMode mode = CsvTokenExtractionMode.WholeCell;
        public string separator = string.Empty;
        public bool trimWhitespace = true;
        public bool ignoreEmptyTokens = true;
        public string regexPattern = string.Empty;
        public int regexCaptureGroup = 1;

        public CsvTokenSyntax ToSchema()
        {
            return new CsvTokenSyntax(mode, separator, trimWhitespace, ignoreEmptyTokens)
            {
                RegexPattern = regexPattern ?? string.Empty,
                RegexCaptureGroup = regexCaptureGroup
            };
        }

        public static CsvWorkspaceTokenSyntaxConfig FromSchema(CsvTokenSyntax syntax)
        {
            CsvWorkspaceTokenSyntaxConfig result = new CsvWorkspaceTokenSyntaxConfig();
            if (syntax == null) return result;
            result.mode = syntax.Mode;
            result.separator = syntax.Separator ?? string.Empty;
            result.trimWhitespace = syntax.TrimWhitespace;
            result.ignoreEmptyTokens = syntax.IgnoreEmptyTokens;
            result.regexPattern = syntax.RegexPattern ?? string.Empty;
            result.regexCaptureGroup = syntax.RegexCaptureGroup;
            return result;
        }
    }

    /// <summary>One explicit link from a source column to a configured table.</summary>
    [Serializable]
    public sealed class CsvWorkspaceReferenceConfig
    {
        public string targetTable = string.Empty;
        public string targetKeyColumn = string.Empty;
        public string targetDisplayColumn = string.Empty;

        public CsvReferenceSpec ToSchema()
        {
            return new CsvReferenceSpec(targetTable, targetKeyColumn)
            {
                TargetDisplayColumn = targetDisplayColumn ?? string.Empty
            };
        }

        public static CsvWorkspaceReferenceConfig FromSchema(CsvReferenceSpec reference)
        {
            CsvWorkspaceReferenceConfig result = new CsvWorkspaceReferenceConfig();
            if (reference == null) return result;
            result.targetTable = reference.TargetTable ?? string.Empty;
            result.targetKeyColumn = reference.TargetKeyColumn ?? string.Empty;
            result.targetDisplayColumn = reference.TargetDisplayColumn ?? string.Empty;
            return result;
        }
    }

    /// <summary>
    /// Explicit metadata for a CSV column.  Name and index can both be set;
    /// index is authoritative and makes duplicate/unnamed headers addressable.
    /// </summary>
    [Serializable]
    public sealed class CsvWorkspaceColumnConfig
    {
        public string name = string.Empty;
        public int index = -1;
        public CsvWorkspaceTokenSyntaxConfig tokenSyntax = new CsvWorkspaceTokenSyntaxConfig();
        public List<CsvWorkspaceReferenceConfig> references = new List<CsvWorkspaceReferenceConfig>();

        // Pascal-case accessors make the DTO pleasant to use from editor code
        // while public fields keep Unity's built-in inspector serialization simple.
        public string Name { get { return name; } set { name = value ?? string.Empty; } }
        public int Index { get { return index; } set { index = value; } }
        public IList<CsvWorkspaceReferenceConfig> References { get { return references; } }

        public CsvColumnSchema ToSchema()
        {
            CsvColumnSchema result = new CsvColumnSchema(name)
            {
                Index = index,
                TokenSyntax = tokenSyntax == null ? null : tokenSyntax.ToSchema()
            };
            if (references != null)
                for (int i = 0; i < references.Count; i++)
                    result.References.Add(references[i] == null ? null : references[i].ToSchema());
            return result;
        }

        public static CsvWorkspaceColumnConfig FromSchema(CsvColumnSchema column)
        {
            CsvWorkspaceColumnConfig result = new CsvWorkspaceColumnConfig();
            if (column == null) return result;
            result.name = column.Name ?? string.Empty;
            result.index = column.Index;
            result.tokenSyntax = CsvWorkspaceTokenSyntaxConfig.FromSchema(column.TokenSyntax);
            result.references.Clear();
            if (column.References != null)
                for (int i = 0; i < column.References.Count; i++)
                    result.references.Add(CsvWorkspaceReferenceConfig.FromSchema(column.References[i]));
            return result;
        }
    }

    /// <summary>One CSV file and its record-view/presentation configuration.</summary>
    [Serializable]
    public sealed class CsvWorkspaceTableConfig
    {
        public string name = string.Empty;
        [Tooltip("The explicit CSV asset edited by this table. CSV files remain canonical.")]
        public TextAsset csvFile;
        public string relativePath = string.Empty;
        public string description = string.Empty;
        public bool enabled = true;
        public string identityColumn = string.Empty;
        public int identityColumnIndex = -1;
        public string displayColumn = string.Empty;
        public int displayColumnIndex = -1;
        public int frozenColumnCount;
        public List<int> frozenColumns = new List<int>();
        public List<int> frozenRows = new List<int>();
        public List<CsvWorkspaceColumnConfig> columns = new List<CsvWorkspaceColumnConfig>();

        public string Name { get { return name; } set { name = value ?? string.Empty; } }
        public string RelativePath { get { return relativePath; } set { relativePath = value ?? string.Empty; } }
        public string Description { get { return description; } set { description = value ?? string.Empty; } }
        public bool Enabled { get { return enabled; } set { enabled = value; } }
        public string IdentityColumn { get { return identityColumn; } set { identityColumn = value ?? string.Empty; } }
        public int IdentityColumnIndex { get { return identityColumnIndex; } set { identityColumnIndex = value; } }
        public string DisplayColumn { get { return displayColumn; } set { displayColumn = value ?? string.Empty; } }
        public int DisplayColumnIndex { get { return displayColumnIndex; } set { displayColumnIndex = value; } }
        public int FrozenColumnCount { get { return frozenColumnCount; } set { frozenColumnCount = Math.Max(0, value); } }
        public IList<int> FrozenColumns { get { return frozenColumns; } }
        public IList<int> FrozenRows { get { return frozenRows; } }
        public IList<CsvWorkspaceColumnConfig> Columns { get { return columns; } }

        public CsvTableSchema ToSchema()
        {
            CsvTableSchema result = new CsvTableSchema(name, relativePath)
            {
                Description = description ?? string.Empty,
                Enabled = enabled,
                IdentityColumn = identityColumn ?? string.Empty,
                IdentityColumnIndex = identityColumnIndex,
                DisplayColumn = displayColumn ?? string.Empty,
                DisplayColumnIndex = displayColumnIndex
            };
            if (columns != null)
                for (int i = 0; i < columns.Count; i++)
                    if (columns[i] != null) result.Columns.Add(columns[i].ToSchema());
            return result;
        }

        public static CsvWorkspaceTableConfig FromSchema(CsvTableSchema table)
        {
            CsvWorkspaceTableConfig result = new CsvWorkspaceTableConfig();
            if (table == null) return result;
            result.name = table.Name ?? string.Empty;
            result.relativePath = table.RelativePath ?? string.Empty;
            result.description = table.Description ?? string.Empty;
            result.enabled = table.Enabled;
            result.identityColumn = table.IdentityColumn ?? string.Empty;
            result.identityColumnIndex = table.IdentityColumnIndex;
            result.displayColumn = table.DisplayColumn ?? string.Empty;
            result.displayColumnIndex = table.DisplayColumnIndex;
            result.columns.Clear();
            if (table.Columns != null)
                for (int i = 0; i < table.Columns.Count; i++) result.columns.Add(CsvWorkspaceColumnConfig.FromSchema(table.Columns[i]));
            return result;
        }
    }

    /// <summary>
    /// A reusable, project-local definition of the CSV workspace.  The asset
    /// contains metadata only; CSV files remain the source of truth.
    /// </summary>
    [CreateAssetMenu(fileName = "CsvWorkspace", menuName = "CSV Tool/Workspace", order = 250)]
    public sealed class CsvWorkspaceAsset : ScriptableObject
    {
        [Tooltip("Project-relative (for example Assets/Data) or absolute directory containing the configured CSV files.")]
        public string rootDirectory = ".";
        public bool caseSensitiveNames;
        [Tooltip("Also show CSV files under the root that are not listed below. They remain fully editable as plain-text tables.")]
        public bool includeUnconfiguredTables;
        public List<CsvWorkspaceTableConfig> tables = new List<CsvWorkspaceTableConfig>();

        public string RootDirectory { get { return rootDirectory; } set { rootDirectory = value ?? string.Empty; } }
        public bool CaseSensitiveNames { get { return caseSensitiveNames; } set { caseSensitiveNames = value; } }
        public IList<CsvWorkspaceTableConfig> Tables { get { return tables; } }

        public CsvWorkspaceSchema ToSchema()
        {
            CsvWorkspaceSchema result = new CsvWorkspaceSchema
            {
                Name = string.IsNullOrWhiteSpace(name) ? "CSV Workspace" : name,
                // Explicit TextAsset references are authoritative. The project root is used
                // only to turn their Unity asset paths into stable filesystem paths.
                RootDirectory = ".",
                CaseSensitiveNames = caseSensitiveNames
            };
            if (tables != null)
                for (int i = 0; i < tables.Count; i++)
                    if (tables[i] != null)
                    {
                        CsvTableSchema table = tables[i].ToSchema();
                        if (tables[i].csvFile != null)
                            table.RelativePath = AssetDatabase.GetAssetPath(tables[i].csvFile);
                        result.Tables.Add(table);
                    }
            return result;
        }

        public CsvWorkspaceSchema CreateSchema() { return ToSchema(); }

        public static CsvWorkspaceAsset FromSchema(CsvWorkspaceSchema schema)
        {
            CsvWorkspaceAsset result = CreateInstance<CsvWorkspaceAsset>();
            result.ApplySchema(schema);
            return result;
        }

        public void ApplySchema(CsvWorkspaceSchema schema)
        {
            if (schema == null) throw new ArgumentNullException("schema");
            rootDirectory = schema.RootDirectory ?? string.Empty;
            caseSensitiveNames = schema.CaseSensitiveNames;
            tables.Clear();
            if (schema.Tables != null)
                for (int i = 0; i < schema.Tables.Count; i++) tables.Add(CsvWorkspaceTableConfig.FromSchema(schema.Tables[i]));
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(schema.Name)) name = schema.Name;
        }

        public bool TryResolveTablePath(CsvWorkspaceTableConfig table, out string absolutePath, out string error)
        {
            return TryResolveTablePath(table, CsvWorkspacePathResolver.GetProjectRoot(), out absolutePath, out error);
        }

        public bool TryResolveTablePath(CsvWorkspaceTableConfig table, string projectRoot, out string absolutePath, out string error)
        {
            return CsvWorkspacePathResolver.TryResolveTablePath(rootDirectory, table == null ? string.Empty : table.relativePath,
                projectRoot, out absolutePath, out error);
        }

    }

    /// <summary>Safe project/root-relative path resolution for workspace files.</summary>
    public static class CsvWorkspacePathResolver
    {
        public static string GetProjectRoot()
        {
            string dataPath = Application.dataPath;
            DirectoryInfo parent = Directory.GetParent(dataPath);
            return parent == null ? Path.GetFullPath(dataPath) : parent.FullName;
        }

        public static bool TryResolveRootDirectory(string configuredRoot, string projectRoot,
            out string absoluteRoot, out string error)
        {
            absoluteRoot = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                error = "Workspace root directory is empty.";
                return false;
            }
            try
            {
                string root = configuredRoot.Trim();
                bool projectRelative = !Path.IsPathRooted(root);
                string projectAbsolute = string.Empty;
                if (projectRelative)
                {
                    if (string.IsNullOrWhiteSpace(projectRoot))
                    {
                        error = "A project root is required for a project-relative workspace root.";
                        return false;
                    }
                    projectAbsolute = Path.GetFullPath(projectRoot);
                    root = Path.Combine(projectAbsolute, root.Replace('/', Path.DirectorySeparatorChar));
                }
                absoluteRoot = Path.GetFullPath(root);
                if (projectRelative && !IsWithin(absoluteRoot, projectAbsolute))
                {
                    absoluteRoot = string.Empty;
                    error = "A project-relative workspace root must stay inside the project directory.";
                    return false;
                }
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is NotSupportedException)
            {
                error = "Workspace root directory is invalid: " + exception.Message;
                return false;
            }
        }

        public static bool TryResolveTablePath(string configuredRoot, string tablePath, string projectRoot,
            out string absolutePath, out string error)
        {
            absolutePath = string.Empty;
            error = string.Empty;
            string absoluteRoot;
            if (!TryResolveRootDirectory(configuredRoot, projectRoot, out absoluteRoot, out error)) return false;
            if (string.IsNullOrWhiteSpace(tablePath))
            {
                error = "Table CSV path is empty.";
                return false;
            }
            try
            {
                string candidate = Path.IsPathRooted(tablePath)
                    ? Path.GetFullPath(tablePath)
                    : Path.GetFullPath(Path.Combine(absoluteRoot, tablePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithin(candidate, absoluteRoot))
                {
                    error = "Table path must stay inside the workspace root.";
                    return false;
                }
                absolutePath = candidate;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is NotSupportedException)
            {
                error = "Table CSV path is invalid: " + exception.Message;
                return false;
            }
        }

        private static bool IsWithin(string candidate, string root)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            StringComparison comparison = Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return candidate.Equals(root, comparison) || candidate.StartsWith(normalizedRoot, comparison);
        }
    }

    [CustomEditor(typeof(CsvWorkspaceAsset))]
    internal sealed class CsvWorkspaceAssetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("CSV files remain the source of truth. Configure table and column metadata here to enable typed editing, record views, autocomplete, and cross-table navigation.", MessageType.Info);
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();

            CsvWorkspaceAsset asset = (CsvWorkspaceAsset)target;
        }

    }
}
