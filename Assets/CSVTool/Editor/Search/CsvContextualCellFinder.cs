using System;
using System.Collections.Generic;
using CsvTool.Core;

namespace CsvTool.Editor.Search
{
    /// <summary>The kind of text that caused a contextual finder result.</summary>
    public enum CsvContextualMatchCategory
    {
        SelectedRowHeader = 0,
        SelectedRowValue = 1,
        OtherRowHeader = 2,
        OtherRowValue = 3
    }

    /// <summary>
    /// One physical cell returned by <see cref="CsvContextualCellFinder"/>.
    /// RecordIndex and ColumnIndex always address the document directly; they
    /// are not visual row/column indices and remain safe to use while a grid
    /// filter is active.
    /// </summary>
    public sealed class CsvContextualCellResult
    {
        internal CsvContextualCellResult(int recordIndex, int columnIndex,
            string headerPreview, string valuePreview,
            CsvContextualMatchCategory category, int rank)
        {
            RecordIndex = recordIndex;
            ColumnIndex = columnIndex;
            HeaderPreview = headerPreview ?? string.Empty;
            ValuePreview = valuePreview ?? string.Empty;
            Category = category;
            Rank = rank;
        }

        /// <summary>Physical index into <see cref="CsvDocument.Records"/>.</summary>
        public int RecordIndex { get; private set; }

        /// <summary>Physical index into the CSV row's columns.</summary>
        public int ColumnIndex { get; private set; }

        /// <summary>Display/raw header text suitable for a result row.</summary>
        public string HeaderPreview { get; private set; }

        /// <summary>Current cell text suitable for a result row.</summary>
        public string ValuePreview { get; private set; }

        public CsvContextualMatchCategory Category { get; private set; }

        /// <summary>Higher ranks are ordered before lower ranks.</summary>
        public int Rank { get; private set; }

        // Descriptive aliases make the integration call site explicit about
        // physical addressing without duplicating any mutable state.
        public int PhysicalRecordIndex { get { return RecordIndex; } }
        public int PhysicalColumnIndex { get { return ColumnIndex; } }
        public string Header { get { return HeaderPreview; } }
        public string Value { get { return ValuePreview; } }
        public bool IsSelectedRow
        {
            get
            {
                return Category == CsvContextualMatchCategory.SelectedRowHeader ||
                    Category == CsvContextualMatchCategory.SelectedRowValue;
            }
        }
    }

    /// <summary>
    /// Searches one opened table for the property or value the user is likely
    /// looking for. Header matches are intentionally returned as cell targets,
    /// so callers can jump directly to the physical column without resolving a
    /// possibly duplicate header name a second time.
    /// </summary>
    public static class CsvContextualCellFinder
    {
        private const int CategoryWeight = 1000000;
        private const int ExactWeight = 100000;
        private const int PrefixWeight = 10000;

        /// <summary>
        /// Returns ranked matches from the current table. The selected row's
        /// header matches come first, followed by its value matches, then the
        /// corresponding matches from other physical rows.
        /// </summary>
        public static IReadOnlyList<CsvContextualCellResult> Search(
            CsvTableController table, int selectedPhysicalRecordIndex, string query)
        {
            if (table == null) throw new ArgumentNullException("table");
            return Search(table, table.Document, selectedPhysicalRecordIndex, query);
        }

        /// <summary>
        /// Explicit-document overload for integrations that already hold the
        /// controller's document. Header labels still come from the controller
        /// so configured display names are searchable and shown consistently.
        /// </summary>
        public static IReadOnlyList<CsvContextualCellResult> Search(
            CsvTableController table, CsvDocument document,
            int selectedPhysicalRecordIndex, string query)
        {
            if (table == null) throw new ArgumentNullException("table");
            List<CsvContextualCellResult> results = new List<CsvContextualCellResult>();
            if (document == null || string.IsNullOrEmpty(query)) return results.AsReadOnly();

            int headerRecordIndex = table.HeaderRecordIndex;
            int columnCount = document.ColumnCount;
            for (int recordIndex = 0; recordIndex < document.Records.Count; recordIndex++)
            {
                if (recordIndex == headerRecordIndex) continue;
                CsvRecord record = document.Records[recordIndex];
                bool selected = recordIndex == selectedPhysicalRecordIndex;
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    string header = table.GetHeader(columnIndex);
                    string rawHeader = columnIndex < table.Headers.Count ? table.Headers[columnIndex] : string.Empty;
                    string value = record.GetValue(columnIndex);
                    // A header describes the same physical column on every row. Returning that
                    // match once on the selected row keeps wide-table navigation useful without
                    // flooding the result list with one duplicate per record.
                    int headerScore = selected ? BestMatchScore(rawHeader, header, query) : -1;
                    int valueScore = MatchScore(value, query);
                    if (headerScore < 0 && valueScore < 0) continue;

                    // Keep one target per physical cell. If both its header and
                    // value match, the header category wins because it is the
                    // more direct answer to "where is this property?".
                    bool headerMatch = headerScore >= 0;
                    int textScore = headerMatch ? headerScore : valueScore;
                    CsvContextualMatchCategory category;
                    if (selected)
                        category = headerMatch ? CsvContextualMatchCategory.SelectedRowHeader : CsvContextualMatchCategory.SelectedRowValue;
                    else
                        category = headerMatch ? CsvContextualMatchCategory.OtherRowHeader : CsvContextualMatchCategory.OtherRowValue;

                    int rank = CategoryWeight * (4 - (int)category) + textScore;
                    results.Add(new CsvContextualCellResult(recordIndex, columnIndex,
                        header, value, category, rank));
                }
            }

            results.Sort(CompareResults);
            return results.AsReadOnly();
        }

        /// <summary>Short alias for callers that use finder terminology.</summary>
        public static IReadOnlyList<CsvContextualCellResult> Find(
            CsvTableController table, int selectedPhysicalRecordIndex, string query)
        {
            return Search(table, selectedPhysicalRecordIndex, query);
        }

        private static int CompareResults(CsvContextualCellResult left, CsvContextualCellResult right)
        {
            int rank = right.Rank.CompareTo(left.Rank);
            if (rank != 0) return rank;
            int record = left.RecordIndex.CompareTo(right.RecordIndex);
            return record != 0 ? record : left.ColumnIndex.CompareTo(right.ColumnIndex);
        }

        private static int BestMatchScore(string rawHeader, string displayHeader, string query)
        {
            int rawScore = MatchScore(rawHeader, query);
            int displayScore = string.Equals(rawHeader, displayHeader, StringComparison.Ordinal)
                ? -1 : MatchScore(displayHeader, query);
            return Math.Max(rawScore, displayScore);
        }

        private static int MatchScore(string text, string query)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return -1;
            int index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;

            int score = 1000 - Math.Min(index, 999);
            if (string.Equals(text, query, StringComparison.OrdinalIgnoreCase)) score += ExactWeight;
            else if (index == 0) score += PrefixWeight;
            // Prefer shorter, more focused labels when two matches begin at
            // the same location, while keeping the score deterministic.
            score += Math.Max(0, 100 - Math.Min(text.Length, 100));
            return score;
        }
    }
}
