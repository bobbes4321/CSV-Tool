using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CsvTool.Core;
using CsvTool.Schema;
using CsvTool.Editor;

namespace CsvTool.Editor.Index
{
    /// A distinct value and the physical rows containing it.
    public sealed class CsvIndexedValue
    {
        internal CsvIndexedValue(string value, List<int> records)
        {
            Value = value;
            RecordIndices = records.AsReadOnly();
        }
        public string Value { get; private set; }
        public IReadOnlyList<int> RecordIndices { get; private set; }
    }

    /// Lazy, per-column distinct-value index for an opened table. Call an
    /// invalidation method after edits (the index deliberately does not poll).
    public sealed class CsvValueIndex
    {
        public const int DefaultPrefixResultCap = 50;
        private readonly CsvTableController table;
        private readonly Dictionary<int, IReadOnlyList<CsvIndexedValue>> cache =
            new Dictionary<int, IReadOnlyList<CsvIndexedValue>>();
        private readonly bool includeNonDataRows;

        public CsvValueIndex(CsvTableController table, bool includeNonDataRows = false)
        {
            this.table = table ?? throw new ArgumentNullException("table");
            this.includeNonDataRows = includeNonDataRows;
        }

        public CsvTableController Table { get { return table; } }

        public IReadOnlyList<CsvIndexedValue> GetDistinctValues(int columnIndex)
        {
            if (columnIndex < 0) throw new ArgumentOutOfRangeException("columnIndex");
            IReadOnlyList<CsvIndexedValue> values;
            if (!cache.TryGetValue(columnIndex, out values))
            {
                Dictionary<string, List<int>> found = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                if (table.Document != null)
                {
                    for (int i = 0; i < table.Document.Records.Count; i++)
                    {
                        CsvRecord record = table.Document.Records[i];
                        if (record.Kind == CsvRecordKind.Header || (!includeNonDataRows && record.Kind != CsvRecordKind.Data)) continue;
                        string value = record.GetValue(columnIndex);
                        List<int> rows;
                        if (!found.TryGetValue(value, out rows)) { rows = new List<int>(); found.Add(value, rows); }
                        rows.Add(i);
                    }
                }
                List<CsvIndexedValue> result = new List<CsvIndexedValue>(found.Count);
                foreach (KeyValuePair<string, List<int>> pair in found) result.Add(new CsvIndexedValue(pair.Key, pair.Value));
                result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Value, b.Value));
                values = result.AsReadOnly();
                cache[columnIndex] = values;
            }
            return values;
        }

        public IReadOnlyList<CsvIndexedValue> LookupPrefix(int columnIndex, string prefix, int maximumResults = DefaultPrefixResultCap)
        {
            if (maximumResults < 0) throw new ArgumentOutOfRangeException("maximumResults");
            prefix = prefix ?? string.Empty;
            List<CsvIndexedValue> result = new List<CsvIndexedValue>();
            IReadOnlyList<CsvIndexedValue> all = GetDistinctValues(columnIndex);
            for (int i = 0; i < all.Count && result.Count < maximumResults; i++)
                if (all[i].Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) result.Add(all[i]);
            return result.AsReadOnly();
        }

        public void InvalidateColumn(int columnIndex) { cache.Remove(columnIndex); }
        public void InvalidateAll() { cache.Clear(); }
        public void Invalidate(int columnIndex) { InvalidateColumn(columnIndex); }
        public void Clear() { InvalidateAll(); }
    }

    public enum CsvReferenceResolutionStatus { Invalid, Missing, Ambiguous, Resolved }

    /// Result of resolving one source token. Target indices are physical CSV coordinates.
    public sealed class CsvReferenceResolution
    {
        public CsvReferenceResolutionStatus Status { get; internal set; }
        public string Token { get; internal set; }
        public string TargetTableName { get; internal set; }
        public string TargetKey { get; internal set; }
        public string TargetDisplay { get; internal set; }
        public int TargetRecordIndex { get; internal set; } = -1;
        public int TargetKeyColumnIndex { get; internal set; } = -1;
        public int TargetDisplayColumnIndex { get; internal set; } = -1;
        public string Message { get; internal set; }
    }

    /// Resolves schema-declared references without guessing relationships.
    public static class CsvReferenceResolver
    {
        public static IReadOnlyList<CsvReferenceResolution> ResolveAll(
            CsvTableController source, int sourceRecordIndex, int sourceColumnIndex,
            IEnumerable<CsvTableController> tables)
        {
            if (source == null) throw new ArgumentNullException("source");
            List<CsvReferenceResolution> output = new List<CsvReferenceResolution>();
            CsvColumnSchema column = FindColumn(source, sourceColumnIndex);
            if (column == null || column.References.Count == 0) return output.AsReadOnly();
            string raw = source.GetCell(sourceRecordIndex, sourceColumnIndex);
            IReadOnlyList<string> tokens = ExtractTokens(raw, column.TokenSyntax);
            foreach (string token in tokens)
                output.Add(ResolveToken(token, column.References, tables));
            return output.AsReadOnly();
        }

        public static IReadOnlyList<CsvReferenceResolution> ResolveAll(
            CsvTableController source, int sourceRecordIndex, int sourceColumnIndex,
            CsvWorkspaceController workspace)
        {
            return ResolveAll(source, sourceRecordIndex, sourceColumnIndex,
                workspace == null ? null : workspace.Tables);
        }

        public static CsvReferenceResolution Resolve(CsvTableController source, int sourceRecordIndex,
            int sourceColumnIndex, IEnumerable<CsvTableController> tables)
        {
            IReadOnlyList<CsvReferenceResolution> all = ResolveAll(source, sourceRecordIndex, sourceColumnIndex, tables);
            return all.Count == 0 ? new CsvReferenceResolution { Status = CsvReferenceResolutionStatus.Invalid, Message = "No reference metadata is configured." } : all[0];
        }

        public static CsvReferenceResolution Resolve(CsvTableController source, int sourceRecordIndex,
            int sourceColumnIndex, CsvWorkspaceController workspace)
        {
            return Resolve(source, sourceRecordIndex, sourceColumnIndex,
                workspace == null ? null : workspace.Tables);
        }

        private static CsvReferenceResolution ResolveToken(string token, IList<CsvReferenceSpec> specs, IEnumerable<CsvTableController> tables)
        {
            List<CsvReferenceResolution> matches = new List<CsvReferenceResolution>();
            foreach (CsvReferenceSpec spec in specs)
            {
                if (spec == null || !spec.IsConfigured) continue;
                CsvTableController target = FindTable(tables, spec.TargetTable);
                if (target == null || target.Document == null) continue;
                int keyColumn = FindTableColumn(target, spec.TargetKeyColumn, false);
                if (keyColumn < 0) continue;
                for (int i = 0; i < target.Document.Records.Count; i++)
                {
                    CsvRecord record = target.Document.Records[i];
                    if (record.Kind != CsvRecordKind.Data) continue;
                    if (string.Equals(record.GetValue(keyColumn), token, StringComparison.OrdinalIgnoreCase))
                    {
                        int display = FindTableColumn(target, spec.TargetDisplayColumn, true);
                        matches.Add(new CsvReferenceResolution { Status = CsvReferenceResolutionStatus.Resolved, Token = token, TargetTableName = target.Name, TargetKey = record.GetValue(keyColumn), TargetDisplay = record.GetValue(display), TargetRecordIndex = i, TargetKeyColumnIndex = keyColumn, TargetDisplayColumnIndex = display });
                    }
                }
            }
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1) return new CsvReferenceResolution { Status = CsvReferenceResolutionStatus.Ambiguous, Token = token, TargetKey = token, Message = "The reference matches more than one target record." };
            return new CsvReferenceResolution { Status = CsvReferenceResolutionStatus.Missing, Token = token, TargetKey = token, Message = "The reference target was not found." };
        }

        private static IReadOnlyList<string> ExtractTokens(string raw, CsvTokenSyntax syntax)
        {
            syntax = syntax ?? new CsvTokenSyntax(); raw = raw ?? string.Empty;
            List<string> result = new List<string>();
            if (syntax.Mode == CsvTokenExtractionMode.FirstWhitespaceToken)
            {
                string[] parts = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) Add(result, parts[0], syntax);
            }
            else if (syntax.Mode == CsvTokenExtractionMode.Delimited) foreach (string item in raw.Split(new[] { syntax.Separator }, StringSplitOptions.None)) Add(result, item, syntax);
            else if (syntax.Mode == CsvTokenExtractionMode.RegexCapture && !string.IsNullOrEmpty(syntax.RegexPattern)) foreach (Match m in Regex.Matches(raw, syntax.RegexPattern)) if (syntax.RegexCaptureGroup < m.Groups.Count) Add(result, m.Groups[syntax.RegexCaptureGroup].Value, syntax);
            else Add(result, raw, syntax);
            return result.AsReadOnly();
        }
        private static void Add(List<string> result, string value, CsvTokenSyntax syntax) { if (syntax.TrimWhitespace) value = value.Trim(); if (!syntax.IgnoreEmptyTokens || value.Length > 0) result.Add(value); }
        private static CsvColumnSchema FindColumn(CsvTableController t, int index) { if (t.Schema != null) foreach (CsvColumnSchema c in t.Schema.Columns) if (c != null && ((c.Index >= 0 && c.Index == index) || (c.Index < 0 && string.Equals(c.Name, t.GetHeader(index), StringComparison.OrdinalIgnoreCase)))) return c; return null; }
        private static int FindTableColumn(CsvTableController t, string selector, bool optional) { if (string.IsNullOrWhiteSpace(selector)) return optional ? -1 : -1; if (t.Schema != null) foreach (CsvColumnSchema c in t.Schema.Columns) if (c != null && c.Index >= 0 && string.Equals(c.Name, selector, StringComparison.OrdinalIgnoreCase)) return c.Index; for (int i = 0; i < t.Headers.Count; i++) if (string.Equals(t.GetHeader(i), selector, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        private static CsvTableController FindTable(IEnumerable<CsvTableController> tables, string name) { if (tables != null) foreach (CsvTableController t in tables) if (t != null && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) return t; return null; }
    }
}
