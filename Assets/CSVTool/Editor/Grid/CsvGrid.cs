using System;
using System.Collections.Generic;
using System.Text;
using CsvTool.Core;
using Neo.EditorUI;
using UnityEditor;
using UnityEngine;

namespace CsvTool.Editor
{
    /// <summary>
    /// Two-axis virtualized CSV grid. The grid owns only view state; it never writes to or otherwise
    /// mutates the supplied <see cref="CsvDocument"/>. Cell edits are reported through events so the
    /// window/controller which owns the document remains the single writer.
    ///
    /// Row coordinates are body-row coordinates (the first non-header record is row zero). Use
    /// <see cref="CsvGridSelection.RecordIndex"/> to address the corresponding document record.
    /// </summary>
    public sealed class CsvGrid
    {
        private const float ScrollbarSize = 16f;
        private const float BoundaryHitWidth = 4f;
        private const float SelectionBorderWidth = 2f;

        private static GUIContent s_tempContent;
        private static GUIStyle s_cellStyle;
        private static GUIStyle s_editCellStyle;
        private static GUIStyle s_headerStyle;
        private static GUIStyle s_rowNumberStyle;

        private readonly CsvGridSettings _settings;
        private readonly List<int> _bodyRecordIndices = new List<int>();
        private readonly List<float> _columnWidths = new List<float>();
        private readonly List<float> _columnOffsets = new List<float>();
        private readonly List<string> _fallbackColumnNames = new List<string>();
        private readonly List<string> _rowNumberTexts = new List<string>();

        private CsvDocument _document;
        private IReadOnlyList<int> _rowMapSource;
        private bool _rowMapInitialized;
        private int _headerRecordIndex = -1;
        private int _columnCount;
        private float _totalColumnWidth;
        private float _frozenWidth;
        private Rect _lastRect;
        private Rect _bodyRect;
        private Rect _headerRect;
        private float _bodyWidth;
        private float _bodyHeight;
        private float _scrollableViewportWidth;
        private float _scrollableContentWidth;
        private int _firstVisibleRow;
        private int _lastVisibleRow;
        private int _firstVisibleColumn;
        private int _lastVisibleColumn;
        private int _controlId;
        private int _resizingColumn = -1;
        private float _resizeStartMouseX;
        private float _resizeStartWidth;
        private int _lastResizeClickColumn = -1;
        private double _lastResizeClickTime = double.NegativeInfinity;
        private CsvGridSelection _selection;
        private int _selectedPhysicalRecordIndex = -1;
        private Vector2 _scrollPosition;
        private CsvGridVisibleStats _visibleStats;
        private bool _isEditing;
        private int _editingRecordIndex = -1;
        private int _editingColumn = -1;
        private string _editingOldValue = string.Empty;
        private string _editingText = string.Empty;
        private bool _editFocusPending;
        private readonly List<string> _autocompleteSuggestions = new List<string>();
        private string _autocompleteQuery;
        private int _autocompleteRecord = -1;
        private int _autocompleteColumn = -1;
        private bool _autocompleteVisible;
        private bool _autocompleteSuppressed;
        private Rect _autocompleteRect;
        // The active cell remains CsvGridSelection for backwards compatibility.  These fields
        // only add spreadsheet-style range state; all coordinates are visual body-row
        // coordinates, while edits continue to carry physical CsvDocument record indices.
        private int _anchorRow = -1;
        private int _anchorColumn = -1;
        // A left-button drag starts from an ordinary cell click and moves only the active
        // corner. Keeping the anchor separate is what makes click-and-drag match Shift+arrow
        // range selection without ever converting visual rows into physical record indices.
        private bool _isRangeDragging;

        private const string EditControlName = "CsvGridCellEdit";

        /// <summary>Creates a grid with project-independent defaults.</summary>
        public CsvGrid(CsvGridSettings settings = null)
        {
            _settings = settings ?? new CsvGridSettings();
            _selection = CsvGridSelection.Invalid;
            _selectedPhysicalRecordIndex = -1;
        }

        public CsvGridSettings Settings { get { return _settings; } }
        public CsvGridSelection Selection { get { return _selection; } }
        public CsvGridVisibleStats VisibleStats { get { return _visibleStats; } }
        public Vector2 ScrollPosition { get { return _scrollPosition; } set { _scrollPosition = value; } }
        public bool IsEditing { get { return _isEditing; } }
        public string EditingText { get { return _editingText; } }
        public bool IsAutocompleteVisible { get { return _autocompleteVisible; } }
        /// <summary>Current cached suggestions; valid until the next edit/provider refresh.</summary>
        public IReadOnlyList<string> AutocompleteSuggestions { get { return _autocompleteSuggestions; } }
        public CsvGridCellEdit PendingEdit
        {
            get
            {
                return _isEditing
                    ? new CsvGridCellEdit(_document, _editingRecordIndex, _editingColumn, _editingOldValue, _editingText)
                    : CsvGridCellEdit.Empty;
            }
        }

        /// <summary>Invoked after a user click or keyboard move changes the selected cell.</summary>
        public event Action<CsvGridSelection> SelectionChanged;

        /// <summary>Raised when an edit is committed. The document is deliberately not mutated here.</summary>
        public event Action<CsvGridCellEdit> CellEditCommitted;

        /// <summary>Raised for a multi-cell edit such as paste or clearing a selection. The grid never mutates the document.</summary>
        public event Action<CsvGridBatchEdit> BatchEditCommitted;

        /// <summary>Alias for consumers which name the event after the paste operation.</summary>
        public event Action<CsvGridBatchEdit> PasteCommitted;

        /// <summary>Raised for the owning controller/window to perform an undo.</summary>
        public event Action UndoRequested;

        /// <summary>Raised for the owning controller/window to perform a redo.</summary>
        public event Action RedoRequested;

        /// <summary>Raised for the owning controller/window to save the current document.</summary>
        public event Action SaveRequested;

        /// <summary>Raised when a view-only interaction needs its owning window to repaint.</summary>
        public event Action RepaintRequested;

        /// <summary>Supplies edit suggestions for a physical record/column and current text.</summary>
        public Func<int, int, string, IReadOnlyList<string>> AutocompleteProvider { get; set; }

        /// <summary>Raised when Ctrl-click requests navigation from a cell value.</summary>
        public event Action<int, int, string> ReferenceNavigationRequested;

        /// <summary>Requests a spreadsheet-style row/column context menu. Coordinates remain physical.</summary>
        public event Action<CsvGridSelection, bool, Vector2> StructureMenuRequested;

        /// <summary>Requests capped suggestions without requiring the popup to be visible.</summary>
        public IReadOnlyList<string> GetAutocompleteSuggestions(int physicalRecord, int column, string editText)
        {
            _autocompleteSuggestions.Clear();
            if (AutocompleteProvider == null) return _autocompleteSuggestions;
            IReadOnlyList<string> values = AutocompleteProvider(physicalRecord, column, editText ?? string.Empty);
            int cap = Mathf.Clamp(_settings.AutocompleteMaxSuggestions, 1, 64);
            if (values != null)
                for (int i = 0; i < values.Count && _autocompleteSuggestions.Count < cap; i++)
                    if (values[i] != null) _autocompleteSuggestions.Add(values[i]);
            return _autocompleteSuggestions;
        }

        /// <summary>Sets the number of leading columns which remain visible while scrolling.</summary>
        public int FrozenColumnCount
        {
            get { return _settings.FrozenColumnCount; }
            set { _settings.FrozenColumnCount = Mathf.Max(0, value); RecalculateColumnLayout(); }
        }

        /// <summary>Clears cached document layout and selection state.</summary>
        public void Reset()
        {
            CommitEdit();
            _document = null;
            _bodyRecordIndices.Clear();
            _headerRecordIndex = -1;
            _columnCount = 0;
            _columnWidths.Clear();
            _columnOffsets.Clear();
            _fallbackColumnNames.Clear();
            _rowNumberTexts.Clear();
            _rowMapSource = null;
            _rowMapInitialized = false;
            _scrollPosition = Vector2.zero;
            _selection = CsvGridSelection.Invalid;
            _anchorRow = _anchorColumn = -1;
            _visibleStats = CsvGridVisibleStats.Empty;
            _resizingColumn = -1;
            _isRangeDragging = false;
            ClearEditState();
            ClearAutocomplete();
        }

        /// <summary>Selects a body row and column. Coordinates are clamped to the current document.</summary>
        public void SelectCell(int row, int column, bool ensureVisible = true)
        {
            if (_document == null || _bodyRecordIndices.Count == 0 || _columnCount == 0)
            {
                SetSelection(CsvGridSelection.Invalid);
                return;
            }

            row = Mathf.Clamp(row, 0, _bodyRecordIndices.Count - 1);
            column = Mathf.Clamp(column, 0, _columnCount - 1);
            if (_isEditing && (_bodyRecordIndices[row] != _editingRecordIndex || column != _editingColumn))
                CommitEdit();
            SetSelection(new CsvGridSelection(row, column, _bodyRecordIndices[row]), ensureVisible);
        }

        /// <summary>
        /// Selects a cell by its physical <see cref="CsvDocument.Records"/> index. The current
        /// visual map is searched so filtered-out records cannot be selected accidentally.
        /// </summary>
        public bool SelectPhysicalCell(int recordIndex, int column, bool ensureVisible = true)
        {
            if (_document == null || recordIndex < 0 || column < 0 || column >= _columnCount)
                return false;

            for (int row = 0; row < _bodyRecordIndices.Count; row++)
            {
                if (_bodyRecordIndices[row] != recordIndex) continue;
                SelectCell(row, column, ensureVisible);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Selects a physical cell without disturbing the viewport when it is already fully visible.
        /// A hidden target is revealed with the minimum movement needed, leaving frozen columns
        /// and any still-useful row or column context in place.
        /// </summary>
        public bool SelectPhysicalCellWithContext(int recordIndex, int column)
        {
            return SelectPhysicalCellForNavigation(recordIndex, column,
                CsvGridRevealMode.Minimal, CsvGridRevealMode.Minimal, null);
        }

        /// <summary>Selects a physical cell using explicit, independently controlled viewport axes.</summary>
        public bool SelectPhysicalCellForNavigation(int recordIndex, int column,
            CsvGridRevealMode vertical, CsvGridRevealMode horizontal, float? rowScreenOffset)
        {
            if (_document == null || recordIndex < 0 || column < 0 || column >= _columnCount)
                return false;

            for (int row = 0; row < _bodyRecordIndices.Count; row++)
            {
                if (_bodyRecordIndices[row] != recordIndex) continue;
                SelectCell(row, column, false);
                if (rowScreenOffset.HasValue)
                    _scrollPosition = CsvGridNavigation.RestoreRowScreenOffset(_scrollPosition,
                        row * _settings.RowHeight, rowScreenOffset.Value);
                RevealSelection(vertical, horizontal);
                return true;
            }
            return false;
        }

        /// <summary>Returns the selected record's current pixel offset from the body viewport top.</summary>
        public bool TryGetRecordScreenOffset(int recordIndex, out float offset)
        {
            for (int row = 0; row < _bodyRecordIndices.Count; row++)
            {
                if (_bodyRecordIndices[row] != recordIndex) continue;
                offset = row * _settings.RowHeight - _scrollPosition.y;
                return true;
            }
            offset = 0f;
            return false;
        }

        public bool IsPhysicalRecordFullyVisible(int recordIndex)
        {
            for (int row = 0; row < _bodyRecordIndices.Count; row++)
            {
                if (_bodyRecordIndices[row] != recordIndex) continue;
                float top = row * _settings.RowHeight;
                return top >= _scrollPosition.y && top + _settings.RowHeight <= _scrollPosition.y + _bodyHeight;
            }
            return false;
        }

        public int TopVisiblePhysicalRecordIndex
        {
            get
            {
                return _firstVisibleRow >= 0 && _firstVisibleRow < _bodyRecordIndices.Count
                    ? _bodyRecordIndices[_firstVisibleRow] : -1;
            }
        }

        /// <summary>The current rectangular selection, or Invalid when no range is selected.</summary>
        public CsvGridRangeSelection RangeSelection
        {
            get
            {
                if (!_selection.IsValid || _anchorRow < 0 || _anchorColumn < 0)
                    return CsvGridRangeSelection.Invalid;
                return new CsvGridRangeSelection(_anchorRow, _anchorColumn, _selection.Row, _selection.Column);
            }
        }

        /// <summary>Alias for RangeSelection.</summary>
        public CsvGridRangeSelection SelectionRange { get { return RangeSelection; } }

        public bool HasRangeSelection { get { return RangeSelection.IsValid && !RangeSelection.IsSingleCell; } }

        /// <summary>Requests keyboard focus for this grid on its next GUI pass.</summary>
        public void Focus()
        {
            if (_controlId != 0) GUIUtility.keyboardControl = _controlId;
        }

        /// <summary>
        /// Tells the grid that the contents of the map passed to <see cref="Draw(Rect, CsvDocument,
        /// IReadOnlyList{int})"/> changed in place. A new list instance is detected automatically.
        /// </summary>
        public void InvalidateVisibleRecordMap()
        {
            _rowMapInitialized = false;
        }

        /// <summary>Refreshes column count/width caches after another component changes the document.</summary>
        public void InvalidateDocumentLayout()
        {
            if (_document == null) return;
            EnsureColumnWidths(_document.ColumnCount);
            RecalculateColumnLayout();
        }

        /// <summary>Draws the grid into a caller-supplied rect; each cell uses explicit rectangles.</summary>
        public void Draw(Rect rect, CsvDocument document)
        {
            Draw(rect, document, null);
        }

        /// <summary>
        /// Draws using an optional visual-to-physical record map. The map contains indices into
        /// <c>document.Records</c>, allowing a shell-owned filter/search to drive the visible rows.
        /// </summary>
        public void Draw(Rect rect, CsvDocument document, IReadOnlyList<int> visualRecordIndices)
        {
            NormalizeSettings();
            _controlId = GUIUtility.GetControlID(FocusType.Keyboard);
            _lastRect = rect;
            EnsureDocument(document);

            if (document == null)
            {
                _visibleStats = CsvGridVisibleStats.Empty;
                DrawEmpty(rect, "No CSV document loaded.");
                return;
            }

            EnsureRowMap(visualRecordIndices);

            CalculateViewport(rect);
            ClampScrollPosition();
            CalculateVisibleRanges();
            HandleInput();
            CalculateVisibleRanges();
            UpdateStats();

            DrawChrome();
            DrawHeader();
            DrawBody();
            DrawScrollbars();
            DrawAutocompletePopup();
            CalculateVisibleRanges();
            UpdateStats();
        }

        private void NormalizeSettings()
        {
            _settings.RowHeight = Mathf.Max(1f, _settings.RowHeight);
            _settings.HeaderHeight = Mathf.Max(1f, _settings.HeaderHeight);
            _settings.RowNumberWidth = Mathf.Max(1f, _settings.RowNumberWidth);
            _settings.MinColumnWidth = Mathf.Max(1f, _settings.MinColumnWidth);
            _settings.MaxColumnWidth = Mathf.Max(_settings.MinColumnWidth, _settings.MaxColumnWidth);
            _settings.DefaultColumnWidth = Mathf.Clamp(_settings.DefaultColumnWidth,
                _settings.MinColumnWidth, _settings.MaxColumnWidth);
            _settings.FrozenColumnCount = Mathf.Max(0, _settings.FrozenColumnCount);
        }

        private void EnsureDocument(CsvDocument document)
        {
            if (ReferenceEquals(_document, document))
                return;

            CommitEdit();
            _document = document;
            _bodyRecordIndices.Clear();
            _headerRecordIndex = -1;
            _selection = CsvGridSelection.Invalid;
            _selectedPhysicalRecordIndex = -1;
            _rowMapSource = null;
            _rowMapInitialized = false;
            _scrollPosition = Vector2.zero;
            _resizingColumn = -1;
            _isRangeDragging = false;
            _anchorRow = _anchorColumn = -1;
            ClearEditState();

            if (document == null)
            {
                _columnCount = 0;
                _columnWidths.Clear();
                _columnOffsets.Clear();
                _fallbackColumnNames.Clear();
                _rowNumberTexts.Clear();
                return;
            }

            for (int i = 0; i < document.Records.Count; i++)
            {
                CsvRecord record = document.Records[i];
                if (record.Kind == CsvRecordKind.Header && _headerRecordIndex < 0)
                    _headerRecordIndex = i;
            }

            _columnCount = document.ColumnCount;
            EnsureColumnWidths(_columnCount);
            RecalculateColumnLayout();
        }

        private void EnsureRowMap(IReadOnlyList<int> visualRecordIndices)
        {
            if (_rowMapInitialized && ReferenceEquals(_rowMapSource, visualRecordIndices)) return;

            int selectedPhysicalRecord = _selection.IsValid ? _selection.RecordIndex : _selectedPhysicalRecordIndex;
            int selectedColumn = _selection.IsValid ? _selection.Column : 0;
            _bodyRecordIndices.Clear();
            _rowNumberTexts.Clear();
            if (visualRecordIndices == null)
            {
                for (int i = 0; i < _document.Records.Count; i++)
                    if (i != _headerRecordIndex) _bodyRecordIndices.Add(i);
            }
            else
            {
                for (int i = 0; i < visualRecordIndices.Count; i++)
                {
                    int recordIndex = visualRecordIndices[i];
                    if (recordIndex >= 0 && recordIndex < _document.Records.Count && recordIndex != _headerRecordIndex)
                        _bodyRecordIndices.Add(recordIndex);
                }
            }
            for (int i = 0; i < _bodyRecordIndices.Count; i++)
                _rowNumberTexts.Add((_bodyRecordIndices[i] + 1).ToString());
            _rowMapSource = visualRecordIndices;
            _rowMapInitialized = true;

            if (_bodyRecordIndices.Count == 0 || _columnCount == 0)
            {
                SetSelection(CsvGridSelection.Invalid, false);
                return;
            }

            int visualRow = -1;
            if (selectedPhysicalRecord >= 0)
                for (int i = 0; i < _bodyRecordIndices.Count; i++)
                    if (_bodyRecordIndices[i] == selectedPhysicalRecord) { visualRow = i; break; }
            if (visualRow >= 0)
                SetSelection(new CsvGridSelection(visualRow, Mathf.Clamp(selectedColumn, 0, _columnCount - 1), selectedPhysicalRecord), false);
            else
                SelectCell(0, 0, false);
        }

        private void EnsureColumnWidths(int count)
        {
            count = Mathf.Max(0, count);
            while (_columnWidths.Count < count) _columnWidths.Add(_settings.DefaultColumnWidth);
            if (_columnWidths.Count > count) _columnWidths.RemoveRange(count, _columnWidths.Count - count);
            while (_fallbackColumnNames.Count < count) _fallbackColumnNames.Add(ColumnName(_fallbackColumnNames.Count));
            if (_fallbackColumnNames.Count > count) _fallbackColumnNames.RemoveRange(count, _fallbackColumnNames.Count - count);
            _columnCount = count;
            RecalculateColumnLayout();
        }

        private void RecalculateColumnLayout()
        {
            _columnOffsets.Clear();
            float offset = 0f;
            for (int i = 0; i < _columnWidths.Count; i++)
            {
                _columnOffsets.Add(offset);
                offset += Mathf.Clamp(_columnWidths[i], _settings.MinColumnWidth, _settings.MaxColumnWidth);
            }

            _totalColumnWidth = offset;
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            _frozenWidth = frozen == 0 ? 0f : _columnOffsets[frozen - 1] + _columnWidths[frozen - 1];
            _scrollableContentWidth = Mathf.Max(0f, _totalColumnWidth - _frozenWidth);
        }

        private void CalculateViewport(Rect rect)
        {
            float width = Mathf.Max(1f, rect.width - _settings.RowNumberWidth - ScrollbarSize);
            float height = Mathf.Max(1f, rect.height - _settings.HeaderHeight - ScrollbarSize);
            _headerRect = new Rect(rect.x + _settings.RowNumberWidth, rect.y, width, _settings.HeaderHeight);
            _bodyRect = new Rect(rect.x + _settings.RowNumberWidth, rect.y + _settings.HeaderHeight, width, height);
            _bodyWidth = width;
            _bodyHeight = height;
            _scrollableViewportWidth = Mathf.Max(1f, width - _frozenWidth);
        }

        private void ClampScrollPosition()
        {
            float maxX = Mathf.Max(0f, _scrollableContentWidth - _scrollableViewportWidth);
            float maxY = Mathf.Max(0f, _bodyRecordIndices.Count * _settings.RowHeight - _bodyHeight);
            _scrollPosition.x = Mathf.Clamp(_scrollPosition.x, 0f, maxX);
            _scrollPosition.y = Mathf.Clamp(_scrollPosition.y, 0f, maxY);
        }

        private void CalculateVisibleRanges()
        {
            if (_bodyRecordIndices.Count == 0 || _columnCount == 0)
            {
                _firstVisibleRow = _lastVisibleRow = 0;
                _firstVisibleColumn = _lastVisibleColumn = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
                return;
            }

            _firstVisibleRow = Mathf.Clamp(Mathf.FloorToInt(_scrollPosition.y / _settings.RowHeight), 0, _bodyRecordIndices.Count - 1);
            _lastVisibleRow = Mathf.Min(_bodyRecordIndices.Count,
                Mathf.CeilToInt((_scrollPosition.y + _bodyHeight) / _settings.RowHeight));

            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            _firstVisibleColumn = frozen;
            _lastVisibleColumn = frozen;
            if (frozen < _columnCount && _scrollableContentWidth > 0f)
            {
                _firstVisibleColumn = FindColumnAtContentX(frozen, _scrollPosition.x);
                _lastVisibleColumn = FindColumnAtContentX(frozen, _scrollPosition.x + _scrollableViewportWidth);
                if (_lastVisibleColumn < _columnCount) _lastVisibleColumn++;
                _firstVisibleColumn = Mathf.Clamp(_firstVisibleColumn, frozen, _columnCount - 1);
                _lastVisibleColumn = Mathf.Clamp(_lastVisibleColumn, _firstVisibleColumn + 1, _columnCount);
            }
        }

        private int FindColumnAtContentX(int firstColumn, float scrollX)
        {
            float target = _columnOffsets[firstColumn] + scrollX;
            int low = firstColumn;
            int high = _columnCount - 1;
            while (low < high)
            {
                int middle = (low + high + 1) >> 1;
                if (_columnOffsets[middle] <= target) low = middle;
                else high = middle - 1;
            }
            return low;
        }

        private void UpdateStats()
        {
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            int visibleColumns = Mathf.Max(0, frozen) + Mathf.Max(0, _lastVisibleColumn - _firstVisibleColumn);
            int visibleRows = Mathf.Max(0, _lastVisibleRow - _firstVisibleRow);
            _visibleStats = new CsvGridVisibleStats
            {
                VisibleBodyRowStart = _firstVisibleRow,
                VisibleBodyRowCount = visibleRows,
                VisibleColumnStart = _columnCount == 0 ? 0 : Mathf.Min(_firstVisibleColumn, _columnCount - 1),
                VisibleColumnCount = visibleColumns,
                DrawnCellCount = visibleRows * visibleColumns,
                TotalBodyRows = _bodyRecordIndices.Count,
                TotalColumns = _columnCount,
                ScrollPosition = _scrollPosition,
                FrozenColumnCount = frozen
            };
        }

        private void HandleInput()
        {
            Event current = Event.current;
            // Cursor regions must be registered on every IMGUI pass. Registering them only for
            // MouseMove leaves Unity without the region during the layout/repaint pass that
            // resolves the cursor.
            AddResizeCursorRects();

            if (HandleAutocompleteInput(current))
                return;

            if (current.type == EventType.ContextClick)
            {
                bool header = _headerRect.Contains(current.mousePosition);
                Rect rowGutter = new Rect(_lastRect.x, _bodyRect.y, _settings.RowNumberWidth, _bodyRect.height);
                bool gutter = rowGutter.Contains(current.mousePosition);
                if (_bodyRect.Contains(current.mousePosition) || header || gutter)
                {
                    int row = header ? (_selection.IsValid ? _selection.Row : 0) : RowAtScreenY(current.mousePosition.y);
                    int column = gutter ? (_selection.IsValid ? _selection.Column : 0) : ColumnAtScreenX(current.mousePosition.x);
                    if (row >= 0 && column >= 0)
                    {
                        SelectCell(row, column, false);
                        StructureMenuRequested?.Invoke(_selection, header, current.mousePosition);
                        current.Use();
                        return;
                    }
                }
            }

            if (current.type == EventType.KeyDown)
            {
                if (_isEditing)
                {
                    HandleEditingKeyboard(current);
                    return;
                }

                if (_selection.IsValid && GUIUtility.keyboardControl == _controlId)
                {
                    if (HandleGridCommand(current)) return;
                    HandleKeyboard(current);
                }
            }

            if (current.type == EventType.ScrollWheel &&
                (_bodyRect.Contains(current.mousePosition) || _headerRect.Contains(current.mousePosition)))
            {
                if (current.shift)
                    _scrollPosition.x += current.delta.y * _settings.DefaultColumnWidth;
                else
                    _scrollPosition.y += current.delta.y * _settings.RowHeight * 2f;
                ClampScrollPosition();
                CalculateVisibleRanges();
                current.Use();
                return;
            }

            if (_resizingColumn >= 0)
            {
                if (current.type == EventType.MouseDrag)
                {
                    _lastResizeClickColumn = -1;
                    float width = Mathf.Clamp(_resizeStartWidth + current.mousePosition.x - _resizeStartMouseX,
                        _settings.MinColumnWidth, _settings.MaxColumnWidth);
                    _columnWidths[_resizingColumn] = width;
                    RecalculateColumnLayout();
                    ClampScrollPosition();
                    CalculateVisibleRanges();
                    GUI.changed = true;
                    current.Use();
                    return;
                }
                if (current.type == EventType.MouseUp)
                {
                    _resizingColumn = -1;
                    GUIUtility.hotControl = 0;
                    current.Use();
                    return;
                }
            }

            if (_isRangeDragging)
            {
                if (current.type == EventType.MouseDrag)
                {
                    UpdateRangeDrag(current.mousePosition);
                    current.Use();
                    return;
                }
                if (current.type == EventType.MouseUp)
                {
                    _isRangeDragging = false;
                    GUIUtility.hotControl = 0;
                    current.Use();
                    return;
                }
            }

            if (current.type == EventType.MouseDown && current.button == 0)
            {
                // A click outside the active cell has the same commit semantics as leaving a
                // spreadsheet text field. Do this before changing selection or consuming the click.
                bool sameEditingCell = false;
                if (_isEditing && _bodyRect.Contains(current.mousePosition))
                {
                    int editingRow = RowAtScreenY(current.mousePosition.y);
                    int editingColumn = ColumnAtScreenX(current.mousePosition.x);
                    sameEditingCell = editingRow >= 0 && editingColumn == _editingColumn &&
                        GetRecordIndex(editingRow) == _editingRecordIndex;
                }
                if (_isEditing && !sameEditingCell)
                    CommitEdit();

                int boundary = FindResizeBoundary(current.mousePosition);
                if (boundary >= 0)
                {
                    if (IsResizeDoubleClick(boundary, current.clickCount))
                    {
                        AutoSizeColumn(boundary);
                        _lastResizeClickColumn = -1;
                        current.Use();
                        return;
                    }
                    _lastResizeClickColumn = boundary;
                    _lastResizeClickTime = EditorApplication.timeSinceStartup;
                    _resizingColumn = boundary;
                    _resizeStartMouseX = current.mousePosition.x;
                    _resizeStartWidth = _columnWidths[boundary];
                    GUIUtility.hotControl = _controlId;
                    GUIUtility.keyboardControl = _controlId;
                    current.Use();
                    return;
                }

                _lastResizeClickColumn = -1;

                if (_bodyRect.Contains(current.mousePosition))
                {
                    int row = RowAtScreenY(current.mousePosition.y);
                    int column = ColumnAtScreenX(current.mousePosition.x);
                    if (row >= 0 && column >= 0)
                    {
                        if (current.control || current.command)
                        {
                            int physicalRecord = GetRecordIndex(row);
                            if (physicalRecord >= 0)
                                ReferenceNavigationRequested?.Invoke(physicalRecord, column,
                                    _document.GetCell(physicalRecord, column));
                            current.Use();
                            return;
                        }
                        if (sameEditingCell)
                            return;
                        if (current.shift) SetRangeCell(row, column);
                        else SelectCell(row, column);
                        if (current.clickCount >= 2)
                            BeginEdit();
                        else
                        {
                            _isRangeDragging = true;
                            GUIUtility.hotControl = _controlId;
                            GUIUtility.keyboardControl = _controlId;
                        }
                        current.Use();
                        return;
                    }
                }

                Rect gutterBody = new Rect(_lastRect.x, _bodyRect.y, _settings.RowNumberWidth, _bodyRect.height);
                if (gutterBody.Contains(current.mousePosition))
                {
                    int row = RowAtScreenY(current.mousePosition.y);
                    if (row >= 0 && _columnCount > 0)
                    {
                        if (current.shift) SetRangeCell(row, _selection.IsValid ? _selection.Column : 0);
                        else SelectCell(row, _selection.IsValid ? _selection.Column : 0);
                        GUIUtility.keyboardControl = _controlId;
                        current.Use();
                        return;
                    }
                }

                if (_headerRect.Contains(current.mousePosition))
                {
                    int column = ColumnAtScreenX(current.mousePosition.x);
                    if (column >= 0 && _bodyRecordIndices.Count > 0)
                    {
                        if (current.shift) SetRangeCell(_selection.IsValid ? _selection.Row : 0, column);
                        else SelectCell(_selection.IsValid ? _selection.Row : 0, column);
                        GUIUtility.keyboardControl = _controlId;
                        current.Use();
                        return;
                    }
                }
            }

        }

        private bool HandleGridCommand(Event current)
        {
            bool command = current.control || current.command;

            if (!command && !current.shift && current.keyCode == KeyCode.F2)
            {
                BeginEdit();
                current.Use();
                return true;
            }

            if (!command && !current.shift && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter))
            {
                BeginEdit();
                current.Use();
                return true;
            }

            if (!command && !current.shift &&
                (current.keyCode == KeyCode.Delete || current.keyCode == KeyCode.Backspace))
            {
                ClearSelection();
                current.Use();
                return true;
            }

            if (!command) return false;

            if (current.keyCode == KeyCode.C)
            {
                GUIUtility.systemCopyBuffer = CopySelectionTsv();
                current.Use();
                return true;
            }

            if (current.keyCode == KeyCode.V)
            {
                PasteTsv(GUIUtility.systemCopyBuffer ?? string.Empty);
                current.Use();
                return true;
            }

            if (current.keyCode == KeyCode.S)
            {
                CommitEdit();
                SaveRequested?.Invoke();
                current.Use();
                return true;
            }

            if (current.keyCode == KeyCode.Z)
            {
                if (current.shift) RedoRequested?.Invoke();
                else UndoRequested?.Invoke();
                current.Use();
                return true;
            }

            if (current.keyCode == KeyCode.Y)
            {
                RedoRequested?.Invoke();
                current.Use();
                return true;
            }

            return false;
        }

        /// <summary>Formats the current selection as spreadsheet-compatible TSV.</summary>
        public string CopySelectionTsv()
        {
            if (_document == null || !_selection.IsValid) return string.Empty;
            CsvGridRangeSelection range = RangeSelection;
            if (!range.IsValid) range = new CsvGridRangeSelection(_selection.Row, _selection.Column,
                _selection.Row, _selection.Column);

            List<IReadOnlyList<string>> rows = new List<IReadOnlyList<string>>(range.RowCount);
            for (int row = range.TopRow; row <= range.BottomRow; row++)
            {
                List<string> values = new List<string>(range.ColumnCount);
                int recordIndex = GetRecordIndex(row);
                for (int column = range.LeftColumn; column <= range.RightColumn; column++)
                    values.Add(recordIndex >= 0 ? _document.GetCell(recordIndex, column) : string.Empty);
                rows.Add(values);
            }
            return CsvGridClipboard.FormatTsv(rows);
        }

        /// <summary>
        /// Parses and emits a paste batch. Existing one-cell paste callers retain the original
        /// CellEditCommitted event; larger pastes use BatchEditCommitted and never mutate the model.
        /// </summary>
        public bool PasteTsv(string text)
        {
            if (_document == null || !_selection.IsValid || string.IsNullOrEmpty(text)) return false;
            List<List<string>> rows = CsvGridClipboard.ParseTsv(text);
            if (rows.Count == 0) return false;

            CsvGridRangeSelection selectedRange = RangeSelection;
            int startRow = selectedRange.IsValid ? selectedRange.TopRow : _selection.Row;
            int startColumn = selectedRange.IsValid ? selectedRange.LeftColumn : _selection.Column;
            if (rows.Count == 1 && rows[0].Count == 1)
            {
                CommitValue(rows[0][0], _selection.RecordIndex, _selection.Column,
                    _document.GetCell(_selection.RecordIndex, _selection.Column));
                return true;
            }

            List<CsvGridCellEdit> edits = new List<CsvGridCellEdit>();
            int maxColumn = _columnCount;
            for (int rowOffset = 0; rowOffset < rows.Count; rowOffset++)
            {
                int visualRow = startRow + rowOffset;
                // A filtered grid's row map is the paste boundary. This deliberately prevents a
                // paste from accidentally writing to a hidden/non-matching physical record.
                int recordIndex = GetRecordIndex(visualRow);
                if (recordIndex < 0) continue;
                List<string> pastedRow = rows[rowOffset];
                for (int columnOffset = 0; columnOffset < pastedRow.Count; columnOffset++)
                {
                    int column = startColumn + columnOffset;
                    if (column < 0) continue;
                    string oldValue = column < _document.Records[recordIndex].CellCount
                        ? _document.GetCell(recordIndex, column) : string.Empty;
                    string newValue = pastedRow[columnOffset] ?? string.Empty;
                    maxColumn = Mathf.Max(maxColumn, column + 1);
                    if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                        edits.Add(new CsvGridCellEdit(_document, recordIndex, column, oldValue, newValue));
                }
            }

            if (edits.Count == 0) return false;
            CsvGridBatchEdit batch = new CsvGridBatchEdit(_document, edits, maxColumn > _columnCount);
            BatchEditCommitted?.Invoke(batch);
            PasteCommitted?.Invoke(batch);
            GUI.changed = true;
            return true;
        }

        private void HandleEditingKeyboard(Event current)
        {
            if ((current.control || current.command) && current.keyCode == KeyCode.Space)
            {
                RefreshAutocomplete(true);
                current.Use();
                return;
            }
            if (_autocompleteVisible && (current.keyCode == KeyCode.UpArrow || current.keyCode == KeyCode.DownArrow))
            {
                int delta = current.keyCode == KeyCode.DownArrow ? 1 : -1;
                _autocompleteSelection = Mathf.Clamp(_autocompleteSelection + delta, 0,
                    _autocompleteSuggestions.Count - 1);
                current.Use();
                return;
            }
            // Save is a grid command even while an edit is active. The text field retains all
            // other editing shortcuts, including arrows, native text undo, and clipboard actions.
            if ((current.control || current.command) && current.keyCode == KeyCode.S)
            {
                CommitEdit();
                SaveRequested?.Invoke();
                current.Use();
                return;
            }

            if (current.keyCode == KeyCode.Escape)
            {
                if (_autocompleteVisible)
                {
                    ClearAutocomplete();
                    _autocompleteSuppressed = true;
                    current.Use();
                    return;
                }
                CancelEdit();
                current.Use();
                return;
            }

            if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
            {
                if (AcceptAutocomplete()) { current.Use(); return; }
                CommitEdit();
                current.Use();
                return;
            }

            if (current.keyCode == KeyCode.Tab)
            {
                if (AcceptAutocomplete()) { current.Use(); return; }
                int row = _selection.Row;
                int column = _selection.Column + (current.shift ? -1 : 1);
                if (column < 0) { column = _columnCount - 1; row--; }
                else if (column >= _columnCount) { column = 0; row++; }
                CommitEdit();
                if (_bodyRecordIndices.Count > 0)
                    SelectCell(row, column, true);
                current.Use();
                return;
            }

            // In particular, do not handle Left/Right/Up/Down here: EditorGUI.TextField must
            // receive them so arrows move the caret inside the active cell.
        }

        private void BeginEdit()
        {
            if (_document == null || !_selection.IsValid) return;
            _isEditing = true;
            _editingRecordIndex = _selection.RecordIndex;
            _editingColumn = _selection.Column;
            _editingOldValue = _document.GetCell(_editingRecordIndex, _editingColumn);
            _editingText = _editingOldValue;
            // A double-click reaches BeginEdit before the named TextField exists for this
            // IMGUI pass. Claim focus for the grid immediately so a previously focused
            // Record/Inspector field drawn later in the same event cannot restart its old
            // edit session and display suggestions for the wrong column.
            GUIUtility.keyboardControl = _controlId;
            _editFocusPending = true;
            ClearAutocomplete();
            _autocompleteSuppressed = false;
            GUI.changed = true;
        }

        private void CommitEdit()
        {
            if (!_isEditing) return;
            CommitValue(_editingText, _editingRecordIndex, _editingColumn, _editingOldValue);
        }

        /// <summary>Commits the active cell edit, if any.</summary>
        public void CommitEditing()
        {
            CommitEdit();
        }

        /// <summary>Cancels the active cell edit, if any.</summary>
        public void CancelEditing()
        {
            CancelEdit();
        }

        private void CommitValue(string value)
        {
            if (!_selection.IsValid || _document == null) return;
            string oldValue = _document.GetCell(_selection.RecordIndex, _selection.Column);
            CommitValue(value ?? string.Empty, _selection.RecordIndex, _selection.Column, oldValue);
        }

        /// <summary>
        /// Clears all non-empty cells in the current rectangular selection. Multiple cells are
        /// emitted as one batch so the owner can validate and undo them atomically.
        /// </summary>
        public bool ClearSelection()
        {
            if (!_selection.IsValid || _document == null) return false;
            CsvGridRangeSelection range = RangeSelection;
            if (!range.IsValid) range = new CsvGridRangeSelection(_selection.Row, _selection.Column,
                _selection.Row, _selection.Column);

            if (range.IsSingleCell)
            {
                string oldValue = _document.GetCell(_selection.RecordIndex, _selection.Column);
                CommitValue(string.Empty, _selection.RecordIndex, _selection.Column, oldValue);
                GUI.changed = true;
                return !string.IsNullOrEmpty(oldValue);
            }

            List<CsvGridCellEdit> edits = new List<CsvGridCellEdit>();
            for (int row = range.TopRow; row <= range.BottomRow; row++)
            {
                int recordIndex = GetRecordIndex(row);
                if (recordIndex < 0) continue;
                for (int column = range.LeftColumn; column <= range.RightColumn; column++)
                {
                    string oldValue = _document.GetCell(recordIndex, column);
                    if (!string.IsNullOrEmpty(oldValue))
                        edits.Add(new CsvGridCellEdit(_document, recordIndex, column, oldValue, string.Empty));
                }
            }

            if (edits.Count == 0) return false;
            BatchEditCommitted?.Invoke(new CsvGridBatchEdit(_document, edits, false));
            GUI.changed = true;
            return true;
        }

        private void CommitValue(string value, int recordIndex, int column, string oldValue)
        {
            bool wasEditing = _isEditing;
            CsvDocument sourceDocument = _document;
            ClearEditState();
            if (recordIndex < 0 || column < 0 || string.Equals(oldValue, value, StringComparison.Ordinal))
                return;
            CellEditCommitted?.Invoke(new CsvGridCellEdit(sourceDocument, recordIndex, column, oldValue, value));
            if (wasEditing) GUI.changed = true;
        }

        private void CancelEdit()
        {
            if (!_isEditing) return;
            ClearEditState();
            GUI.changed = true;
        }

        private void ClearEditState()
        {
            _isEditing = false;
            _editingRecordIndex = -1;
            _editingColumn = -1;
            _editingOldValue = string.Empty;
            _editingText = string.Empty;
            _editFocusPending = false;
            ClearAutocomplete();
        }

        private int _autocompleteSelection;

        private void ClearAutocomplete()
        {
            _autocompleteVisible = false;
            _autocompleteQuery = null;
            _autocompleteRecord = -1;
            _autocompleteColumn = -1;
            _autocompleteSelection = 0;
            _autocompleteSuggestions.Clear();
        }

        private bool HandleAutocompleteInput(Event current)
        {
            if (!_isEditing || !_autocompleteVisible) return false;
            return NeoAutocompleteOverlay.HandleInput(current, _autocompleteRect,
                _autocompleteSuggestions.Count, _settings.RowHeight, ref _autocompleteSelection,
                () => { AcceptAutocomplete(); }, RepaintRequested);
        }

        private void RefreshAutocomplete(bool force)
        {
            if (!_isEditing || AutocompleteProvider == null) { ClearAutocomplete(); return; }
            _autocompleteSuppressed = false;
            string query = _editingText ?? string.Empty;
            if (!force && _autocompleteVisible && _autocompleteRecord == _editingRecordIndex &&
                _autocompleteColumn == _editingColumn && string.Equals(_autocompleteQuery, query, StringComparison.Ordinal))
                return;
            IReadOnlyList<string> provided = AutocompleteProvider(_editingRecordIndex, _editingColumn, query);
            _autocompleteSuggestions.Clear();
            int cap = Mathf.Clamp(_settings.AutocompleteMaxSuggestions, 1, 64);
            if (provided != null)
                for (int i = 0; i < provided.Count && _autocompleteSuggestions.Count < cap; i++)
                    if (provided[i] != null) _autocompleteSuggestions.Add(provided[i]);
            _autocompleteQuery = query;
            _autocompleteRecord = _editingRecordIndex;
            _autocompleteColumn = _editingColumn;
            _autocompleteSelection = 0;
            _autocompleteVisible = _autocompleteSuggestions.Count > 0;
        }

        private bool AcceptAutocomplete()
        {
            if (!_autocompleteVisible || _autocompleteSuggestions.Count == 0) return false;
            _editingText = _autocompleteSuggestions[Mathf.Clamp(_autocompleteSelection, 0, _autocompleteSuggestions.Count - 1)];
            _autocompleteSuppressed = true;
            ClearAutocomplete();
            // A focused IMGUI text field renders its TextEditor cache instead of immediately
            // adopting a programmatic value change. Give focus to the grid for this pass so the
            // field synchronizes from _editingText, then restore edit focus on the repaint.
            GUIUtility.keyboardControl = _controlId;
            _editFocusPending = true;
            GUI.changed = true;
            RepaintRequested?.Invoke();
            return true;
        }

        private void DrawAutocompletePopup()
        {
            if (!_isEditing || !_autocompleteVisible || _autocompleteSuggestions.Count == 0) return;
            if (Event.current.type != EventType.Repaint) return;

            float height = Mathf.Min(8, _autocompleteSuggestions.Count) * _settings.RowHeight;
            _autocompleteRect = new Rect(_autocompleteRect.x, _autocompleteRect.y, 280f, height);
            NeoAutocompleteOverlay.Draw(_autocompleteRect, _autocompleteSuggestions,
                _autocompleteSelection, _settings.RowHeight);
        }

        private void HandleKeyboard(Event current)
        {
            int row = _selection.Row;
            int column = _selection.Column;
            int page = Mathf.Max(1, Mathf.FloorToInt(_bodyHeight / _settings.RowHeight) - 1);
            bool handled = true;
            switch (current.keyCode)
            {
                case KeyCode.LeftArrow: column--; break;
                case KeyCode.RightArrow: column++; break;
                case KeyCode.UpArrow: row--; break;
                case KeyCode.DownArrow: row++; break;
                case KeyCode.PageUp: row -= page; break;
                case KeyCode.PageDown: row += page; break;
                case KeyCode.Home:
                    if (current.control || current.command) { row = 0; column = 0; }
                    else column = 0;
                    break;
                case KeyCode.End:
                    if (current.control || current.command) { row = _bodyRecordIndices.Count - 1; column = _columnCount - 1; }
                    else column = _columnCount - 1;
                    break;
                default: handled = false; break;
            }

            if (!handled) return;
            if (current.shift) SetRangeCell(row, column, true);
            else SelectCell(row, column, true);
            current.Use();
        }

        private int RowAtScreenY(float screenY)
        {
            float local = screenY - _bodyRect.y + _scrollPosition.y;
            int row = Mathf.FloorToInt(local / _settings.RowHeight);
            return row >= 0 && row < _bodyRecordIndices.Count ? row : -1;
        }

        private void UpdateRangeDrag(Vector2 mousePosition)
        {
            // IMGUI keeps sending drag events to our hot control after the pointer leaves the
            // body. Clamp to the body so a quick drag to an edge still selects the last visible
            // cell instead of dropping the range update altogether.
            float x = Mathf.Clamp(mousePosition.x, _bodyRect.x, _bodyRect.xMax - 0.01f);
            float y = Mathf.Clamp(mousePosition.y, _bodyRect.y, _bodyRect.yMax - 0.01f);
            int row = RowAtScreenY(y);
            int column = ColumnAtScreenX(x);
            if (row >= 0 && column >= 0) SetRangeCell(row, column);
        }

        private int ColumnAtScreenX(float screenX)
        {
            float local = screenX - _bodyRect.x;
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            if (local < 0f || local > _bodyWidth) return -1;
            if (local < _frozenWidth)
                return FindColumnAtLocalX(local, 0, frozen);
            if (frozen >= _columnCount) return -1;
            return FindColumnAtContentX(frozen, _scrollPosition.x + local - _frozenWidth);
        }

        private int FindColumnAtLocalX(float localX, int start, int end, float scrollX = 0f)
        {
            for (int i = start; i < end; i++)
            {
                float left = _columnOffsets[i] - (i >= start && start > 0 ? scrollX : 0f);
                float right = left + _columnWidths[i];
                if (localX >= left && localX < right) return i;
            }
            return -1;
        }

        private int FindResizeBoundary(Vector2 mouse)
        {
            if (!_headerRect.Contains(mouse)) return -1;
            float localX = mouse.x - _bodyRect.x;
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            for (int i = 0; i < _columnCount; i++)
            {
                float x = ResizeBoundaryX(i);
                if (IsResizeBoundaryVisible(i, x, frozen) && Mathf.Abs(localX - x) <= BoundaryHitWidth) return i;
            }
            return -1;
        }

        private void AddResizeCursorRects()
        {
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            for (int i = 0; i < frozen; i++) AddResizeCursorRect(i, ResizeBoundaryX(i), frozen);
            for (int i = _firstVisibleColumn; i < _lastVisibleColumn; i++)
                AddResizeCursorRect(i, ResizeBoundaryX(i), frozen);
        }

        // Resize handles are always the right edge of the column they resize. Scrolling columns
        // are drawn in a clipped pane, so their screen-local position only subtracts scroll X;
        // the frozen-pane offset is already accounted for by that pane's group origin.
        private float ResizeBoundaryX(int column)
        {
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            return _columnOffsets[column] + _columnWidths[column] -
                (column < frozen ? 0f : _scrollPosition.x);
        }

        private bool IsResizeBoundaryVisible(int column, float localX, int frozen)
        {
            return column < frozen
                ? localX >= 0f && localX <= _frozenWidth
                : localX >= _frozenWidth && localX <= _bodyWidth;
        }

        private void AutoSizeColumn(int column)
        {
            if (_document == null || column < 0 || column >= _columnCount) return;

            SetTempContent(GetHeaderValue(column));
            float width = HeaderStyle.CalcSize(s_tempContent).x;
            for (int recordIndex = 0; recordIndex < _document.Records.Count; recordIndex++)
            {
                if (recordIndex == _headerRecordIndex) continue;
                SetTempContent(_document.Records[recordIndex].GetValue(column));
                width = Mathf.Max(width, CellStyle.CalcSize(s_tempContent).x);
            }

            _columnWidths[column] = Mathf.Clamp(width, _settings.MinColumnWidth, _settings.MaxColumnWidth);
            RecalculateColumnLayout();
            ClampScrollPosition();
            CalculateVisibleRanges();
            GUI.changed = true;
        }

        private bool IsResizeDoubleClick(int column, int clickCount)
        {
            if (clickCount >= 2) return true;
            return column == _lastResizeClickColumn &&
                EditorApplication.timeSinceStartup - _lastResizeClickTime <= 0.5d;
        }

        private void AddResizeCursorRect(int column, float localX, int frozen)
        {
            if (!IsResizeBoundaryVisible(column, localX, frozen)) return;
            EditorGUIUtility.AddCursorRect(new Rect(_bodyRect.x + localX - BoundaryHitWidth,
                _headerRect.y, BoundaryHitWidth * 2f, _headerRect.height), MouseCursor.ResizeHorizontal);
        }

        private void SetSelection(CsvGridSelection value, bool ensureVisible = false)
        {
            SetActiveSelection(value, ensureVisible, true);
        }

        private void SetRangeCell(int row, int column, bool ensureVisible = false)
        {
            if (_document == null || _bodyRecordIndices.Count == 0 || _columnCount == 0)
            {
                SetSelection(CsvGridSelection.Invalid, false);
                return;
            }

            if (_selection.IsValid && (_anchorRow < 0 || _anchorColumn < 0))
            {
                _anchorRow = _selection.Row;
                _anchorColumn = _selection.Column;
            }
            if (_anchorRow < 0 || _anchorColumn < 0)
            {
                _anchorRow = Mathf.Clamp(row, 0, _bodyRecordIndices.Count - 1);
                _anchorColumn = Mathf.Clamp(column, 0, _columnCount - 1);
            }

            row = Mathf.Clamp(row, 0, _bodyRecordIndices.Count - 1);
            column = Mathf.Clamp(column, 0, _columnCount - 1);
            SetActiveSelection(new CsvGridSelection(row, column, _bodyRecordIndices[row]), ensureVisible, false);
        }

        private void SetActiveSelection(CsvGridSelection value, bool ensureVisible, bool resetAnchor)
        {
            if (_selection.Row == value.Row && _selection.Column == value.Column &&
                _selection.RecordIndex == value.RecordIndex && _selection.IsValid == value.IsValid)
            {
                if (resetAnchor && value.IsValid)
                {
                    _anchorRow = value.Row;
                    _anchorColumn = value.Column;
                }
                return;
            }
            _selection = value;
            if (value.IsValid)
            {
                _selectedPhysicalRecordIndex = value.RecordIndex;
                if (resetAnchor)
                {
                    _anchorRow = value.Row;
                    _anchorColumn = value.Column;
                }
            }
            else if (resetAnchor) _anchorRow = _anchorColumn = -1;
            if (ensureVisible) EnsureSelectionVisible();
            SelectionChanged?.Invoke(_selection);
            GUI.changed = true;
        }

        private void EnsureSelectionVisible()
        {
            if (!_selection.IsValid) return;
            if (_selection.Row < _firstVisibleRow) _scrollPosition.y = _selection.Row * _settings.RowHeight;
            else if (_selection.Row >= _lastVisibleRow)
                _scrollPosition.y = (_selection.Row + 1) * _settings.RowHeight - _bodyHeight;

            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            if (_selection.Column >= frozen)
            {
                float left = _columnOffsets[_selection.Column] - _frozenWidth;
                float right = left + _columnWidths[_selection.Column];
                if (left < _scrollPosition.x) _scrollPosition.x = left;
                else if (right > _scrollPosition.x + _scrollableViewportWidth)
                    _scrollPosition.x = right - _scrollableViewportWidth;
            }
            ClampScrollPosition();
        }

        private void RevealSelection(CsvGridRevealMode vertical, CsvGridRevealMode horizontal)
        {
            if (!_selection.IsValid) return;

            float rowTop = _selection.Row * _settings.RowHeight;
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            bool frozenColumn = _selection.Column < frozen;
            float left = frozenColumn ? 0f : _columnOffsets[_selection.Column] - _frozenWidth;
            float width = _columnWidths[_selection.Column];
            _scrollPosition = CsvGridNavigation.Reveal(_scrollPosition, rowTop, _settings.RowHeight,
                left, width, _bodyHeight, _scrollableViewportWidth, frozenColumn, vertical, horizontal);
            ClampScrollPosition();
        }

        private void DrawChrome()
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(_lastRect, NeoColors.GridBackground);
            EditorGUI.DrawRect(new Rect(_lastRect.x, _lastRect.y, _settings.RowNumberWidth, _settings.HeaderHeight), NeoColors.GridHeaderBackground);
            EditorGUI.DrawRect(new Rect(_lastRect.x, _bodyRect.y, _settings.RowNumberWidth, _bodyRect.height), NeoColors.GridRowNumberBackground);
            EditorGUI.DrawRect(new Rect(_lastRect.x, _lastRect.yMax - ScrollbarSize, _lastRect.width, 1f), NeoColors.GridLine);
        }

        private void DrawHeader()
        {
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            if (frozen > 0)
            {
                Rect fixedClip = new Rect(_headerRect.x, _headerRect.y, _frozenWidth, _headerRect.height);
                GUI.BeginClip(fixedClip);
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(new Rect(0f, 0f, fixedClip.width, fixedClip.height), NeoColors.GridHeaderBackground);
                    for (int i = 0; i < frozen; i++) DrawHeaderCell(i, _columnOffsets[i]);
                }
                GUI.EndClip();
            }

            Rect scrollingClip = new Rect(_headerRect.x + _frozenWidth, _headerRect.y,
                Mathf.Max(0f, _headerRect.width - _frozenWidth), _headerRect.height);
            GUI.BeginClip(scrollingClip);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(0f, 0f, scrollingClip.width, scrollingClip.height), NeoColors.GridHeaderBackground);
                for (int i = _firstVisibleColumn; i < _lastVisibleColumn; i++)
                    DrawHeaderCell(i, _columnOffsets[i] - _scrollPosition.x - _frozenWidth);
            }
            GUI.EndClip();

            if (Event.current.type == EventType.Repaint)
            {
                if (frozen > 0)
                    EditorGUI.DrawRect(new Rect(_headerRect.x + _frozenWidth - 1f, _headerRect.y, 1f, _headerRect.height), NeoColors.GridSelectionBorder);
                EditorGUI.DrawRect(new Rect(_lastRect.x, _lastRect.yMax - ScrollbarSize, _settings.RowNumberWidth, 1f), NeoColors.GridLine);
            }
        }

        private void DrawHeaderCell(int column, float x)
        {
            Rect cell = new Rect(x, 0f, _columnWidths[column], _settings.HeaderHeight);
            CsvGridRangeSelection range = RangeSelection;
            if (range.IsValid && column >= range.LeftColumn && column <= range.RightColumn)
                EditorGUI.DrawRect(cell, NeoColors.GridSelectionFill);
            else if (_selection.IsValid && _selection.Column == column)
                EditorGUI.DrawRect(cell, NeoColors.GridSelectionFill);
            EditorGUI.DrawRect(new Rect(cell.x, cell.yMax - 1f, cell.width, 1f), NeoColors.GridLine);
            SetTempContent(GetHeaderValue(column));
            GUI.Label(cell, s_tempContent, HeaderStyle);
        }

        private string GetHeaderValue(int column)
        {
            if (_headerRecordIndex >= 0 && _headerRecordIndex < _document.Records.Count)
                return _document.Records[_headerRecordIndex].GetValue(column);
            return _fallbackColumnNames[column];
        }

        private void DrawBody()
        {
            int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
            if (frozen > 0)
            {
                Rect fixedClip = new Rect(_bodyRect.x, _bodyRect.y, _frozenWidth, _bodyRect.height);
                GUI.BeginClip(fixedClip);
                if (Event.current.type == EventType.Repaint || _isEditing)
                {
                    for (int row = _firstVisibleRow; row < _lastVisibleRow; row++)
                    {
                        float y = row * _settings.RowHeight - _scrollPosition.y;
                        if (Event.current.type == EventType.Repaint && _selection.IsValid && _selection.Row == row)
                            EditorGUI.DrawRect(new Rect(0f, y, fixedClip.width, _settings.RowHeight), NeoColors.GridCrosshairFill);
                        for (int column = 0; column < frozen; column++)
                            DrawCell(row, column, _columnOffsets[column], y);
                    }
                }
                GUI.EndClip();
            }

            Rect scrollingClip = new Rect(_bodyRect.x + _frozenWidth, _bodyRect.y,
                Mathf.Max(0f, _bodyRect.width - _frozenWidth), _bodyRect.height);
            GUI.BeginClip(scrollingClip);
            if (Event.current.type == EventType.Repaint || _isEditing)
            {
                for (int row = _firstVisibleRow; row < _lastVisibleRow; row++)
                {
                    float y = row * _settings.RowHeight - _scrollPosition.y;
                    if (Event.current.type == EventType.Repaint && _selection.IsValid && _selection.Row == row)
                        EditorGUI.DrawRect(new Rect(0f, y, scrollingClip.width, _settings.RowHeight), NeoColors.GridCrosshairFill);
                    for (int column = _firstVisibleColumn; column < _lastVisibleColumn; column++)
                        DrawCell(row, column, _columnOffsets[column] - _scrollPosition.x - _frozenWidth, y);
                }
            }
            GUI.EndClip();

            if (Event.current.type == EventType.Repaint)
            {
                CsvGridRangeSelection range = RangeSelection;
                for (int row = _firstVisibleRow; row < _lastVisibleRow; row++)
                {
                    float y = _bodyRect.y + row * _settings.RowHeight - _scrollPosition.y;
                    Rect numberRect = new Rect(_lastRect.x, y, _settings.RowNumberWidth, _settings.RowHeight);
                    if (range.IsValid && range.ContainsRow(row))
                        EditorGUI.DrawRect(numberRect, NeoColors.GridSelectionFill);
                    else if (_selection.IsValid && _selection.Row == row)
                        EditorGUI.DrawRect(numberRect, NeoColors.GridSelectionFill);
                    SetTempContent(_rowNumberTexts[row]);
                    GUI.Label(numberRect, s_tempContent, RowNumberStyle);
                    EditorGUI.DrawRect(new Rect(numberRect.x, numberRect.yMax - 1f, numberRect.width, 1f), NeoColors.GridLine);
                }
                if (frozen > 0)
                    EditorGUI.DrawRect(new Rect(_bodyRect.x + _frozenWidth - 1f, _bodyRect.y, 1f, _bodyRect.height), NeoColors.GridSelectionBorder);
            }
        }

        private void DrawCell(int row, int column, float x, float y)
        {
            Rect cell = new Rect(x, y, _columnWidths[column], _settings.RowHeight);
            bool selected = _selection.IsValid && _selection.Row == row && _selection.Column == column;
            CsvGridRangeSelection range = RangeSelection;
            bool rangeSelected = range.IsValid && range.Contains(row, column);
            bool crosshair = _selection.IsValid && !rangeSelected && (_selection.Row == row || _selection.Column == column);
            bool editing = _isEditing && _editingRecordIndex == _bodyRecordIndices[row] && _editingColumn == column;
            if (Event.current.type == EventType.Repaint)
            {
                if (rangeSelected) EditorGUI.DrawRect(cell, selected ? NeoColors.GridSelectionFillStrong : NeoColors.GridSelectionFill);
                else if (crosshair) EditorGUI.DrawRect(cell, NeoColors.GridCrosshairFill);
                EditorGUI.DrawRect(new Rect(cell.xMax - 1f, cell.y, 1f, cell.height), NeoColors.GridLine);
                EditorGUI.DrawRect(new Rect(cell.x, cell.yMax - 1f, cell.width, 1f), NeoColors.GridLine);
            }

            int recordIndex = _bodyRecordIndices[row];
            if (editing)
            {
                GUI.SetNextControlName(EditControlName);
                string value = EditorGUI.TextField(cell, _editingText, EditCellStyle);
                if (!string.Equals(value, _editingText, StringComparison.Ordinal))
                {
                    _editingText = value;
                    _autocompleteSuppressed = string.IsNullOrEmpty(_editingText);
                    if (_autocompleteSuppressed)
                        ClearAutocomplete();
                    else if (_settings.AutocompleteWhileTyping)
                        RefreshAutocomplete(false);
                    GUI.changed = true;
                }
                if (_settings.AutocompleteWhileTyping && !_autocompleteVisible && !_autocompleteSuppressed)
                    RefreshAutocomplete(false);
                if (_autocompleteVisible)
                {
                    int frozen = Mathf.Clamp(_settings.FrozenColumnCount, 0, _columnCount);
                    float popupX = _bodyRect.x + _columnOffsets[column] -
                        (column < frozen ? 0f : _scrollPosition.x);
                    float popupY = _bodyRect.y + row * _settings.RowHeight - _scrollPosition.y +
                        _settings.RowHeight;
                    _autocompleteRect = new Rect(popupX, popupY, 280f,
                        _settings.RowHeight * Mathf.Min(8, _autocompleteSuggestions.Count));
                }
                if (_editFocusPending && Event.current.type == EventType.Repaint)
                {
                    EditorGUI.FocusTextInControl(EditControlName);
                    _editFocusPending = false;
                }
            }
            else if (Event.current.type == EventType.Repaint)
            {
                SetTempContent(_document.Records[recordIndex].GetValue(column));
                GUI.Label(cell, s_tempContent, CellStyle);
            }
            if (Event.current.type == EventType.Repaint)
            {
                if (rangeSelected) DrawRangeBorder(cell, row, column, range);
                else if (selected) DrawSelectionBorder(cell);
            }
        }

        private void DrawRangeBorder(Rect cell, int row, int column, CsvGridRangeSelection range)
        {
            Color color = NeoColors.GridSelectionBorder;
            if (row == range.TopRow) EditorGUI.DrawRect(new Rect(cell.x, cell.y, cell.width, SelectionBorderWidth), color);
            if (row == range.BottomRow) EditorGUI.DrawRect(new Rect(cell.x, cell.yMax - SelectionBorderWidth, cell.width, SelectionBorderWidth), color);
            if (column == range.LeftColumn) EditorGUI.DrawRect(new Rect(cell.x, cell.y, SelectionBorderWidth, cell.height), color);
            if (column == range.RightColumn) EditorGUI.DrawRect(new Rect(cell.xMax - SelectionBorderWidth, cell.y, SelectionBorderWidth, cell.height), color);
        }

        private void DrawSelectionBorder(Rect cell)
        {
            Color color = NeoColors.GridSelectionBorder;
            EditorGUI.DrawRect(new Rect(cell.x, cell.y, cell.width, SelectionBorderWidth), color);
            EditorGUI.DrawRect(new Rect(cell.x, cell.yMax - SelectionBorderWidth, cell.width, SelectionBorderWidth), color);
            EditorGUI.DrawRect(new Rect(cell.x, cell.y, SelectionBorderWidth, cell.height), color);
            EditorGUI.DrawRect(new Rect(cell.xMax - SelectionBorderWidth, cell.y, SelectionBorderWidth, cell.height), color);
        }

        private void DrawScrollbars()
        {
            Rect vertical = new Rect(_lastRect.xMax - ScrollbarSize, _bodyRect.y, ScrollbarSize, _bodyRect.height);
            Rect horizontal = new Rect(_bodyRect.x + _frozenWidth, _lastRect.yMax - ScrollbarSize,
                Mathf.Max(1f, _bodyWidth - _frozenWidth), ScrollbarSize);
            _scrollPosition.y = GUI.VerticalScrollbar(vertical, _scrollPosition.y, _bodyHeight,
                0f, Mathf.Max(_bodyHeight, _bodyRecordIndices.Count * _settings.RowHeight));
            _scrollPosition.x = GUI.HorizontalScrollbar(horizontal, _scrollPosition.x, _scrollableViewportWidth,
                0f, Mathf.Max(_scrollableViewportWidth, _scrollableContentWidth));
            ClampScrollPosition();
        }

        private void DrawEmpty(Rect rect, string message)
        {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(rect, NeoColors.GridBackground);
            GUI.Label(rect, message, NeoStyles.HeaderSubtitle);
        }

        private int GetRecordIndex(int row)
        {
            return row >= 0 && row < _bodyRecordIndices.Count ? _bodyRecordIndices[row] : -1;
        }

        private static void SetTempContent(string text)
        {
            if (s_tempContent == null) s_tempContent = new GUIContent();
            s_tempContent.text = text ?? string.Empty;
            s_tempContent.tooltip = null;
            s_tempContent.image = null;
        }

        private static string ColumnName(int index)
        {
            string result = string.Empty;
            int value = index + 1;
            while (value > 0)
            {
                int remainder = (value - 1) % 26;
                result = (char)('A' + remainder) + result;
                value = (value - 1) / 26;
            }
            return result;
        }

        private static GUIStyle CellStyle
        {
            get
            {
                if (s_cellStyle == null)
                {
                    s_cellStyle = new GUIStyle(EditorStyles.label)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        clipping = TextClipping.Clip,
                        padding = new RectOffset(5, 5, 0, 0),
                        normal = { textColor = NeoColors.GridCellText }
                    };
                }
                return s_cellStyle;
            }
        }

        private static GUIStyle EditCellStyle
        {
            get
            {
                if (s_editCellStyle == null)
                {
                    s_editCellStyle = new GUIStyle(EditorStyles.textField)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        clipping = TextClipping.Clip,
                        padding = new RectOffset(5, 5, 0, 0)
                    };
                }
                return s_editCellStyle;
            }
        }

        private static GUIStyle HeaderStyle
        {
            get
            {
                if (s_headerStyle == null)
                {
                    s_headerStyle = new GUIStyle(NeoStyles.SectionTitle)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        clipping = TextClipping.Clip,
                        padding = new RectOffset(5, 5, 0, 0),
                        normal = { textColor = NeoColors.GridHeaderText }
                    };
                }
                return s_headerStyle;
            }
        }

        private static GUIStyle RowNumberStyle
        {
            get
            {
                if (s_rowNumberStyle == null)
                {
                    s_rowNumberStyle = new GUIStyle(NeoStyles.MiniDim)
                    {
                        alignment = TextAnchor.MiddleRight,
                        padding = new RectOffset(3, 6, 0, 0),
                        normal = { textColor = NeoColors.GridRowNumberText }
                    };
                }
                return s_rowNumberStyle;
            }
        }
    }

    /// <summary>A rectangular selection in visual body-row/column coordinates.</summary>
    public struct CsvGridRangeSelection
    {
        public readonly int AnchorRow;
        public readonly int AnchorColumn;
        public readonly int ActiveRow;
        public readonly int ActiveColumn;
        public readonly int TopRow;
        public readonly int BottomRow;
        public readonly int LeftColumn;
        public readonly int RightColumn;
        public readonly bool IsValid;

        public CsvGridRangeSelection(int anchorRow, int anchorColumn, int activeRow, int activeColumn)
        {
            AnchorRow = anchorRow;
            AnchorColumn = anchorColumn;
            ActiveRow = activeRow;
            ActiveColumn = activeColumn;
            TopRow = Math.Min(anchorRow, activeRow);
            BottomRow = Math.Max(anchorRow, activeRow);
            LeftColumn = Math.Min(anchorColumn, activeColumn);
            RightColumn = Math.Max(anchorColumn, activeColumn);
            IsValid = anchorRow >= 0 && anchorColumn >= 0 && activeRow >= 0 && activeColumn >= 0;
        }

        public int RowCount { get { return IsValid ? BottomRow - TopRow + 1 : 0; } }
        public int ColumnCount { get { return IsValid ? RightColumn - LeftColumn + 1 : 0; } }
        public bool IsSingleCell { get { return IsValid && RowCount == 1 && ColumnCount == 1; } }
        public bool Contains(int row, int column)
        {
            return IsValid && row >= TopRow && row <= BottomRow && column >= LeftColumn && column <= RightColumn;
        }
        public bool ContainsRow(int row) { return IsValid && row >= TopRow && row <= BottomRow; }
        public bool ContainsColumn(int column) { return IsValid && column >= LeftColumn && column <= RightColumn; }
        public static CsvGridRangeSelection Invalid { get { return new CsvGridRangeSelection(-1, -1, -1, -1); } }
    }

    /// <summary>One immutable batch of physical document edits emitted by the grid.</summary>
    public sealed class CsvGridBatchEdit
    {
        private readonly List<CsvGridCellEdit> _edits;

        public CsvGridBatchEdit(CsvDocument document, IReadOnlyList<CsvGridCellEdit> edits, bool extendsColumns)
        {
            Document = document;
            _edits = edits == null ? new List<CsvGridCellEdit>() : new List<CsvGridCellEdit>(edits);
            ExtendsColumns = extendsColumns;
        }

        public CsvDocument Document { get; private set; }
        public CsvDocument SourceDocument { get { return Document; } }
        public IReadOnlyList<CsvGridCellEdit> Edits { get { return _edits; } }
        public IReadOnlyList<CsvGridCellEdit> CellEdits { get { return _edits; } }
        public IReadOnlyList<CsvGridCellEdit> Changes { get { return _edits; } }
        public bool ExtendsColumns { get; private set; }
        public bool IncludesColumnExtension { get { return ExtendsColumns; } }
        public bool AllowsColumnExtension { get { return ExtendsColumns; } }
        public CsvDocument Source { get { return Document; } }
        public int Count { get { return _edits.Count; } }
    }

    /// <summary>Pure TSV formatter/parser shared by grid clipboard operations and tests.</summary>
    public static class CsvGridClipboard
    {
        public static string FormatTsv(IReadOnlyList<IReadOnlyList<string>> rows)
        {
            if (rows == null || rows.Count == 0) return string.Empty;
            StringBuilder builder = new StringBuilder();
            for (int row = 0; row < rows.Count; row++)
            {
                if (row > 0) builder.Append('\n');
                IReadOnlyList<string> values = rows[row];
                if (values == null) continue;
                for (int column = 0; column < values.Count; column++)
                {
                    if (column > 0) builder.Append('\t');
                    AppendTsvField(builder, values[column]);
                }
            }
            return builder.ToString();
        }

        public static string FormatTsv(IReadOnlyList<List<string>> rows)
        {
            if (rows == null) return string.Empty;
            List<IReadOnlyList<string>> view = new List<IReadOnlyList<string>>(rows.Count);
            for (int i = 0; i < rows.Count; i++) view.Add(rows[i]);
            return FormatTsv(view);
        }

        public static string FormatTsv(IEnumerable<IEnumerable<string>> rows)
        {
            if (rows == null) return string.Empty;
            List<IReadOnlyList<string>> view = new List<IReadOnlyList<string>>();
            foreach (IEnumerable<string> row in rows)
            {
                List<string> values = row == null ? new List<string>() : new List<string>(row);
                view.Add(values);
            }
            return FormatTsv(view);
        }

        private static void AppendTsvField(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            bool quote = value.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0;
            if (!quote) { builder.Append(value); return; }
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
                if (value[i] == '"') builder.Append("\"\"");
                else builder.Append(value[i]);
            builder.Append('"');
        }

        public static List<List<string>> ParseTsv(string text)
        {
            List<List<string>> rows = new List<List<string>>();
            if (string.IsNullOrEmpty(text)) return rows;
            List<string> row = new List<string>();
            StringBuilder field = new StringBuilder();
            bool inQuotes = false;
            bool atFieldStart = true;
            bool fieldStarted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else field.Append(c);
                    continue;
                }

                if (c == '"' && atFieldStart)
                {
                    inQuotes = true;
                    fieldStarted = true;
                    atFieldStart = false;
                    continue;
                }
                if (c == '\t')
                {
                    row.Add(field.ToString());
                    field.Length = 0;
                    atFieldStart = true;
                    fieldStarted = true;
                    continue;
                }
                if (c == '\r' || c == '\n')
                {
                    row.Add(field.ToString());
                    field.Length = 0;
                    rows.Add(row);
                    row = new List<string>();
                    atFieldStart = true;
                    fieldStarted = false;
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    continue;
                }
                field.Append(c);
                atFieldStart = false;
                fieldStarted = true;
            }

            // A final line ending already emitted its row. Do not manufacture an extra empty row,
            // but retain trailing empty fields (the row list is non-empty after a tab).
            if (field.Length > 0 || row.Count > 0 || fieldStarted)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }
            return rows;
        }
    }

    [Serializable]
    public sealed class CsvGridSettings
    {
        public float RowHeight = 20f;
        public float HeaderHeight = 22f;
        public float RowNumberWidth = 48f;
        public float DefaultColumnWidth = 120f;
        public float MinColumnWidth = 36f;
        public float MaxColumnWidth = 900f;
        public int FrozenColumnCount = 1;
        /// <summary>Maximum number of suggestions copied from an autocomplete provider.</summary>
        public int AutocompleteMaxSuggestions = 8;
        /// <summary>When enabled, refreshes suggestions as the active edit text changes.</summary>
        public bool AutocompleteWhileTyping;
    }

    public struct CsvGridSelection
    {
        public readonly int Row;
        public readonly int Column;
        public readonly int RecordIndex;
        public readonly bool IsValid;

        public CsvGridSelection(int row, int column, int recordIndex)
        {
            Row = row;
            Column = column;
            RecordIndex = recordIndex;
            IsValid = row >= 0 && column >= 0 && recordIndex >= 0;
        }

        public static CsvGridSelection Invalid { get { return new CsvGridSelection(-1, -1, -1, false); } }

        private CsvGridSelection(int row, int column, int recordIndex, bool valid)
        {
            Row = row;
            Column = column;
            RecordIndex = recordIndex;
            IsValid = valid;
        }
    }

    /// <summary>Describes a grid edit without taking ownership of the document mutation.</summary>
    public struct CsvGridCellEdit
    {
        public CsvGridCellEdit(int recordIndex, int columnIndex, string oldValue, string newValue)
            : this(null, recordIndex, columnIndex, oldValue, newValue)
        {
        }

        public CsvGridCellEdit(CsvDocument document, int recordIndex, int columnIndex, string oldValue, string newValue)
        {
            Document = document;
            RecordIndex = recordIndex;
            ColumnIndex = columnIndex;
            OldValue = oldValue ?? string.Empty;
            NewValue = newValue ?? string.Empty;
        }

        /// <summary>Physical index into <c>CsvDocument.Records</c>.</summary>
        public readonly CsvDocument Document;
        public readonly int RecordIndex;
        public readonly int ColumnIndex;
        public readonly string OldValue;
        public readonly string NewValue;

        // Aliases make the event convenient for callers which describe this coordinate as the
        // physical record/column rather than using CsvDocument's terminology.
        public int PhysicalRecordIndex { get { return RecordIndex; } }
        public int Column { get { return ColumnIndex; } }

        public static CsvGridCellEdit Empty
        {
            get { return new CsvGridCellEdit(null, -1, -1, string.Empty, string.Empty); }
        }
    }

    public struct CsvGridVisibleStats
    {
        public int VisibleBodyRowStart;
        public int VisibleBodyRowCount;
        public int VisibleColumnStart;
        public int VisibleColumnCount;
        public int DrawnCellCount;
        public int TotalBodyRows;
        public int TotalColumns;
        public int FrozenColumnCount;
        public Vector2 ScrollPosition;

        public static CsvGridVisibleStats Empty
        {
            get { return new CsvGridVisibleStats { VisibleBodyRowStart = 0, VisibleColumnStart = 0 }; }
        }
    }
}
