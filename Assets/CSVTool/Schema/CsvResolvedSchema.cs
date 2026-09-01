using System;
using System.Collections.Generic;

namespace CsvTool.Schema
{
    /// <summary>The outcome of resolving a configured selector against a header row.</summary>
    public enum CsvSchemaResolutionStatus
    {
        Unspecified = 0,
        Resolved = 1,
        Missing = 2,
        Ambiguous = 3,
        InvalidIndex = 4
    }

    /// <summary>One selector resolved to a physical CSV column (or a diagnostic).</summary>
    public sealed class CsvResolvedColumn
    {
        public CsvResolvedColumn(CsvColumnSchema schema, CsvSchemaResolutionStatus status,
            int physicalIndex, string headerName, string label)
        {
            Schema = schema;
            Status = status;
            PhysicalIndex = physicalIndex;
            HeaderName = headerName ?? string.Empty;
            Label = label ?? string.Empty;
        }

        public CsvColumnSchema Schema { get; private set; }
        public CsvSchemaResolutionStatus Status { get; private set; }
        public int PhysicalIndex { get; private set; }
        public string HeaderName { get; private set; }
        public string Label { get; private set; }
        public bool IsResolved { get { return Status == CsvSchemaResolutionStatus.Resolved; } }

        // Convenient aliases for consumers that use "index" terminology.
        public int Index { get { return PhysicalIndex; } }
        public string DisplayName { get { return Label; } }
    }

    /// <summary>Resolved identity/display selectors and column overrides for one table.</summary>
    public sealed class CsvResolvedTableSchema
    {
        private readonly List<string> headers;
        private readonly List<string> labels;
        private readonly List<CsvResolvedColumn> columns;
        private readonly List<CsvSchemaIssue> diagnostics;

        internal CsvResolvedTableSchema(CsvTableSchema table, IList<string> headers,
            IList<string> labels, IList<CsvResolvedColumn> columns, IList<CsvSchemaIssue> diagnostics,
            CsvResolvedColumn identity, CsvResolvedColumn display)
        {
            Table = table;
            this.headers = new List<string>(headers);
            this.labels = new List<string>(labels);
            this.columns = new List<CsvResolvedColumn>(columns);
            this.diagnostics = new List<CsvSchemaIssue>(diagnostics);
            Identity = identity;
            Display = display;
        }

        public CsvTableSchema Table { get; private set; }
        public IReadOnlyList<string> Headers { get { return headers; } }
        public IReadOnlyList<string> Labels { get { return labels; } }
        public IReadOnlyList<CsvResolvedColumn> Columns { get { return columns; } }
        public IReadOnlyList<CsvSchemaIssue> Diagnostics { get { return diagnostics; } }
        public CsvResolvedColumn Identity { get; private set; }
        public CsvResolvedColumn Display { get; private set; }
        public bool IsValid
        {
            get
            {
                for (int i = 0; i < diagnostics.Count; i++)
                    if (diagnostics[i].Severity == CsvSchemaIssueSeverity.Error) return false;
                return true;
            }
        }
    }

    /// <summary>Resolves schema selectors without IO or Unity dependencies.</summary>
    public static class CsvSchemaResolver
    {
        public static CsvResolvedTableSchema Resolve(CsvTableSchema table, IList<string> headers)
        {
            return Resolve(table, headers, false);
        }

        public static CsvResolvedTableSchema Resolve(CsvTableSchema table, IList<string> headers, bool caseSensitive)
        {
            if (headers == null) headers = new string[0];
            if (table == null)
            {
                return new CsvResolvedTableSchema(null, headers, new List<string>(BuildUniqueLabels(headers)),
                    new List<CsvResolvedColumn>(), new List<CsvSchemaIssue> {
                        Error("TABLE_NULL", "Cannot resolve a null table schema.", string.Empty, string.Empty)
                    }, null, null);
            }

            List<string> labels = new List<string>(BuildUniqueLabels(headers));
            List<CsvSchemaIssue> issues = new List<CsvSchemaIssue>();
            List<CsvResolvedColumn> columns = new List<CsvResolvedColumn>();
            for (int i = 0; i < table.Columns.Count; i++)
            {
                CsvColumnSchema column = table.Columns[i];
                CsvResolvedColumn resolved = ResolveColumn(table.Name, column, headers, labels, caseSensitive, issues);
                columns.Add(resolved);
            }

            CsvResolvedColumn identity = ResolveSelector(table.Name, "Identity", table.IdentityColumn,
                table.IdentityColumnIndex, headers, labels, caseSensitive, issues);
            CsvResolvedColumn display = ResolveSelector(table.Name, "Display", table.DisplayColumn,
                table.DisplayColumnIndex, headers, labels, caseSensitive, issues);
            return new CsvResolvedTableSchema(table, headers, labels, columns, issues, identity, display);
        }

        public static IReadOnlyList<string> BuildUniqueLabels(IList<string> headers)
        {
            List<string> result = new List<string>();
            if (headers == null) return result;
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Count; i++)
            {
                string header = headers[i] ?? string.Empty;
                string key = header.Trim();
                int count;
                counts.TryGetValue(key, out count);
                counts[key] = count + 1;
                // Add the physical position to every repeated/blank label, making it stable and unique.
                result.Add(string.IsNullOrWhiteSpace(key) ? "Column " + (i + 1) : key);
            }
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Count; i++)
            {
                string key = result[i];
                int total = counts.ContainsKey((headers[i] ?? string.Empty).Trim()) ? counts[(headers[i] ?? string.Empty).Trim()] : 1;
                int n; seen.TryGetValue(key, out n); seen[key] = n + 1;
                if (total > 1) result[i] = key + " [column " + i + "]";
            }
            return result;
        }

        private static CsvResolvedColumn ResolveColumn(string table, CsvColumnSchema column, IList<string> headers,
            IList<string> labels, bool sensitive, List<CsvSchemaIssue> issues)
        {
            if (column == null)
            {
                issues.Add(Error("COLUMN_NULL", "Cannot resolve a null column override.", table, string.Empty));
                return new CsvResolvedColumn(null, CsvSchemaResolutionStatus.Unspecified, -1, string.Empty, "Column");
            }
            return ResolveSelector(table, column.Name, column.Name, column.Index, headers, labels, sensitive, issues, column);
        }

        private static CsvResolvedColumn ResolveSelector(string table, string role, string name, int index,
            IList<string> headers, IList<string> labels, bool sensitive, List<CsvSchemaIssue> issues, CsvColumnSchema schema = null)
        {
            string requested = name ?? string.Empty;
            if (index < -1)
            {
                issues.Add(Error("SCHEMA_INDEX_INVALID", role + " index must be -1 or a zero-based index.", table, requested));
                return new CsvResolvedColumn(schema, CsvSchemaResolutionStatus.InvalidIndex, -1, string.Empty, requested);
            }
            if (index >= 0)
            {
                if (index >= headers.Count)
                {
                    issues.Add(Error("SCHEMA_INDEX_OUT_OF_RANGE", role + " index " + index + " is outside the header row.", table, requested));
                    return new CsvResolvedColumn(schema, CsvSchemaResolutionStatus.InvalidIndex, -1, string.Empty, requested);
                }
                string actual = headers[index] ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(requested) && !EqualsName(requested, actual, sensitive))
                    issues.Add(Warning("SCHEMA_INDEX_NAME_MISMATCH", role + " index resolves to '" + actual + "', not the configured name '" + requested + "'.", table, requested));
                return new CsvResolvedColumn(schema, CsvSchemaResolutionStatus.Resolved, index, actual, labels[index]);
            }
            if (string.IsNullOrWhiteSpace(requested))
                return new CsvResolvedColumn(schema, CsvSchemaResolutionStatus.Unspecified, -1, string.Empty, string.Empty);
            int found = -1;
            for (int i = 0; i < headers.Count; i++)
            {
                if (!EqualsName(requested, headers[i] ?? string.Empty, sensitive)) continue;
                if (found >= 0)
                {
                    issues.Add(Error("SCHEMA_HEADER_AMBIGUOUS", "Configured name matches more than one header; specify its physical index.", table, requested));
                    return new CsvResolvedColumn(schema, CsvSchemaResolutionStatus.Ambiguous, -1, string.Empty, requested);
                }
                found = i;
            }
            if (found < 0)
            {
                issues.Add(Error("SCHEMA_HEADER_MISSING", "Configured header was not found.", table, requested));
                return new CsvResolvedColumn(schema, CsvSchemaResolutionStatus.Missing, -1, string.Empty, requested);
            }
            return new CsvResolvedColumn(schema, CsvSchemaResolutionStatus.Resolved, found, headers[found] ?? string.Empty, labels[found]);
        }

        private static bool EqualsName(string a, string b, bool sensitive)
        {
            return string.Equals(a, b, sensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        private static CsvSchemaIssue Error(string code, string message, string table, string column) { return new CsvSchemaIssue(CsvSchemaIssueSeverity.Error, code, message, table, column); }
        private static CsvSchemaIssue Warning(string code, string message, string table, string column) { return new CsvSchemaIssue(CsvSchemaIssueSeverity.Warning, code, message, table, column); }
    }
}
