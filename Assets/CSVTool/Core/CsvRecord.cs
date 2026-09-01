using System;
using System.Collections.Generic;
using System.Text;

namespace CsvTool.Core
{
    public sealed class CsvRecord
    {
        private readonly List<string> _values;
        private List<string> _originalValues;
        private string _originalRawText;
        private bool _isDirty;

        internal CsvRecord(int index, CsvRecordKind kind, string rawText, string lineEnding, List<string> values, bool malformedQuotes)
        {
            Index = index;
            Kind = kind;
            _originalRawText = rawText ?? string.Empty;
            OriginalLineEnding = lineEnding ?? string.Empty;
            _values = values ?? new List<string>();
            _originalValues = new List<string>(_values);
            HasMalformedQuotes = malformedQuotes;
        }

        public int Index { get; internal set; }
        public CsvRecordKind Kind { get; internal set; }
        public string OriginalLineEnding { get; private set; }
        public bool HasMalformedQuotes { get; private set; }
        public bool IsDirty { get { return _isDirty; } }
        public int CellCount { get { return _values.Count; } }
        public int OriginalCellCount { get { return _originalValues.Count; } }
        public IReadOnlyList<string> Values { get { return _values; } }

        public string GetValue(int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= _values.Count) return string.Empty;
            return _values[columnIndex];
        }

        public string GetOriginalValue(int columnIndex)
        {
            if (columnIndex < 0 || columnIndex >= _originalValues.Count) return string.Empty;
            return _originalValues[columnIndex];
        }

        internal bool IsCellDirty(int columnIndex)
        {
            if (columnIndex < 0) return false;
            if (columnIndex >= _values.Count) return false;
            if (columnIndex >= _originalValues.Count) return !string.IsNullOrEmpty(_values[columnIndex]);
            return !string.Equals(_values[columnIndex], _originalValues[columnIndex], StringComparison.Ordinal);
        }

        internal string SetValue(int columnIndex, string value)
        {
            if (columnIndex < 0) throw new ArgumentOutOfRangeException("columnIndex");
            EnsureColumnCount(columnIndex + 1);
            string old = _values[columnIndex];
            _values[columnIndex] = value ?? string.Empty;
            _isDirty = !ValuesEqual(_values, _originalValues);
            return old;
        }

        internal void EnsureColumnCount(int count)
        {
            while (_values.Count < count) _values.Add(string.Empty);
        }

        internal void RestoreValue(int columnIndex, string value, int targetCellCount)
        {
            if (targetCellCount < 0)
            {
                SetValue(columnIndex, value);
                return;
            }

            EnsureColumnCount(Math.Max(columnIndex + 1, targetCellCount));
            _values[columnIndex] = value ?? string.Empty;
            while (_values.Count > targetCellCount) _values.RemoveAt(_values.Count - 1);
            _isDirty = !ValuesEqual(_values, _originalValues);
        }

        /// <summary>Restores one value while keeping unrelated edits in an extended row.</summary>
        internal void RestoreCell(int columnIndex, string value)
        {
            if (columnIndex < 0) throw new ArgumentOutOfRangeException("columnIndex");
            EnsureColumnCount(columnIndex + 1);
            _values[columnIndex] = value ?? string.Empty;

            // Newly-addressed columns are structural. Trim only trailing empty columns;
            // retain any extension that is still needed by another edit in this row.
            int minimumCount = _originalValues.Count;
            while (_values.Count > minimumCount && string.IsNullOrEmpty(_values[_values.Count - 1]))
                _values.RemoveAt(_values.Count - 1);
            _isDirty = !ValuesEqual(_values, _originalValues);
        }

        internal string SerializeContent()
        {
            if (_values.Count == 0) return string.Empty;
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < _values.Count; i++)
            {
                if (i > 0) builder.Append(',');
                AppendEscaped(builder, _values[i]);
            }
            return builder.ToString();
        }

        internal void MarkClean(string serializedContent)
        {
            _originalRawText = serializedContent ?? string.Empty;
            _originalValues = new List<string>(_values);
            _isDirty = false;
        }

        private static bool ValuesEqual(List<string> first, List<string> second)
        {
            if (first.Count != second.Count) return false;
            for (int i = 0; i < first.Count; i++)
                if (!string.Equals(first[i], second[i], StringComparison.Ordinal)) return false;
            return true;
        }

        internal string GetRawContent()
        {
            return _isDirty ? SerializeContent() : _originalRawText;
        }

        internal static void AppendEscaped(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            bool quote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!quote)
            {
                builder.Append(value);
                return;
            }

            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '"') builder.Append("\"\"");
                else builder.Append(c);
            }
            builder.Append('"');
        }
    }
}
