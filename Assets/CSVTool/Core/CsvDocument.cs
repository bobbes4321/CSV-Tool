using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CsvTool.Core
{
    /// <summary>
    /// Loss-minimizing logical CSV document. Unchanged records retain their original text and line ending,
    /// so a save after editing a single cell does not reformat the rest of a file.
    /// </summary>
    public sealed class CsvDocument
    {
        private readonly List<CsvRecord> _records;
        private readonly List<CsvParseDiagnostic> _diagnostics;
        private readonly CsvParseOptions _options;
        private byte[] _originalBytes;
        private string _sourcePath;
        private string _newline;
        private bool _hasFinalNewline;

        private CsvDocument(List<CsvRecord> records, List<CsvParseDiagnostic> diagnostics, CsvParseOptions options,
            CsvEncodingInfo encoding, byte[] originalBytes, string sourcePath, string newline, bool hasFinalNewline)
        {
            _records = records;
            _diagnostics = diagnostics;
            _options = options;
            EncodingInfo = encoding;
            _originalBytes = (byte[])originalBytes.Clone();
            _sourcePath = sourcePath;
            _newline = newline;
            _hasFinalNewline = hasFinalNewline;
            History = new CsvEditHistory();
        }

        public CsvEncodingInfo EncodingInfo { get; private set; }
        public CsvEditHistory History { get; private set; }
        public IReadOnlyList<CsvRecord> Records { get { return _records; } }
        public IReadOnlyList<CsvParseDiagnostic> Diagnostics { get { return _diagnostics; } }
        /// <summary>All cells whose current value or physical row width differs from the loaded state.</summary>
        public IReadOnlyList<CsvCellChange> Changes { get { return GetChanges(); } }
        public IReadOnlyList<CsvCellChange> DirtyCells { get { return GetChanges(); } }
        public string SourcePath { get { return _sourcePath; } }
        public string Newline { get { return _newline; } }
        public bool HasFinalNewline { get { return _hasFinalNewline; } }
        public bool IsDirty
        {
            get
            {
                for (int i = 0; i < _records.Count; i++) if (_records[i].IsDirty) return true;
                return false;
            }
        }

        public int ColumnCount
        {
            get
            {
                int max = 0;
                for (int i = 0; i < _records.Count; i++) max = Math.Max(max, _records[i].CellCount);
                return max;
            }
        }

        public static CsvDocument Load(byte[] bytes, CsvParseOptions options = null, string sourcePath = null)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            options = options ?? new CsvParseOptions();
            CsvEncodingInfo encoding = CsvEncodingInfo.Detect(bytes);
            string text = encoding.Decode(bytes);
            string newline;
            bool finalNewline;
            List<RawRecord> rawRecords = ScanRecords(text, out newline, out finalNewline);
            List<CsvRecord> records = new List<CsvRecord>(rawRecords.Count);
            List<CsvParseDiagnostic> diagnostics = new List<CsvParseDiagnostic>();

            for (int i = 0; i < rawRecords.Count; i++)
            {
                RawRecord raw = rawRecords[i];
                bool malformed;
                List<string> values = ParseFields(raw.Content, out malformed);
                CsvRecordKind kind = Classify(values, raw.Content, options);
                records.Add(new CsvRecord(i, kind, raw.Content, raw.LineEnding, values, malformed));
                if (malformed)
                {
                    CsvDiagnosticSeverity severity = options.StrictQuotes ? CsvDiagnosticSeverity.Error : CsvDiagnosticSeverity.Warning;
                    diagnostics.Add(new CsvParseDiagnostic(severity, "Unclosed or malformed quoted field; original text will be retained until edited.", i));
                }
            }

            if (options.HasHeader)
            {
                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i].Kind == CsvRecordKind.Data || records[i].Kind == CsvRecordKind.Unknown)
                    {
                        records[i].Kind = CsvRecordKind.Header;
                        break;
                    }
                }
            }

            return new CsvDocument(records, diagnostics, options, encoding, bytes, sourcePath, newline, finalNewline);
        }

        public static CsvDocument LoadFromFile(string path, CsvParseOptions options = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A CSV path is required.", "path");
            string fullPath = Path.GetFullPath(path);
            return Load(File.ReadAllBytes(fullPath), options, fullPath);
        }

        public bool SetCell(int recordIndex, int columnIndex, string value)
        {
            if (recordIndex < 0 || recordIndex >= _records.Count) throw new ArgumentOutOfRangeException("recordIndex");
            if (columnIndex < 0) throw new ArgumentOutOfRangeException("columnIndex");
            CsvRecord record = _records[recordIndex];
            string oldValue = record.GetValue(columnIndex);
            int oldCellCount = record.CellCount;
            value = value ?? string.Empty;
            if (string.Equals(oldValue, value, StringComparison.Ordinal)) return false;
            record.SetValue(columnIndex, value);
            History.Record(new CsvCellEdit(recordIndex, columnIndex, oldValue, value, oldCellCount, record.CellCount));
            return true;
        }

        /// <summary>
        /// Applies multiple physical record/column assignments as one undoable operation.
        /// Duplicate coordinates are collapsed (the last assignment wins), and assignments
        /// that end at their current value are ignored.
        /// </summary>
        public bool SetCells(IEnumerable<CsvCellAssignment> assignments)
        {
            if (assignments == null) throw new ArgumentNullException("assignments");
            List<CsvCellAssignment> normalized = new List<CsvCellAssignment>();
            Dictionary<long, int> positions = new Dictionary<long, int>();
            foreach (CsvCellAssignment assignment in assignments)
            {
                ValidateCoordinate(assignment.RecordIndex, assignment.ColumnIndex);
                long key = MakeCoordinateKey(assignment.RecordIndex, assignment.ColumnIndex);
                int position;
                if (positions.TryGetValue(key, out position)) normalized[position] = assignment;
                else
                {
                    positions.Add(key, normalized.Count);
                    normalized.Add(assignment);
                }
            }

            List<CsvCellEdit> edits = new List<CsvCellEdit>(normalized.Count);
            for (int i = 0; i < normalized.Count; i++)
            {
                CsvCellAssignment assignment = normalized[i];
                CsvRecord record = _records[assignment.RecordIndex];
                string value = assignment.Value ?? string.Empty;
                string oldValue = record.GetValue(assignment.ColumnIndex);
                if (string.Equals(oldValue, value, StringComparison.Ordinal)) continue;
                int oldCellCount = record.CellCount;
                record.SetValue(assignment.ColumnIndex, value);
                edits.Add(new CsvCellEdit(assignment.RecordIndex, assignment.ColumnIndex, oldValue, value,
                    oldCellCount, record.CellCount));
            }

            if (edits.Count == 0) return false;
            History.RecordBatch(edits);
            return true;
        }

        /// <summary>Applies a batch of cell edits. OldValue is ignored; the document is authoritative.</summary>
        public bool SetCells(IEnumerable<CsvCellEdit> edits)
        {
            if (edits == null) throw new ArgumentNullException("edits");
            List<CsvCellAssignment> assignments = new List<CsvCellAssignment>();
            foreach (CsvCellEdit edit in edits)
                assignments.Add(new CsvCellAssignment(edit.RecordIndex, edit.ColumnIndex, edit.NewValue));
            return SetCells(assignments);
        }

        /// <summary>Returns a snapshot of current dirty cells and their original/current values.</summary>
        public IReadOnlyList<CsvCellChange> GetChanges()
        {
            List<CsvCellChange> changes = new List<CsvCellChange>();
            for (int recordIndex = 0; recordIndex < _records.Count; recordIndex++)
            {
                CsvRecord record = _records[recordIndex];
                int count = Math.Max(record.CellCount, record.OriginalCellCount);
                for (int columnIndex = 0; columnIndex < count; columnIndex++)
                {
                    if (record.IsCellDirty(columnIndex))
                        changes.Add(new CsvCellChange(recordIndex, columnIndex,
                            record.GetOriginalValue(columnIndex), record.GetValue(columnIndex)));
                }
            }
            return changes;
        }

        public IReadOnlyList<CsvCellChange> GetDirtyChanges()
        {
            return GetChanges();
        }

        /// <summary>Restores one physical cell to its loaded value as one undoable operation.</summary>
        public bool RevertCell(int recordIndex, int columnIndex)
        {
            ValidateCoordinate(recordIndex, columnIndex);
            CsvRecord record = _records[recordIndex];
            if (!record.IsCellDirty(columnIndex)) return false;

            string oldValue = record.GetValue(columnIndex);
            int oldCellCount = record.CellCount;
            record.RestoreCell(columnIndex, record.GetOriginalValue(columnIndex));
            History.Record(new CsvCellEdit(recordIndex, columnIndex, oldValue,
                record.GetValue(columnIndex), oldCellCount, record.CellCount));
            return true;
        }

        /// <summary>Restores every dirty cell and row width as one undoable operation.</summary>
        public bool RevertAll()
        {
            IReadOnlyList<CsvCellChange> changes = GetChanges();
            if (changes.Count == 0) return false;

            List<CsvCellEdit> edits = new List<CsvCellEdit>(changes.Count);
            for (int i = 0; i < changes.Count; i++)
            {
                CsvCellChange change = changes[i];
                CsvRecord record = _records[change.RecordIndex];
                string oldValue = record.GetValue(change.ColumnIndex);
                int oldCellCount = record.CellCount;
                bool lastCellInRecord = i == changes.Count - 1 || changes[i + 1].RecordIndex != change.RecordIndex;
                if (lastCellInRecord)
                    record.RestoreValue(change.ColumnIndex, change.OriginalValue, record.OriginalCellCount);
                else
                    record.SetValue(change.ColumnIndex, change.OriginalValue);
                edits.Add(new CsvCellEdit(change.RecordIndex, change.ColumnIndex, oldValue,
                    record.GetValue(change.ColumnIndex), oldCellCount, record.CellCount));
            }
            History.RecordBatch(edits);
            return true;
        }

        public bool RevertAllChanges()
        {
            return RevertAll();
        }

        public string GetCell(int recordIndex, int columnIndex)
        {
            if (recordIndex < 0 || recordIndex >= _records.Count) throw new ArgumentOutOfRangeException("recordIndex");
            if (columnIndex < 0) throw new ArgumentOutOfRangeException("columnIndex");
            return _records[recordIndex].GetValue(columnIndex);
        }

        public bool Undo() { return History.Undo(this); }
        public bool Redo() { return History.Redo(this); }

        /// <summary>Returns bytes suitable for writing. An unchanged document returns its exact original bytes.</summary>
        public byte[] Serialize()
        {
            if (!IsDirty) return (byte[])_originalBytes.Clone();
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < _records.Count; i++)
            {
                CsvRecord record = _records[i];
                text.Append(record.GetRawContent());
                string lineEnding = record.OriginalLineEnding;
                if (record.IsDirty && lineEnding.Length == 0 && i < _records.Count - 1) lineEnding = _newline;
                text.Append(lineEnding);
            }
            return EncodingInfo.Encode(text.ToString());
        }

        /// <summary>
        /// Safely writes through a sibling temporary file and atomically replaces the destination where supported.
        /// A changed destination is rejected to prevent silently overwriting someone else's edits.
        /// </summary>
        public void SaveToFile(string path = null, bool detectExternalChanges = true)
        {
            string destination = string.IsNullOrEmpty(path) ? _sourcePath : Path.GetFullPath(path);
            if (string.IsNullOrEmpty(destination)) throw new InvalidOperationException("No source path is associated with this document.");
            bool isOriginalPath = !string.IsNullOrEmpty(_sourcePath) && PathsEqualForConflict(destination, _sourcePath);
            if (detectExternalChanges && isOriginalPath)
            {
                if (!File.Exists(destination) || !ByteArraysEqual(File.ReadAllBytes(destination), _originalBytes))
                    throw new CsvExternalChangeException(destination);
            }

            byte[] bytes = Serialize();
            string temp = destination + ".csvtool-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(destination)) File.Replace(temp, destination, null);
                else File.Move(temp, destination);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }

            _originalBytes = bytes;
            _sourcePath = destination;
            for (int i = 0; i < _records.Count; i++) _records[i].MarkClean(_records[i].GetRawContent());
            History.Clear();
        }

        /// <summary>Compares file paths using the platform's expected case rules for conflict detection.</summary>
        public static bool PathsEqualForConflict(string firstPath, string secondPath)
        {
            if (string.IsNullOrEmpty(firstPath) || string.IsNullOrEmpty(secondPath)) return false;
            StringComparison comparison = IsWindowsPlatform() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(firstPath), Path.GetFullPath(secondPath), comparison);
        }

        private static bool IsWindowsPlatform()
        {
            PlatformID platform = Environment.OSVersion.Platform;
            return platform == PlatformID.Win32NT || platform == PlatformID.Win32S || platform == PlatformID.Win32Windows || platform == PlatformID.WinCE;
        }

        internal void ApplyHistoryValue(int recordIndex, int columnIndex, string value, int targetCellCount)
        {
            if (recordIndex < 0 || recordIndex >= _records.Count) throw new ArgumentOutOfRangeException("recordIndex");
            _records[recordIndex].RestoreValue(columnIndex, value, targetCellCount);
        }

        private void ValidateCoordinate(int recordIndex, int columnIndex)
        {
            if (recordIndex < 0 || recordIndex >= _records.Count) throw new ArgumentOutOfRangeException("recordIndex");
            if (columnIndex < 0) throw new ArgumentOutOfRangeException("columnIndex");
        }

        private static long MakeCoordinateKey(int recordIndex, int columnIndex)
        {
            return ((long)recordIndex << 32) ^ (uint)columnIndex;
        }

        private static CsvRecordKind Classify(IReadOnlyList<string> values, string raw, CsvParseOptions options)
        {
            string probe = options.TrimClassificationWhitespace ? raw.TrimStart(' ', '\t', '\r', '\n') : raw;
            if (raw.Length == 0 || AreAllValuesEmpty(values)) return CsvRecordKind.Blank;
            if ((options.CommentDetector != null && options.CommentDetector(values)) || StartsWithAny(probe, options.CommentPrefixes)) return CsvRecordKind.Comment;
            if ((options.SectionDetector != null && options.SectionDetector(values)) || StartsWithAny(probe, options.SectionPrefixes)) return CsvRecordKind.Section;
            return CsvRecordKind.Data;
        }

        private static bool AreAllValuesEmpty(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0) return true;
            for (int i = 0; i < values.Count; i++)
                if (!string.IsNullOrEmpty(values[i])) return false;
            return true;
        }

        private static bool StartsWithAny(string value, IReadOnlyList<string> prefixes)
        {
            if (prefixes == null) return false;
            for (int i = 0; i < prefixes.Count; i++)
                if (!string.IsNullOrEmpty(prefixes[i]) && value.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private struct RawRecord
        {
            public string Content;
            public string LineEnding;
        }

        private static List<RawRecord> ScanRecords(string text, out string newline, out bool hasFinalNewline)
        {
            List<RawRecord> result = new List<RawRecord>();
            newline = "\n";
            hasFinalNewline = false;
            if (string.IsNullOrEmpty(text)) return result;

            int start = 0;
            bool inQuotes = false;
            bool fieldAtStart = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') i++;
                        else inQuotes = false;
                    }
                    continue;
                }

                if (c == '"' && fieldAtStart) { inQuotes = true; fieldAtStart = false; continue; }
                if (c == ',') { fieldAtStart = true; continue; }
                if (c == '\r' || c == '\n')
                {
                    string ending = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : c.ToString();
                    result.Add(new RawRecord { Content = text.Substring(start, i - start), LineEnding = ending });
                    if (newline == "\n") newline = ending;
                    hasFinalNewline = true;
                    if (ending.Length == 2) i++;
                    start = i + 1;
                    fieldAtStart = true;
                    continue;
                }
                fieldAtStart = false;
            }

            if (start < text.Length)
            {
                result.Add(new RawRecord { Content = text.Substring(start), LineEnding = string.Empty });
                hasFinalNewline = false;
            }
            return result;
        }

        private static List<string> ParseFields(string raw, out bool malformed)
        {
            List<string> values = new List<string>();
            StringBuilder field = new StringBuilder();
            bool inQuotes = false;
            bool fieldAtStart = true;
            bool quoteClosed = false;
            malformed = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < raw.Length && raw[i + 1] == '"') { field.Append('"'); i++; }
                        else { inQuotes = false; quoteClosed = true; }
                    }
                    else field.Append(c);
                    continue;
                }

                if (c == '"' && fieldAtStart) { inQuotes = true; fieldAtStart = false; continue; }
                if (c == ',')
                {
                    values.Add(field.ToString());
                    field.Length = 0;
                    fieldAtStart = true;
                    quoteClosed = false;
                    continue;
                }
                if (quoteClosed && c != ' ' && c != '\t') malformed = true;
                if (c == '"') malformed = true;
                field.Append(c);
                fieldAtStart = false;
            }

            if (inQuotes) malformed = true;
            values.Add(field.ToString());
            return values;
        }

        private static bool ByteArraysEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++) if (first[i] != second[i]) return false;
            return true;
        }
    }
}
