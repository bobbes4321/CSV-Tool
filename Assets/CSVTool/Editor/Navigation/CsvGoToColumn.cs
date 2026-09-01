using System;
using System.Collections.Generic;
using CsvTool.Schema;

namespace CsvTool.Editor
{
    public enum CsvGoToColumnMatchKind
    {
        None = 0,
        DisplayName = 1,
        Header = 2,
        Group = 3,
        CurrentValue = 4
    }

    /// <summary>One physical CSV column offered by the Go to Column navigator.</summary>
    public sealed class CsvGoToColumnEntry
    {
        public CsvGoToColumnEntry(int physicalColumnIndex, string displayName, string rawHeader,
            string groupName, string currentValue, int score,
            CsvGoToColumnMatchKind matchKind = CsvGoToColumnMatchKind.None)
        {
            PhysicalColumnIndex = physicalColumnIndex;
            DisplayName = displayName ?? string.Empty;
            RawHeader = rawHeader ?? string.Empty;
            GroupName = string.IsNullOrEmpty(groupName) ? "Other" : groupName;
            CurrentValue = currentValue ?? string.Empty;
            ValuePreview = CreatePreview(CurrentValue, 96);
            Score = score;
            MatchKind = matchKind;
        }

        /// <summary>Zero-based physical CSV column index. This is the edit/navigation target.</summary>
        public int PhysicalColumnIndex { get; private set; }
        public int ColumnIndex { get { return PhysicalColumnIndex; } }
        public string DisplayName { get; private set; }
        public string RawHeader { get; private set; }
        public string GroupName { get; private set; }
        public string CurrentValue { get; private set; }
        public string ValuePreview { get; private set; }
        public int Score { get; private set; }
        public CsvGoToColumnMatchKind MatchKind { get; private set; }
        public string MatchLabel
        {
            get
            {
                switch (MatchKind)
                {
                    case CsvGoToColumnMatchKind.DisplayName: return "Display name";
                    case CsvGoToColumnMatchKind.Header: return "Header";
                    case CsvGoToColumnMatchKind.Group: return "Group";
                    case CsvGoToColumnMatchKind.CurrentValue: return "Current value";
                    default: return string.Empty;
                }
            }
        }

        /// <summary>Human-readable fallback for blank headers.</summary>
        public string HeaderLabel
        {
            get { return string.IsNullOrEmpty(RawHeader) ? "Column " + (PhysicalColumnIndex + 1) : RawHeader; }
        }

        private static string CreatePreview(string value, int maximumLength)
        {
            value = value ?? string.Empty;
            value = value.Replace("\r", " ").Replace("\n", " ");
            if (value.Length <= maximumLength) return value;
            return value.Substring(0, Math.Max(0, maximumLength - 1)) + "\u2026";
        }
    }

    /// <summary>
    /// Search and keyboard-selection model for the Go to Column popup. The model is independent of
    /// IMGUI, making the ranking and physical-index behavior testable without opening an editor.
    /// </summary>
    public sealed class CsvGoToColumnModel
    {
        private readonly List<CsvGoToColumnEntry> allEntries;
        private readonly List<CsvGoToColumnEntry> results = new List<CsvGoToColumnEntry>();
        private readonly int preferredPhysicalColumnIndex;
        private string query = string.Empty;
        private int selectedIndex;

        public CsvGoToColumnModel(IEnumerable<CsvGoToColumnEntry> entries,
            int preferredPhysicalColumnIndex = -1)
        {
            this.preferredPhysicalColumnIndex = preferredPhysicalColumnIndex;
            allEntries = new List<CsvGoToColumnEntry>();
            if (entries != null)
                foreach (CsvGoToColumnEntry entry in entries)
                    if (entry != null && entry.PhysicalColumnIndex >= 0) allEntries.Add(entry);
            SetQuery(string.Empty);
        }

        public IReadOnlyList<CsvGoToColumnEntry> Entries { get { return allEntries; } }
        public IReadOnlyList<CsvGoToColumnEntry> Results { get { return results; } }
        public string Query { get { return query; } }
        public int SelectedIndex { get { return selectedIndex; } }
        public bool HasResults { get { return results.Count > 0; } }

        public CsvGoToColumnEntry SelectedEntry
        {
            get { return results.Count == 0 ? null : results[Math.Max(0, Math.Min(selectedIndex, results.Count - 1))]; }
        }

        /// <summary>Filters and re-ranks columns. Matching is case-insensitive and stable by physical index.</summary>
        public void SetQuery(string value)
        {
            query = (value ?? string.Empty).Trim();
            results.Clear();
            for (int i = 0; i < allEntries.Count; i++)
            {
                CsvGoToColumnEntry entry = allEntries[i];
                CsvGoToColumnMatchKind matchKind;
                int score = MatchScore(entry, query, out matchKind);
                if (score < 0) continue;
                results.Add(new CsvGoToColumnEntry(entry.PhysicalColumnIndex, entry.DisplayName,
                    entry.RawHeader, entry.GroupName, entry.CurrentValue, score, matchKind));
            }
            results.Sort(CompareEntries);
            selectedIndex = 0;
            if (string.IsNullOrEmpty(query) && preferredPhysicalColumnIndex >= 0)
                for (int i = 0; i < results.Count; i++)
                    if (results[i].PhysicalColumnIndex == preferredPhysicalColumnIndex)
                    {
                        selectedIndex = i;
                        break;
                    }
        }

        public void MoveSelection(int delta)
        {
            if (results.Count == 0) { selectedIndex = 0; return; }
            selectedIndex = Math.Max(0, Math.Min(selectedIndex + delta, results.Count - 1));
        }

        /// <summary>Selects a result row by its result-list index.</summary>
        public void SelectIndex(int index)
        {
            selectedIndex = results.Count == 0 ? 0 : Math.Max(0, Math.Min(index, results.Count - 1));
        }

        public bool TryGetSelectedPhysicalColumn(out int physicalColumnIndex)
        {
            CsvGoToColumnEntry entry = SelectedEntry;
            if (entry == null)
            {
                physicalColumnIndex = -1;
                return false;
            }
            physicalColumnIndex = entry.PhysicalColumnIndex;
            return true;
        }

        /// <summary>Creates entries from one table and a selected physical record.</summary>
        public static CsvGoToColumnModel ForTable(CsvTableController table, int selectedPhysicalRecordIndex,
            CsvRecordViewDefinition recordView = null, int preferredPhysicalColumnIndex = -1)
        {
            if (table == null) return new CsvGoToColumnModel(null, preferredPhysicalColumnIndex);
            IReadOnlyList<string> values = null;
            if (table.Document != null && selectedPhysicalRecordIndex >= 0 &&
                selectedPhysicalRecordIndex < table.Document.Records.Count)
                values = table.Document.Records[selectedPhysicalRecordIndex].Values;
            return ForColumns(table.Headers, values, table.Schema, recordView, preferredPhysicalColumnIndex);
        }

        /// <summary>
        /// Creates entries from headers, a selected physical row, and optional explicit metadata.
        /// Header and row lists are never re-ordered; entries always return physical indices.
        /// </summary>
        public static CsvGoToColumnModel ForColumns(IReadOnlyList<string> headers,
            IReadOnlyList<string> currentRowValues, CsvTableSchema schema = null,
            CsvRecordViewDefinition recordView = null, int preferredPhysicalColumnIndex = -1)
        {
            List<CsvGoToColumnEntry> entries = new List<CsvGoToColumnEntry>();
            if (headers == null) return new CsvGoToColumnModel(entries, preferredPhysicalColumnIndex);
            for (int index = 0; index < headers.Count; index++)
            {
                string rawHeader = headers[index] ?? string.Empty;
                string displayName = string.IsNullOrEmpty(rawHeader) ? "Column " + (index + 1) : rawHeader;
                string group = "Other";
                CsvColumnSchema configured = FindSchemaColumn(schema, headers, index);
                if (configured != null)
                {
                    if (!string.IsNullOrWhiteSpace(configured.EffectiveDisplayName))
                        displayName = configured.EffectiveDisplayName;
                    group = "General";
                }

                CsvRecordFieldDefinition fieldDefinition = FindRecordField(recordView, headers, index);
                if (fieldDefinition != null)
                {
                    if (!string.IsNullOrWhiteSpace(fieldDefinition.DisplayName)) displayName = fieldDefinition.DisplayName;
                    if (!string.IsNullOrWhiteSpace(fieldDefinition.GroupName)) group = fieldDefinition.GroupName;
                }
                else if (recordView != null && !string.IsNullOrEmpty(recordView.UnconfiguredGroupName))
                {
                    group = recordView.UnconfiguredGroupName;
                }

                string value = currentRowValues != null && index < currentRowValues.Count
                    ? currentRowValues[index] ?? string.Empty : string.Empty;
                entries.Add(new CsvGoToColumnEntry(index, displayName, rawHeader, group, value, 0));
            }
            return new CsvGoToColumnModel(entries, preferredPhysicalColumnIndex);
        }

        private static CsvColumnSchema FindSchemaColumn(CsvTableSchema schema, IReadOnlyList<string> headers, int index)
        {
            if (schema == null || schema.Columns == null) return null;
            CsvColumnSchema byIndex = null;
            int nameMatches = 0;
            CsvColumnSchema byName = null;
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                CsvColumnSchema candidate = schema.Columns[i];
                if (candidate == null) continue;
                if (candidate.Index >= 0)
                {
                    if (candidate.Index == index)
                    {
                        if (byIndex != null) return null; // conflicting explicit selectors are ambiguous
                        byIndex = candidate;
                    }
                    continue;
                }
                if (!string.IsNullOrEmpty(candidate.Name) &&
                    string.Equals(candidate.Name, headers[index] ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    // A name-only selector cannot identify one physical column when the
                    // header row repeats that name. Keep the navigator honest about the
                    // same ambiguity enforced by schema resolution.
                    int headerMatches = 0;
                    for (int headerIndex = 0; headerIndex < headers.Count; headerIndex++)
                        if (string.Equals(candidate.Name, headers[headerIndex] ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase)) headerMatches++;
                    if (headerMatches != 1) continue;
                    nameMatches++;
                    byName = candidate;
                }
            }
            if (byIndex != null) return byIndex;
            return nameMatches == 1 ? byName : null;
        }

        private static CsvRecordFieldDefinition FindRecordField(CsvRecordViewDefinition definition,
            IReadOnlyList<string> headers, int index)
        {
            if (definition == null || definition.Fields == null) return null;
            CsvRecordFieldDefinition byIndex = null;
            int nameMatches = 0;
            CsvRecordFieldDefinition byName = null;
            for (int i = 0; i < definition.Fields.Count; i++)
            {
                CsvRecordFieldDefinition candidate = definition.Fields[i];
                if (candidate == null) continue;
                if (candidate.ColumnIndex >= 0)
                {
                    if (candidate.ColumnIndex == index)
                    {
                        if (byIndex != null) return null;
                        byIndex = candidate;
                    }
                    continue;
                }
                if (!string.IsNullOrEmpty(candidate.ColumnName) &&
                    string.Equals(candidate.ColumnName, headers[index] ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    nameMatches++;
                    byName = candidate;
                }
            }
            if (byIndex != null) return byIndex;
            return nameMatches == 1 ? byName : null;
        }

        private static int CompareEntries(CsvGoToColumnEntry first, CsvGoToColumnEntry second)
        {
            int score = second.Score.CompareTo(first.Score);
            return score != 0 ? score : first.PhysicalColumnIndex.CompareTo(second.PhysicalColumnIndex);
        }

        private static int MatchScore(CsvGoToColumnEntry entry, string text,
            out CsvGoToColumnMatchKind matchKind)
        {
            matchKind = CsvGoToColumnMatchKind.None;
            if (string.IsNullOrEmpty(text)) return 0;
            int best = -1;
            ConsiderMatch(ScoreText(entry.DisplayName, text, 400),
                CsvGoToColumnMatchKind.DisplayName, ref best, ref matchKind);
            ConsiderMatch(ScoreText(entry.RawHeader, text, 300),
                CsvGoToColumnMatchKind.Header, ref best, ref matchKind);
            ConsiderMatch(ScoreText(entry.GroupName, text, 200),
                CsvGoToColumnMatchKind.Group, ref best, ref matchKind);
            ConsiderMatch(ScoreText(entry.CurrentValue, text, 100),
                CsvGoToColumnMatchKind.CurrentValue, ref best, ref matchKind);
            return best;
        }

        private static void ConsiderMatch(int score, CsvGoToColumnMatchKind kind,
            ref int best, ref CsvGoToColumnMatchKind matchKind)
        {
            if (score <= best) return;
            best = score;
            matchKind = kind;
        }

        private static int ScoreText(string candidate, string query, int weight)
        {
            if (string.IsNullOrEmpty(candidate)) return -1;
            if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase)) return weight + 100;
            if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return weight + 80;
            int contiguous = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (contiguous >= 0) return weight + 60 - Math.Min(contiguous, 40);
            int queryIndex = 0;
            for (int i = 0; i < candidate.Length && queryIndex < query.Length; i++)
                if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(query[queryIndex])) queryIndex++;
            return queryIndex == query.Length ? weight + 20 - Math.Min(candidate.Length - query.Length, 20) : -1;
        }
    }
}
