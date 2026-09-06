using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using CsvTool.Core;
using CsvTool.Editor;
using CsvTool.Schema;

namespace CsvTool.Editor.Validation
{
    /// <summary>The severity of a dataset validation diagnostic.</summary>
    public enum CsvDatasetDiagnosticSeverity
    {
        Warning = 0,
        Error = 1
    }

    /// <summary>
    /// A stable, physical location for one dataset validation result. A
    /// negative record or column means that the diagnostic is about table
    /// configuration rather than a particular cell.
    /// </summary>
    public sealed class CsvDatasetDiagnostic
    {
        public CsvDatasetDiagnostic(CsvDatasetDiagnosticSeverity severity, string code,
            string message, string tableName, int physicalRecordIndex, int physicalColumnIndex)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            TableName = tableName ?? string.Empty;
            PhysicalRecordIndex = physicalRecordIndex;
            PhysicalColumnIndex = physicalColumnIndex;
        }

        public CsvDatasetDiagnosticSeverity Severity { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public string TableName { get; private set; }
        public int PhysicalRecordIndex { get; private set; }
        public int PhysicalColumnIndex { get; private set; }

        // Short aliases make the machine-readable contract convenient for
        // callers while retaining the explicit physical-coordinate names.
        public string Table { get { return TableName; } }
        public int PhysicalRecord { get { return PhysicalRecordIndex; } }
        public int PhysicalColumn { get { return PhysicalColumnIndex; } }

        public override string ToString()
        {
            return Severity + " " + Code + " [" + TableName + ", record "
                + PhysicalRecordIndex + ", column " + PhysicalColumnIndex + "]: " + Message;
        }
    }

    /// <summary>Raised by a save gate when dataset diagnostics contain errors.</summary>
    public sealed class CsvDatasetValidationException : InvalidOperationException
    {
        private readonly IReadOnlyList<CsvDatasetDiagnostic> diagnostics;

        public CsvDatasetValidationException(IReadOnlyList<CsvDatasetDiagnostic> diagnostics)
            : base("CSV save was blocked by dataset validation: " + FormatSummary(diagnostics))
        {
            this.diagnostics = diagnostics ?? new CsvDatasetDiagnostic[0];
        }

        public IReadOnlyList<CsvDatasetDiagnostic> Diagnostics { get { return diagnostics; } }

        public static bool HasErrors(IReadOnlyList<CsvDatasetDiagnostic> diagnostics)
        {
            for (int i = 0; diagnostics != null && i < diagnostics.Count; i++)
                if (diagnostics[i].Severity == CsvDatasetDiagnosticSeverity.Error) return true;
            return false;
        }

        private static string FormatSummary(IReadOnlyList<CsvDatasetDiagnostic> diagnostics)
        {
            for (int i = 0; diagnostics != null && i < diagnostics.Count; i++)
                if (diagnostics[i].Severity == CsvDatasetDiagnosticSeverity.Error)
                    return diagnostics[i].Code + " at " + diagnostics[i].TableName +
                        " record " + diagnostics[i].PhysicalRecordIndex + ".";
            return "unknown error.";
        }
    }

    /// <summary>
    /// Validates opened tables using their already-resolved physical schema.
    /// This service has no Unity dependency and is suitable for editor and
    /// automation callers. It never mutates a document or writes a file.
    /// </summary>
    public static class CsvDatasetValidator
    {
        public static IReadOnlyList<CsvDatasetDiagnostic> Validate(
            IEnumerable<CsvTableController> tables)
        {
            List<CsvTableController> openedTables = Materialize(tables);
            List<CsvDatasetDiagnostic> diagnostics = new List<CsvDatasetDiagnostic>();

            for (int tableIndex = 0; tableIndex < openedTables.Count; tableIndex++)
            {
                CsvTableController table = openedTables[tableIndex];
                ValidateTable(table, openedTables, diagnostics);
            }

            return diagnostics.AsReadOnly();
        }

        public static IReadOnlyList<CsvDatasetDiagnostic> Validate(
            params CsvTableController[] tables)
        {
            return Validate((IEnumerable<CsvTableController>)tables);
        }

        private static List<CsvTableController> Materialize(IEnumerable<CsvTableController> tables)
        {
            List<CsvTableController> result = new List<CsvTableController>();
            if (tables == null) return result;
            foreach (CsvTableController table in tables) result.Add(table);
            return result;
        }

        private static void ValidateTable(CsvTableController table,
            IList<CsvTableController> allTables, List<CsvDatasetDiagnostic> diagnostics)
        {
            if (table == null)
            {
                Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "TABLE_NULL",
                    "The validation set contains a null table.", string.Empty, -1, -1);
                return;
            }

            if (table.Document == null)
            {
                // An unopened workspace table is still retained in the validation set so
                // reference validation can report an unopened required target. It is not
                // itself a reason to block saving an unrelated currently-open table.
                return;
            }

            CsvResolvedTableSchema resolved = table.ResolvedSchema;
            if (resolved == null)
            {
                Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "TABLE_SCHEMA_UNRESOLVED",
                    "The table has no resolved schema.", table.Name, -1, -1);
                return;
            }

            AddSchemaDiagnostics(table, resolved, diagnostics);
            ValidateConfiguredColumns(table, resolved, diagnostics);
            ValidateIdentity(table, resolved, diagnostics);

            for (int columnIndex = 0; columnIndex < resolved.Columns.Count; columnIndex++)
            {
                CsvResolvedColumn column = resolved.Columns[columnIndex];
                if (column == null || !column.IsResolved || column.Schema == null
                    || column.Schema.References.Count == 0) continue;

                for (int recordIndex = 0; recordIndex < table.Document.Records.Count; recordIndex++)
                {
                    if (table.Document.Records[recordIndex].Kind != CsvRecordKind.Data) continue;
                    ValidateReferences(table, recordIndex, column, allTables, diagnostics);
                }
            }
        }

        private static void AddSchemaDiagnostics(CsvTableController table,
            CsvResolvedTableSchema resolved, List<CsvDatasetDiagnostic> diagnostics)
        {
            for (int i = 0; i < resolved.Diagnostics.Count; i++)
            {
                CsvSchemaIssue issue = resolved.Diagnostics[i];
                int physicalColumn = FindIssueColumn(resolved, issue.ColumnName);
                Add(diagnostics,
                    issue.Severity == CsvSchemaIssueSeverity.Error
                        ? CsvDatasetDiagnosticSeverity.Error
                        : CsvDatasetDiagnosticSeverity.Warning,
                    issue.Code, issue.Message, table.Name, -1, physicalColumn);
            }
        }

        private static int FindIssueColumn(CsvResolvedTableSchema resolved, string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            if (resolved.Identity != null && resolved.Identity.IsResolved
                && string.Equals(name, resolved.Table.IdentityColumn, StringComparison.OrdinalIgnoreCase))
                return resolved.Identity.PhysicalIndex;
            for (int i = 0; i < resolved.Columns.Count; i++)
            {
                CsvResolvedColumn column = resolved.Columns[i];
                if (column != null && column.IsResolved && column.Schema != null
                    && string.Equals(name, column.Schema.Name, StringComparison.OrdinalIgnoreCase))
                    return column.PhysicalIndex;
            }
            return -1;
        }

        private static void ValidateConfiguredColumns(CsvTableController table,
            CsvResolvedTableSchema resolved, List<CsvDatasetDiagnostic> diagnostics)
        {
            for (int i = 0; i < resolved.Columns.Count; i++)
            {
                CsvResolvedColumn column = resolved.Columns[i];
                if (column == null || !column.IsResolved || column.Schema == null) continue;
                CsvColumnSchema schema = column.Schema;

                if ((schema.Minimum.HasValue || schema.Maximum.HasValue)
                    && schema.ValueKind != CsvValueKind.Integer
                    && schema.ValueKind != CsvValueKind.Decimal)
                {
                    Add(diagnostics, CsvDatasetDiagnosticSeverity.Error,
                        "SCHEMA_RANGE_KIND_UNSUPPORTED",
                        "Minimum and maximum require an Integer or Decimal value kind.",
                        table.Name, -1, column.PhysicalIndex);
                }

                for (int recordIndex = 0; recordIndex < table.Document.Records.Count; recordIndex++)
                {
                    if (table.Document.Records[recordIndex].Kind != CsvRecordKind.Data) continue;
                    string value = table.GetCell(recordIndex, column.PhysicalIndex) ?? string.Empty;
                    ValidateValue(table, recordIndex, column.PhysicalIndex, value, schema, diagnostics);
                }
            }
        }

        private static void ValidateValue(CsvTableController table, int recordIndex,
            int physicalColumn, string value, CsvColumnSchema schema,
            List<CsvDatasetDiagnostic> diagnostics)
        {
            bool empty = string.IsNullOrWhiteSpace(value);
            if (schema.Required && empty)
            {
                Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_REQUIRED_MISSING",
                    "A required value is empty.", table.Name, recordIndex, physicalColumn);
            }
            if (empty) return;

            long integerValue;
            double decimalValue;
            switch (schema.ValueKind)
            {
                case CsvValueKind.Integer:
                    if (!CsvRecordValueParser.TryParseInteger(value, out integerValue))
                    {
                        Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_TYPE_INVALID",
                            "Value '" + value + "' is not a valid integer.", table.Name,
                            recordIndex, physicalColumn);
                        return;
                    }
                    ValidateRange(table, recordIndex, physicalColumn, integerValue,
                        schema, diagnostics);
                    break;
                case CsvValueKind.Decimal:
                    if (!CsvRecordValueParser.TryParseDecimal(value, out decimalValue)
                        || double.IsNaN(decimalValue) || double.IsInfinity(decimalValue))
                    {
                        Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_TYPE_INVALID",
                            "Value '" + value + "' is not a valid decimal.", table.Name,
                            recordIndex, physicalColumn);
                        return;
                    }
                    ValidateRange(table, recordIndex, physicalColumn, decimalValue,
                        schema, diagnostics);
                    break;
                case CsvValueKind.Boolean:
                    bool booleanValue;
                    if (!CsvRecordValueParser.TryParseBoolean(value, out booleanValue))
                    {
                        Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_TYPE_INVALID",
                            "Value '" + value + "' is not a valid boolean.", table.Name,
                            recordIndex, physicalColumn);
                    }
                    break;
                case CsvValueKind.Enum:
                    if (!ContainsEnumValue(schema.EnumValues, value))
                    {
                        Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_ENUM_INVALID",
                            "Value '" + value + "' is not one of the configured enum values.",
                            table.Name, recordIndex, physicalColumn);
                    }
                    break;
            }
        }

        private static bool ContainsEnumValue(IList<string> values, string value)
        {
            if (values == null) return false;
            for (int i = 0; i < values.Count; i++)
                if (string.Equals(values[i], value, StringComparison.Ordinal)) return true;
            return false;
        }

        private static void ValidateRange(CsvTableController table, int recordIndex,
            int physicalColumn, double value, CsvColumnSchema schema,
            List<CsvDatasetDiagnostic> diagnostics)
        {
            if (schema.Minimum.HasValue && value < schema.Minimum.Value)
            {
                Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_RANGE_BELOW_MINIMUM",
                    "Value is below the configured minimum of " + schema.Minimum.Value.ToString(CultureInfo.InvariantCulture) + ".",
                    table.Name, recordIndex, physicalColumn);
            }
            if (schema.Maximum.HasValue && value > schema.Maximum.Value)
            {
                Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_RANGE_ABOVE_MAXIMUM",
                    "Value is above the configured maximum of " + schema.Maximum.Value.ToString(CultureInfo.InvariantCulture) + ".",
                    table.Name, recordIndex, physicalColumn);
            }
        }

        private static void ValidateIdentity(CsvTableController table,
            CsvResolvedTableSchema resolved, List<CsvDatasetDiagnostic> diagnostics)
        {
            if (resolved.Identity == null || !resolved.Identity.IsResolved) return;
            int identityColumn = resolved.Identity.PhysicalIndex;
            Dictionary<string, int> firstRecords = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int recordIndex = 0; recordIndex < table.Document.Records.Count; recordIndex++)
            {
                if (table.Document.Records[recordIndex].Kind != CsvRecordKind.Data) continue;
                string value = table.GetCell(recordIndex, identityColumn) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                {
                    Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_IDENTITY_EMPTY",
                        "The configured identity value is empty.", table.Name,
                        recordIndex, identityColumn);
                    continue;
                }
                int firstRecord;
                if (firstRecords.TryGetValue(value, out firstRecord))
                {
                    Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "DATA_IDENTITY_DUPLICATE",
                        "Identity value '" + value + "' duplicates physical record " + firstRecord + ".",
                        table.Name, recordIndex, identityColumn);
                }
                else
                {
                    firstRecords.Add(value, recordIndex);
                }
            }
        }

        private static void ValidateReferences(CsvTableController source, int sourceRecordIndex,
            CsvResolvedColumn sourceColumn, IList<CsvTableController> allTables,
            List<CsvDatasetDiagnostic> diagnostics)
        {
            string raw = source.GetCell(sourceRecordIndex, sourceColumn.PhysicalIndex) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw)) return;

            List<string> tokens;
            string extractionError;
            if (!TryExtractTokens(raw, sourceColumn.Schema.TokenSyntax, out tokens, out extractionError))
            {
                Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "REFERENCE_TOKEN_EXTRACTION_INVALID",
                    extractionError, source.Name, sourceRecordIndex, sourceColumn.PhysicalIndex);
                return;
            }

            for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                string token = tokens[tokenIndex];
                if (string.IsNullOrEmpty(token)) continue;
                List<CsvTableController> targetMatches = new List<CsvTableController>();
                bool anyRequired = false;
                bool anyPresent = false;
                int matchCount = 0;
                for (int specIndex = 0; specIndex < sourceColumn.Schema.References.Count; specIndex++)
                {
                    CsvReferenceSpec spec = sourceColumn.Schema.References[specIndex];
                    if (spec == null || !spec.IsConfigured) continue;
                    anyRequired |= spec.Required;
                    CsvTableController target;
                    bool ambiguousTable;
                    FindTarget(allTables, spec.TargetTable, source.CaseSensitiveNames, out target, out ambiguousTable);
                    if (ambiguousTable)
                    {
                        Add(diagnostics, CsvDatasetDiagnosticSeverity.Error,
                            "REFERENCE_TARGET_AMBIGUOUS",
                            "Reference target table name '" + spec.TargetTable + "' is ambiguous.",
                            source.Name, sourceRecordIndex, sourceColumn.PhysicalIndex);
                        continue;
                    }
                    if (target == null || target.Document == null) continue;
                    anyPresent = true;

                    int keyColumn;
                    string keyError;
                    if (!TryResolveTargetKeyColumn(target, spec.TargetKeyColumn,
                        out keyColumn, out keyError))
                    {
                        Add(diagnostics, CsvDatasetDiagnosticSeverity.Error,
                            "REFERENCE_KEY_SELECTOR_INVALID", keyError, source.Name,
                            sourceRecordIndex, sourceColumn.PhysicalIndex);
                        continue;
                    }

                    for (int targetRecordIndex = 0; targetRecordIndex < target.Document.Records.Count; targetRecordIndex++)
                    {
                        if (target.Document.Records[targetRecordIndex].Kind != CsvRecordKind.Data) continue;
                        if (string.Equals(target.GetCell(targetRecordIndex, keyColumn), token,
                            source.CaseSensitiveNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        {
                            matchCount++;
                            targetMatches.Add(target);
                        }
                    }
                }

                if (matchCount == 1) continue;
                if (matchCount > 1)
                {
                    Add(diagnostics, CsvDatasetDiagnosticSeverity.Error, "REFERENCE_AMBIGUOUS",
                        "Reference token '" + token + "' resolves to more than one target record.",
                        source.Name, sourceRecordIndex, sourceColumn.PhysicalIndex);
                    continue;
                }

                CsvDatasetDiagnosticSeverity severity = anyRequired
                    ? CsvDatasetDiagnosticSeverity.Error : CsvDatasetDiagnosticSeverity.Warning;
                string code = anyPresent ? "REFERENCE_MISSING" : "REFERENCE_TARGET_MISSING";
                string message = anyPresent
                    ? "Reference token '" + token + "' was not found in the configured target table."
                    : "The configured reference target is not open; token '" + token + "' cannot be verified.";
                Add(diagnostics, severity, code, message, source.Name,
                    sourceRecordIndex, sourceColumn.PhysicalIndex);
            }
        }

        private static void FindTarget(IList<CsvTableController> tables, string name,
            bool caseSensitiveNames, out CsvTableController target, out bool ambiguous)
        {
            target = null;
            ambiguous = false;
            if (tables == null) return;
            for (int i = 0; i < tables.Count; i++)
            {
                CsvTableController candidate = tables[i];
                if (candidate == null || !string.Equals(candidate.Name, name,
                    caseSensitiveNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) continue;
                if (target != null)
                {
                    ambiguous = true;
                    return;
                }
                target = candidate;
            }
        }

        private static bool TryResolveTargetKeyColumn(CsvTableController target, string selector,
            out int physicalColumn, out string error)
        {
            physicalColumn = -1;
            error = string.Empty;
            if (target != null && target.TryResolvePhysicalColumn(selector, out physicalColumn)) return true;
            error = "Target key selector '" + selector + "' is missing or ambiguous on table '" +
                (target == null ? string.Empty : target.Name) + "'.";
            return false;
        }

        private static bool TryExtractTokens(string raw, CsvTokenSyntax syntax,
            out List<string> tokens, out string error)
        {
            tokens = new List<string>();
            error = string.Empty;
            syntax = syntax ?? new CsvTokenSyntax();
            try
            {
                if (syntax.Mode == CsvTokenExtractionMode.FirstWhitespaceToken)
                {
                    string[] parts = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) AddToken(tokens, parts[0], syntax);
                    return true;
                }
                if (syntax.Mode == CsvTokenExtractionMode.Delimited)
                {
                    if (string.IsNullOrEmpty(syntax.Separator))
                    {
                        error = "Delimited token extraction requires a separator.";
                        return false;
                    }
                    string[] parts = raw.Split(new[] { syntax.Separator }, StringSplitOptions.None);
                    for (int i = 0; i < parts.Length; i++) AddToken(tokens, parts[i], syntax);
                    return true;
                }
                if (syntax.Mode == CsvTokenExtractionMode.RegexCapture)
                {
                    if (string.IsNullOrEmpty(syntax.RegexPattern) || syntax.RegexCaptureGroup < 0)
                    {
                        error = "Regex token extraction requires a pattern and a non-negative capture group.";
                        return false;
                    }
                    Regex regex = new Regex(syntax.RegexPattern, RegexOptions.CultureInvariant,
                        CsvTokenSyntax.RegexMatchTimeout);
                    MatchCollection matches = regex.Matches(raw);
                    for (int i = 0; i < matches.Count; i++)
                    {
                        Match match = matches[i];
                        if (syntax.RegexCaptureGroup >= match.Groups.Count)
                        {
                            error = "Regex token extraction requested an unavailable capture group.";
                            return false;
                        }
                        AddToken(tokens, match.Groups[syntax.RegexCaptureGroup].Value, syntax);
                    }
                    return true;
                }

                AddToken(tokens, raw, syntax);
                return true;
            }
            catch (ArgumentException exception)
            {
                error = "Regex token extraction is invalid: " + exception.Message;
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                error = "Regex token extraction exceeded the validation time limit.";
                return false;
            }
        }

        private static void AddToken(List<string> tokens, string value, CsvTokenSyntax syntax)
        {
            value = value ?? string.Empty;
            if (syntax.TrimWhitespace) value = value.Trim();
            if (!syntax.IgnoreEmptyTokens || value.Length > 0) tokens.Add(value);
        }

        private static void Add(List<CsvDatasetDiagnostic> diagnostics,
            CsvDatasetDiagnosticSeverity severity, string code, string message,
            string table, int physicalRecord, int physicalColumn)
        {
            diagnostics.Add(new CsvDatasetDiagnostic(severity, code, message,
                table, physicalRecord, physicalColumn));
        }
    }
}
