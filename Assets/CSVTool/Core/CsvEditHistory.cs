using System;
using System.Collections.Generic;

namespace CsvTool.Core
{
    public struct CsvCellEdit
    {
        public CsvCellEdit(int recordIndex, int columnIndex, string oldValue, string newValue)
            : this(recordIndex, columnIndex, oldValue, newValue, -1, -1)
        {
        }

        internal CsvCellEdit(int recordIndex, int columnIndex, string oldValue, string newValue, int oldCellCount, int newCellCount)
        {
            RecordIndex = recordIndex;
            ColumnIndex = columnIndex;
            OldValue = oldValue ?? string.Empty;
            NewValue = newValue ?? string.Empty;
            OldCellCount = oldCellCount;
            NewCellCount = newCellCount;
        }

        public int RecordIndex;
        public int ColumnIndex;
        public string OldValue;
        public string NewValue;
        public int OldCellCount;
        public int NewCellCount;
    }

    /// <summary>A cell value assignment used by <see cref="CsvDocument.SetCells"/>.
    /// Coordinates are physical CSV record and column indexes (including the header and
    /// non-data records), not filtered/view indexes.</summary>
    public struct CsvCellAssignment
    {
        public CsvCellAssignment(int recordIndex, int columnIndex, string value)
        {
            RecordIndex = recordIndex;
            ColumnIndex = columnIndex;
            Value = value ?? string.Empty;
        }

        public int RecordIndex;
        public int ColumnIndex;
        public string Value;
    }

    /// <summary>A value difference between the loaded document and its current in-memory state.</summary>
    public struct CsvCellChange
    {
        public CsvCellChange(int recordIndex, int columnIndex, string originalValue, string currentValue)
        {
            RecordIndex = recordIndex;
            ColumnIndex = columnIndex;
            OriginalValue = originalValue ?? string.Empty;
            CurrentValue = currentValue ?? string.Empty;
        }

        public int RecordIndex;
        public int ColumnIndex;
        public string OriginalValue;
        public string CurrentValue;

        // Friendly aliases for callers that prefer old/new terminology.
        public string OldValue { get { return OriginalValue; } }
        public string NewValue { get { return CurrentValue; } }
    }

    internal sealed class CsvEditBatch
    {
        public readonly List<CsvCellEdit> Edits;

        public CsvEditBatch(List<CsvCellEdit> edits)
        {
            Edits = edits;
        }
    }

    internal interface ICsvEditOperation
    {
        void Undo(CsvDocument document);
        void Redo(CsvDocument document);
    }

    internal sealed class CsvCellEditOperation : ICsvEditOperation
    {
        private readonly List<CsvCellEdit> edits;
        public CsvCellEditOperation(List<CsvCellEdit> edits) { this.edits = new List<CsvCellEdit>(edits); }
        public void Undo(CsvDocument document) { for (int i = edits.Count - 1; i >= 0; i--) { CsvCellEdit edit = edits[i]; document.ApplyHistoryValue(edit.RecordIndex, edit.ColumnIndex, edit.OldValue, edit.OldCellCount); } }
        public void Redo(CsvDocument document) { for (int i = 0; i < edits.Count; i++) { CsvCellEdit edit = edits[i]; document.ApplyHistoryValue(edit.RecordIndex, edit.ColumnIndex, edit.NewValue, edit.NewCellCount); } }
    }

    /// <summary>A small command history independent of Unity's Undo system and serialized object state.</summary>
    public sealed class CsvEditHistory
    {
        private readonly List<ICsvEditOperation> _batches = new List<ICsvEditOperation>();
        private int _cursor;

        /// <summary>Number of undoable operations. A batch is one operation.</summary>
        public int Count { get { return _batches.Count; } }
        public bool CanUndo { get { return _cursor > 0; } }
        public bool CanRedo { get { return _cursor < _batches.Count; } }

        internal void Record(CsvCellEdit edit)
        {
            RecordBatch(new List<CsvCellEdit> { edit });
        }

        internal void RecordBatch(List<CsvCellEdit> edits)
        {
            if (edits == null || edits.Count == 0) return;
            if (_cursor < _batches.Count) _batches.RemoveRange(_cursor, _batches.Count - _cursor);
            _batches.Add(new CsvCellEditOperation(edits));
            _cursor++;
        }

        internal void RecordOperation(ICsvEditOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (_cursor < _batches.Count) _batches.RemoveRange(_cursor, _batches.Count - _cursor);
            _batches.Add(operation);
            _cursor++;
        }

        public bool Undo(CsvDocument document)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (!CanUndo) return false;
            _batches[_cursor - 1].Undo(document);
            _cursor--;
            return true;
        }

        public bool Redo(CsvDocument document)
        {
            if (document == null) throw new ArgumentNullException("document");
            if (!CanRedo) return false;
            _batches[_cursor].Redo(document);
            _cursor++;
            return true;
        }

        public void Clear()
        {
            _batches.Clear();
            _cursor = 0;
        }
    }
}
