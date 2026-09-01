using System;
using CsvTool.Schema;
using UnityEditor;
using UnityEngine;

namespace CsvTool.Editor
{
    /// <summary>
    /// A compact Record view which can be hosted beside the Grid. The inspector
    /// deliberately delegates rendering, validation, and edits to
    /// <see cref="CsvRecordView"/> so both record presentations retain exactly
    /// the same physical-record edit semantics.
    /// </summary>
    public sealed class CsvQuickRecordInspector
    {
        private const float HeaderHeight = 24f;
        private const float CloseButtonWidth = 22f;

        private CsvRecordView recordView;
        private CsvTableController controller;
        private bool showCloseButton = true;
        private bool isOpen = true;
        private Rect lastRect;

        /// <summary>Creates an unbound inspector. Call <see cref="Bind"/> before drawing.</summary>
        public CsvQuickRecordInspector()
        {
        }

        /// <summary>Creates and binds an inspector for a table.</summary>
        public CsvQuickRecordInspector(CsvTableController table, CsvTableSchema tableSchema,
            CsvRecordViewDefinition viewDefinition = null)
        {
            Bind(table, tableSchema, viewDefinition);
        }

        public CsvTableController Controller { get { return controller; } }
        public CsvRecordView RecordView { get { return recordView; } }
        public int SelectedRecordIndex
        {
            get { return recordView == null ? -1 : recordView.SelectedRecordIndex; }
        }
        public bool IsBound { get { return recordView != null && controller != null; } }
        public bool IsOpen
        {
            get { return isOpen; }
            set { isOpen = value; }
        }
        public bool ShowCloseButton
        {
            get { return showCloseButton; }
            set { showCloseButton = value; }
        }
        public Rect LastRect { get { return lastRect; } }

        /// <summary>Raised when the selected physical record changes.</summary>
        public event Action<int> RecordSelected;

        /// <summary>Raised when the wrapped Record view commits a physical cell edit.</summary>
        public event Action<CsvRecordViewCellEdit> CellEditCommitted;

        /// <summary>Raised when the wrapped view needs its owner to repaint.</summary>
        public event Action RepaintRequested;

        /// <summary>Raised by the header close button.</summary>
        public event Action CloseRequested;

        /// <summary>
        /// Raised when the host asks the Grid to select a physical column. The
        /// inspector does not maintain a second grid selection state.
        /// </summary>
        public event Action<int, int> ColumnSelected;

        /// <summary>
        /// Binds the inspector to an existing controller and Record view definition.
        /// Rebinding preserves the wrapped view and therefore avoids creating a
        /// second document or record copy.
        /// </summary>
        public void Bind(CsvTableController table, CsvTableSchema tableSchema,
            CsvRecordViewDefinition viewDefinition = null)
        {
            if (table == null)
            {
                Unbind();
                return;
            }

            controller = table;
            if (recordView == null)
            {
                recordView = new CsvRecordView(table, tableSchema, viewDefinition);
                Subscribe();
            }
            else
            {
                recordView.Bind(table, tableSchema, viewDefinition);
            }
        }

        /// <summary>Unbinds the inspector without changing the CSV document.</summary>
        public void Unbind()
        {
            Unsubscribe();
            recordView = null;
            controller = null;
        }

        /// <summary>
        /// Selects a physical document record. Filtered/visual row indices are
        /// intentionally not accepted here.
        /// </summary>
        public void SelectRecord(int physicalRecordIndex)
        {
            if (recordView == null) return;
            recordView.SelectRecord(physicalRecordIndex);
        }

        /// <summary>
        /// Requests that the host select a physical Grid cell corresponding to a
        /// field in this inspector. The row defaults to the current record.
        /// </summary>
        public void SelectColumn(int physicalColumnIndex)
        {
            SelectColumn(SelectedRecordIndex, physicalColumnIndex);
        }

        /// <summary>Requests selection of an explicit physical record/column.</summary>
        public void SelectColumn(int physicalRecordIndex, int physicalColumnIndex)
        {
            if (physicalRecordIndex < 0 || physicalColumnIndex < 0) return;
            ColumnSelected?.Invoke(physicalRecordIndex, physicalColumnIndex);
        }

        /// <summary>Alias useful to keyboard-driven hosts and Go To Column popups.</summary>
        public void GoToColumn(int physicalColumnIndex)
        {
            SelectColumn(physicalColumnIndex);
        }

        /// <summary>
        /// Draws the panel into a caller-supplied rect. The header remains
        /// available even when no document is loaded, which makes the close
        /// affordance predictable while the host changes tables.
        /// </summary>
        public void Draw(Rect rect)
        {
            lastRect = rect;
            if (!isOpen) return;

            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            Rect header = new Rect(rect.x + 4f, rect.y + 2f,
                Mathf.Max(1f, rect.width - 8f), HeaderHeight);
            DrawHeader(header);

            Rect content = new Rect(rect.x + 4f, rect.y + HeaderHeight + 2f,
                Mathf.Max(1f, rect.width - 8f), Mathf.Max(1f, rect.height - HeaderHeight - 6f));
            if (recordView != null)
                recordView.Form.Draw(content);
            else
                GUI.Label(content, "No record selected.", EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawHeader(Rect rect)
        {
            float closeWidth = showCloseButton ? CloseButtonWidth : 0f;
            Rect titleRect = new Rect(rect.x, rect.y, Mathf.Max(1f, rect.width - closeWidth), rect.height);
            string title = controller == null ? "Record Inspector" : controller.Name + "  |  Record";
            if (SelectedRecordIndex >= 0) title += "  (row " + (SelectedRecordIndex + 1) + ")";
            GUI.Label(titleRect, title, EditorStyles.boldLabel);

            if (!showCloseButton) return;
            Rect closeRect = new Rect(rect.xMax - CloseButtonWidth, rect.y,
                CloseButtonWidth, rect.height);
            if (GUI.Button(closeRect, "x", EditorStyles.miniButton))
            {
                isOpen = false;
                CloseRequested?.Invoke();
            }
        }

        private void Subscribe()
        {
            if (recordView == null) return;
            recordView.RecordSelected += OnRecordSelected;
            recordView.CellEditCommitted += OnCellEditCommitted;
            recordView.RepaintRequested += OnRepaintRequested;
        }

        private void Unsubscribe()
        {
            if (recordView == null) return;
            recordView.RecordSelected -= OnRecordSelected;
            recordView.CellEditCommitted -= OnCellEditCommitted;
            recordView.RepaintRequested -= OnRepaintRequested;
        }

        private void OnRecordSelected(int physicalRecordIndex)
        {
            RecordSelected?.Invoke(physicalRecordIndex);
        }

        private void OnCellEditCommitted(CsvRecordViewCellEdit edit)
        {
            CellEditCommitted?.Invoke(edit);
        }

        private void OnRepaintRequested()
        {
            RepaintRequested?.Invoke();
        }
    }
}
