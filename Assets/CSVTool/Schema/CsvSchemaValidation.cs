using System;
using System.Collections.Generic;

namespace CsvTool.Schema
{
    public enum CsvSchemaIssueSeverity
    {
        Warning = 0,
        Error = 1
    }

    public sealed class CsvSchemaIssue
    {
        public CsvSchemaIssue(CsvSchemaIssueSeverity severity, string code, string message, string tableName, string columnName)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            TableName = tableName ?? string.Empty;
            ColumnName = columnName ?? string.Empty;
        }

        public CsvSchemaIssueSeverity Severity { get; private set; }

        public string Code { get; private set; }

        public string Message { get; private set; }

        public string TableName { get; private set; }

        public string ColumnName { get; private set; }

        public override string ToString()
        {
            string location = string.IsNullOrEmpty(TableName) ? string.Empty : " [" + TableName;
            if (!string.IsNullOrEmpty(ColumnName))
            {
                location += "." + ColumnName;
            }
            if (!string.IsNullOrEmpty(location))
            {
                location += "]";
            }

            return Severity + " " + Code + location + ": " + Message;
        }
    }

    /// <summary>
    /// Validates configuration structure without opening or parsing CSV files.
    /// Header-dependent validation belongs to the document/index layer, because
    /// this API intentionally has no IO dependency.
    /// </summary>
    public static class CsvSchemaValidator
    {
        public static IReadOnlyList<CsvSchemaIssue> Validate(CsvWorkspaceSchema workspace)
        {
            List<CsvSchemaIssue> issues = new List<CsvSchemaIssue>();
            if (workspace == null)
            {
                issues.Add(Error("WORKSPACE_NULL", "Workspace schema is null.", string.Empty, string.Empty));
                return issues;
            }

            if (string.IsNullOrWhiteSpace(workspace.Name))
            {
                issues.Add(Warning("WORKSPACE_NAME_EMPTY", "Workspace has no display name.", string.Empty, string.Empty));
            }

            HashSet<string> tableNames = new HashSet<string>(workspace.CaseSensitiveNames
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase);
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int tableIndex = 0; tableIndex < workspace.Tables.Count; tableIndex++)
            {
                CsvTableSchema table = workspace.Tables[tableIndex];
                if (table == null)
                {
                    issues.Add(Error("TABLE_NULL", "Workspace contains a null table entry.", string.Empty, string.Empty));
                    continue;
                }

                string tableName = table.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(tableName))
                {
                    issues.Add(Error("TABLE_NAME_EMPTY", "Table name is required.", tableName, string.Empty));
                }
                else if (!tableNames.Add(tableName))
                {
                    issues.Add(Error("TABLE_NAME_DUPLICATE", "Table name is duplicated in the workspace.", tableName, string.Empty));
                }

                string path = table.RelativePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                {
                    issues.Add(Error("TABLE_PATH_EMPTY", "Relative CSV path is required.", tableName, string.Empty));
                }
                else if (!paths.Add(path))
                {
                    issues.Add(Error("TABLE_PATH_DUPLICATE", "More than one table points at the same CSV path.", tableName, string.Empty));
                }

                ValidateTable(workspace, table, issues);
            }

            return issues;
        }

        public static bool IsValid(CsvWorkspaceSchema workspace)
        {
            IReadOnlyList<CsvSchemaIssue> issues = Validate(workspace);
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i].Severity == CsvSchemaIssueSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }

        private static void ValidateTable(CsvWorkspaceSchema workspace, CsvTableSchema table, List<CsvSchemaIssue> issues)
        {
            if (table.IdentityColumnIndex < -1)
            {
                issues.Add(Error("IDENTITY_COLUMN_INDEX_INVALID", "Identity column index must be -1 or a zero-based index.", table.Name, table.IdentityColumn));
            }

            if (table.DisplayColumnIndex < -1)
            {
                issues.Add(Error("DISPLAY_COLUMN_INDEX_INVALID", "Display column index must be -1 or a zero-based index.", table.Name, table.DisplayColumn));
            }

            HashSet<string> namedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> indexedColumns = new HashSet<int>();
            for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                CsvColumnSchema column = table.Columns[columnIndex];
                if (column == null)
                {
                    issues.Add(Error("COLUMN_NULL", "Table contains a null column override.", table.Name, string.Empty));
                    continue;
                }

                string columnName = column.Name ?? string.Empty;
                if (!column.HasSelector)
                {
                    issues.Add(Error("COLUMN_SELECTOR_EMPTY", "Column override must specify Name, Index, or both.", table.Name, columnName));
                }

                if (column.Index < -1)
                {
                    issues.Add(Error("COLUMN_INDEX_INVALID", "Column index must be -1 or a zero-based index.", table.Name, columnName));
                }
                else if (column.Index >= 0 && !indexedColumns.Add(column.Index))
                {
                    issues.Add(Error("COLUMN_INDEX_DUPLICATE", "Column index is used by more than one override.", table.Name, columnName));
                }

                if (!string.IsNullOrWhiteSpace(columnName) && !namedColumns.Add(columnName))
                {
                    issues.Add(Error("COLUMN_NAME_DUPLICATE", "Column name is used by more than one override.", table.Name, columnName));
                }

                if (column.Minimum.HasValue && column.Maximum.HasValue && column.Minimum.Value > column.Maximum.Value)
                {
                    issues.Add(Error("COLUMN_RANGE_INVALID", "Minimum cannot be greater than maximum.", table.Name, columnName));
                }

                if ((column.ValueKind == CsvValueKind.List || column.ValueKind == CsvValueKind.Reference)
                    && (column.TokenSyntax == null || !column.TokenSyntax.IsConfigured))
                {
                    issues.Add(Error("TOKEN_SYNTAX_MISSING", "List/reference columns require configured token extraction metadata.", table.Name, columnName));
                }

                if (column.ValueKind == CsvValueKind.List
                    && column.TokenSyntax != null
                    && column.TokenSyntax.Mode != CsvTokenExtractionMode.Delimited)
                {
                    issues.Add(Error("LIST_EXTRACTION_MODE_INVALID", "List columns must use delimiter-separated token extraction.", table.Name, columnName));
                }

                if (column.TokenSyntax != null
                    && column.TokenSyntax.Mode == CsvTokenExtractionMode.RegexCapture
                    && column.TokenSyntax.RegexCaptureGroup < 0)
                {
                    issues.Add(Error("REGEX_CAPTURE_GROUP_INVALID", "Regex capture group must be zero or a positive group number.", table.Name, columnName));
                }

                if (column.ValueKind == CsvValueKind.Enum && column.EnumValues.Count == 0)
                {
                    issues.Add(Warning("ENUM_VALUES_EMPTY", "Enum column has no explicit autocomplete values.", table.Name, columnName));
                }

                for (int referenceIndex = 0; referenceIndex < column.References.Count; referenceIndex++)
                {
                    CsvReferenceSpec reference = column.References[referenceIndex];
                    if (reference == null || !reference.IsConfigured)
                    {
                        issues.Add(Error("REFERENCE_INCOMPLETE", "Reference must specify a target table and target key column.", table.Name, columnName));
                        continue;
                    }

                    CsvTableSchema target = workspace.FindTable(reference.TargetTable);
                    if (target == null)
                    {
                        issues.Add(Error("REFERENCE_TABLE_MISSING", "Reference target table is not in the workspace.", table.Name, columnName));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(target.IdentityColumn)
                        && !string.Equals(target.IdentityColumn, reference.TargetKeyColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(Warning("REFERENCE_KEY_DIFFERS_FROM_IDENTITY", "Reference target key differs from the target table identity column.", table.Name, columnName));
                    }
                }
            }
        }

        private static CsvSchemaIssue Error(string code, string message, string table, string column)
        {
            return new CsvSchemaIssue(CsvSchemaIssueSeverity.Error, code, message, table, column);
        }

        private static CsvSchemaIssue Warning(string code, string message, string table, string column)
        {
            return new CsvSchemaIssue(CsvSchemaIssueSeverity.Warning, code, message, table, column);
        }
    }
}
