using System;
using System.Collections.Generic;
using CsvTool.Core;
using CsvTool.Schema;
using Neo.EditorUI;
using UnityEditor;
using UnityEngine;

namespace CsvTool.Editor
{
    /// <summary>
    /// A form/detail projection over one CsvTableController. It never owns a
    /// copy of a row: every edit goes through SetCell, preserving the table's
    /// shared undo history and save/recovery path.
    /// </summary>
    public sealed class CsvRecordView
    {
        private static int nextViewId;
        private const float DefaultListWidth = 220f;
        private const float ListRowHeight = 22f;
        private const float FieldRowHeight = 20f;
        private const float HeaderHeight = 48f;

        private CsvTableController controller;
        private CsvTableSchema schema;
        private CsvRecordViewDefinition definition;
        private IReadOnlyList<CsvRecordFieldGroup> groups = new CsvRecordFieldGroup[0];
        private readonly List<int> filteredRecords = new List<int>();
        private readonly Dictionary<string, bool> groupOpen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Vector2 recordScroll;
        private Vector2 detailScroll;
        private string recordSearch = string.Empty;
        private int selectedRecordIndex = -1;
        private string lastError = string.Empty;
        private readonly CsvRecordForm form;
        private string autocompleteText;
        private string autocompleteQuery;
        private int autocompleteRecord = -1;
        private int autocompleteColumn = -1;
        private int autocompleteSelection;
        private bool autocompleteVisible;
        private bool autocompleteSuppressed;
        private Rect autocompleteRect;
        private Vector2 autocompleteScreenAnchor;
        private readonly List<string> autocompleteSuggestions = new List<string>();
        private readonly Dictionary<string, string> editBuffers = new Dictionary<string, string>();
        private string activeControlName;
        private int activeRecord = -1;
        private int activeColumn = -1;
        private bool editFocusPending;
        private bool activeControlDrawnThisPass;
        private bool activeControlFocusedThisPass;
        private readonly int viewId;

        public Func<int, int, string, IReadOnlyList<string>> AutocompleteProvider { get; set; }
        public int AutocompleteMaxSuggestions = 8;
        public bool AutocompleteWhileTyping = true;

        public CsvRecordView(CsvTableController controller, CsvTableSchema schema = null,
            CsvRecordViewDefinition definition = null)
        {
            viewId = ++nextViewId;
            form = new CsvRecordForm(this);
            this.definition = definition;
            Bind(controller, schema, definition);
        }

        public CsvTableController Controller { get { return controller; } }
        public CsvTableSchema Schema { get { return schema; } }
        public CsvRecordViewDefinition Definition { get { return definition; } }
        public int SelectedRecordIndex { get { return selectedRecordIndex; } }
        public CsvRecord SelectedRecord
        {
            get
            {
                if (controller == null || controller.Document == null || selectedRecordIndex < 0 ||
                    selectedRecordIndex >= controller.Document.Records.Count) return null;
                return controller.Document.Records[selectedRecordIndex];
            }
        }
        public string RecordSearch { get { return recordSearch; } }
        public string LastError { get { return lastError; } }
        public IReadOnlyList<CsvRecordFieldGroup> Groups { get { return groups; } }
        public IReadOnlyList<int> FilteredRecordIndices { get { return filteredRecords; } }
        public CsvRecordForm Form { get { return form; } }
        public bool IsEditing { get { return activeRecord >= 0 && activeColumn >= 0; } }
        public bool IsAutocompleteVisible { get { return autocompleteVisible; } }
        public IReadOnlyList<string> AutocompleteSuggestions { get { return autocompleteSuggestions; } }

        /// <summary>Raised after a successful controller.SetCell call.</summary>
        public event Action<CsvRecordViewCellEdit> CellEditCommitted;

        /// <summary>Raised when the selected physical record changes.</summary>
        public event Action<int> RecordSelected;

        /// <summary>Useful for a host window that owns recovery journals and repainting.</summary>
        public event Action RepaintRequested;

        /// <summary>Requests capped suggestions using physical document coordinates.</summary>
        public IReadOnlyList<string> GetAutocompleteSuggestions(int physicalRecordIndex,
            int physicalColumnIndex, string editText)
        {
            List<string> result = new List<string>();
            if (AutocompleteProvider == null) return result;
            IReadOnlyList<string> provided = AutocompleteProvider(physicalRecordIndex,
                physicalColumnIndex, editText ?? string.Empty);
            int cap = Mathf.Clamp(AutocompleteMaxSuggestions, 1,
                NeoAutocompleteOverlay.MaxVisibleSuggestions);
            if (provided != null)
                for (int i = 0; i < provided.Count && result.Count < cap; i++)
                    if (provided[i] != null) result.Add(provided[i]);
            return result;
        }

        public void Bind(CsvTableController nextController, CsvTableSchema nextSchema = null,
            CsvRecordViewDefinition nextDefinition = null)
        {
            // Rebinding is used after edits and configuration changes. Never discard a live
            // delayed field value merely because the host is refreshing this projection.
            CommitEditing();
            controller = nextController;
            schema = nextSchema ?? (nextController == null ? null : nextController.Schema);
            if (nextDefinition != null || definition == null) definition = nextDefinition;
            RefreshFields();
            RebuildRecordList();
            if (!IsVisible(selectedRecordIndex)) selectedRecordIndex = filteredRecords.Count == 0 ? -1 : filteredRecords[0];
            lastError = string.Empty;
            ClearEditSession();
        }

        public void SetDefinition(CsvRecordViewDefinition nextDefinition)
        {
            CommitEditing();
            definition = nextDefinition;
            RefreshFields();
            RebuildRecordList();
        }

        public void RefreshFields()
        {
            groups = controller == null ? new CsvRecordFieldGroup[0] :
                CsvRecordFieldResolver.Resolve(controller.Headers, schema, definition);
        }

        public void SetRecordSearch(string search)
        {
            search = search ?? string.Empty;
            if (string.Equals(recordSearch, search, StringComparison.Ordinal)) return;
            CommitEditing();
            recordSearch = search;
            RebuildRecordList();
            if (!IsVisible(selectedRecordIndex)) selectedRecordIndex = filteredRecords.Count == 0 ? -1 : filteredRecords[0];
        }

        public bool SelectRecord(int physicalRecordIndex)
        {
            if (!IsVisible(physicalRecordIndex)) return false;
            if (selectedRecordIndex == physicalRecordIndex) return true;
            CommitEditing();
            selectedRecordIndex = physicalRecordIndex;
            lastError = string.Empty;
            if (RecordSelected != null) RecordSelected(physicalRecordIndex);
            if (RepaintRequested != null) RepaintRequested();
            return true;
        }

        /// <summary>
        /// Commits this view's one explicit edit session. Hosts call this before transferring
        /// selection to another surface, so a record popup can never outlive its owning field.
        /// </summary>
        public void CommitEditing()
        {
            if (activeRecord < 0 || activeColumn < 0 || controller == null)
            {
                ClearEditSession();
                return;
            }

            int record = activeRecord;
            int column = activeColumn;
            string value = autocompleteText ?? string.Empty;
            ClearEditSession();
            string current = controller.GetCell(record, column);
            if (!string.Equals(value, current, StringComparison.Ordinal)) TryCommitCell(record, column, value);
        }

        /// <summary>Cancels this view's active edit session without changing the CSV document.</summary>
        public void CancelEditing()
        {
            ClearEditSession();
        }

        public bool SelectFilteredRecord(int listIndex)
        {
            if (listIndex < 0 || listIndex >= filteredRecords.Count) return false;
            return SelectRecord(filteredRecords[listIndex]);
        }

        public bool SelectPreviousVisible()
        {
            int index = IndexInFiltered(selectedRecordIndex);
            if (index < 0) return filteredRecords.Count > 0 && SelectRecord(filteredRecords[filteredRecords.Count - 1]);
            return SelectRecord(filteredRecords[Mathf.Max(0, index - 1)]);
        }

        public bool SelectNextVisible()
        {
            int index = IndexInFiltered(selectedRecordIndex);
            if (index < 0) return filteredRecords.Count > 0 && SelectRecord(filteredRecords[0]);
            return SelectRecord(filteredRecords[Mathf.Min(filteredRecords.Count - 1, index + 1)]);
        }

        /// <summary>
        /// Attempts an exact string edit. This public method is also the
        /// simplest integration point for a host window or command palette.
        /// </summary>
        public bool TryCommitCell(int physicalRecordIndex, int columnIndex, string value)
        {
            if (controller == null) return false;
            string oldValue = controller.GetCell(physicalRecordIndex, columnIndex);
            try
            {
                if (!controller.SetCell(physicalRecordIndex, columnIndex, value)) return false;
                lastError = string.Empty;
                if (CellEditCommitted != null)
                    CellEditCommitted(new CsvRecordViewCellEdit(physicalRecordIndex, columnIndex, oldValue, value ?? string.Empty));
                if (RepaintRequested != null) RepaintRequested();
                return true;
            }
            catch (Exception exception)
            {
                lastError = exception.Message;
                if (RepaintRequested != null) RepaintRequested();
                return false;
            }
        }

        /// <summary>Draws a two-pane record browser and grouped form into a rect.</summary>
        public void Draw(Rect rect)
        {
            if (controller == null)
            {
                GUI.Label(new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 22f),
                    "No CSV table selected.", NeoStyles.HeaderSubtitle);
                return;
            }
            if (!controller.IsLoaded)
            {
                GUI.Label(new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 22f),
                    "Open the table before showing its records.", NeoStyles.HeaderSubtitle);
                return;
            }
            if (!IsVisible(selectedRecordIndex))
            {
                RebuildRecordList();
                selectedRecordIndex = filteredRecords.Count == 0 ? -1 : filteredRecords[0];
            }
            float listWidth = Mathf.Clamp(DefaultListWidth, 160f, Mathf.Max(160f, rect.width * 0.38f));
            Rect listRect = new Rect(rect.x, rect.y, listWidth, rect.height);
            Rect detailRect = new Rect(listRect.xMax + 6f, rect.y, Mathf.Max(80f, rect.width - listWidth - 6f), rect.height);
            DrawRecordList(listRect);
            DrawDetails(detailRect);
        }

        private void DrawRecordList(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            Rect titleRect = new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, 18f);
            GUI.Label(titleRect, "Records  " + filteredRecords.Count, NeoStyles.SectionTitle);
            Rect searchRect = new Rect(rect.x + 6f, titleRect.yMax + 3f, rect.width - 12f, 20f);
            GUI.SetNextControlName("CsvRecordSearch");
            string nextSearch = EditorGUI.TextField(searchRect, recordSearch, EditorStyles.toolbarSearchField);
            if (!string.Equals(nextSearch, recordSearch, StringComparison.Ordinal)) SetRecordSearch(nextSearch);

            Rect scrollRect = new Rect(rect.x + 4f, searchRect.yMax + 4f, rect.width - 8f,
                Mathf.Max(1f, rect.height - searchRect.yMax + rect.y - 8f));
            float contentHeight = Mathf.Max(scrollRect.height, filteredRecords.Count * ListRowHeight);
            recordScroll = GUI.BeginScrollView(scrollRect, recordScroll,
                new Rect(0f, 0f, scrollRect.width - 14f, contentHeight));
            for (int i = 0; i < filteredRecords.Count; i++)
            {
                int physicalIndex = filteredRecords[i];
                Rect row = new Rect(0f, i * ListRowHeight, scrollRect.width - 14f, ListRowHeight);
                bool active = physicalIndex == selectedRecordIndex;
                if (Event.current.type == EventType.Repaint && active)
                    EditorGUI.DrawRect(row, NeoColors.RowSelected);
                string label = RecordLabel(physicalIndex);
                GUI.Label(new Rect(row.x + 6f, row.y + 1f, row.width - 12f, row.height - 2f),
                    label, active ? EditorStyles.boldLabel : EditorStyles.label);
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && row.Contains(Event.current.mousePosition))
                {
                    SelectRecord(physicalIndex);
                    Event.current.Use();
                }
            }
            GUI.EndScrollView();
        }

private void DrawDetails(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            CsvRecord record = SelectedRecord;
            if (record == null)
            {
                GUI.Label(new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, 22f),
                    filteredRecords.Count == 0 ? "No visible records." : "Select a record.", NeoStyles.HeaderSubtitle);
                return;
            }
            string label = RecordLabel(record.Index);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 6f, rect.width - 190f, 20f), label, NeoStyles.HeaderTitle);
            GUI.Label(new Rect(rect.x + 10f, rect.y + 26f, rect.width - 190f, 16f),
                "Physical row " + (record.Index + 1) + "  ·  " + record.Kind, NeoStyles.MiniDim);
            Rect previousRect = new Rect(rect.xMax - 176f, rect.y + 8f, 76f, 22f);
            Rect nextRect = new Rect(rect.xMax - 94f, rect.y + 8f, 76f, 22f);
            if (GUI.Button(previousRect, "‹ Previous", EditorStyles.miniButtonLeft)) SelectPreviousVisible();
            if (GUI.Button(nextRect, "Next ›", EditorStyles.miniButtonRight)) SelectNextVisible();

            Rect formRect = new Rect(rect.x + 5f, rect.y + HeaderHeight,
                rect.width - 10f, Mathf.Max(1f, rect.height - HeaderHeight - 5f));
            form.Draw(formRect);
        }

/// <summary>Draws only the selected record's fields, with no record-list navigation chrome.</summary>
        internal void DrawForm(Rect rect)
        {
            CsvRecord record = SelectedRecord;
            if (record == null)
            {
                GUI.Label(new Rect(rect.x + 5f, rect.y + 5f, rect.width - 10f, 22f),
                    filteredRecords.Count == 0 ? "No visible records." : "Select a record.", NeoStyles.HeaderSubtitle);
                return;
            }

            float errorHeight = string.IsNullOrEmpty(lastError) ? 0f : 30f;
            if (errorHeight > 0f)
                GUI.Label(new Rect(rect.x + 5f, rect.y, rect.width - 10f, 28f), lastError, EditorStyles.helpBox);
            Rect body = new Rect(rect.x, rect.y + errorHeight, rect.width,
                Mathf.Max(1f, rect.height - errorHeight));
            activeControlDrawnThisPass = false;
            activeControlFocusedThisPass = false;
            // Autocomplete must see pointer events before the focused TextField or an overlapped
            // field consumes them.
            if (autocompleteVisible)
            {
                // Use the rectangle cached by the repaint which actually drew the popup. Calling
                // ScreenToGUIPoint here, before entering the scroll view, produces a different
                // clip-space origin in docked/floating editor layouts and makes clicks fall through.
                HandleAutocompleteMouse(Event.current);
            }
            float contentHeight = EstimateContentHeight();
            detailScroll = GUI.BeginScrollView(body, detailScroll,
                new Rect(0f, 0f, body.width - 14f, Mathf.Max(body.height, contentHeight)));
            float y = 0f;
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                CsvRecordFieldGroup group = groups[groupIndex];
                if (group == null) continue;
                bool open;
                if (!groupOpen.TryGetValue(group.Name, out open)) open = true;
                Rect groupRect = new Rect(0f, y, body.width - 14f, 22f);
                bool nextOpen = EditorGUI.Foldout(groupRect, open, group.Name, true, NeoStyles.SectionTitle);
                groupOpen[group.Name] = nextOpen;
                y += 24f;
                if (!nextOpen) continue;
                for (int fieldIndex = 0; fieldIndex < group.Fields.Count; fieldIndex++)
                {
                    CsvRecordField field = group.Fields[fieldIndex];
                    if (field == null || field.Hidden) continue;
                    DrawField(new Rect(0f, y, body.width - 14f, FieldRowHeight), field);
                    y += FieldRowHeight + (string.IsNullOrEmpty(field.HelpText) ? 2f : 18f);
                }
                y += 4f;
            }
            GUI.EndScrollView();
            if (autocompleteVisible && Event.current.type == EventType.Repaint)
                PositionAutocomplete(body);
            DrawAutocompletePopup();
            CommitIfFocusMoved();
        }


        private void DrawField(Rect row, CsvRecordField field)
        {
            const float labelWidth = 154f;
            Rect labelRect = new Rect(row.x + 4f, row.y, Mathf.Min(labelWidth, row.width * 0.43f), row.height);
            string label = field.DisplayName + (field.Required ? " *" : string.Empty);
            GUI.Label(labelRect, new GUIContent(label, field.HelpText), EditorStyles.label);
            Rect valueRect = new Rect(labelRect.xMax + 4f, row.y, Mathf.Max(30f, row.width - labelRect.xMax - 8f), row.height);
            string current = controller.GetCell(selectedRecordIndex, field.ColumnIndex);
            bool valid;
            string validationError;
            valid = CsvRecordValueParser.IsValid(field, current, out validationError);
            bool enabled = controller.IsRecordVisible(selectedRecordIndex);
            EditorGUI.BeginDisabledGroup(!enabled);
            string next = current;
            bool usePopup = field.ValueKind == CsvValueKind.Enum && field.EnumValues != null && field.EnumValues.Count > 0 &&
                (string.IsNullOrEmpty(current) || CsvRecordValueParser.IsValidEnum(current, field.EnumValues, false));
            if (field.ValueKind == CsvValueKind.Boolean && CsvRecordValueParser.TryParseBoolean(current, out bool boolValue))
            {
                bool nextBool = EditorGUI.Toggle(valueRect, boolValue);
                if (nextBool != boolValue) next = FormatBoolean(current, nextBool);
            }
            else if (usePopup)
            {
                int selected = -1;
                for (int i = 0; i < field.EnumValues.Count; i++)
                    if (string.Equals(field.EnumValues[i], current, StringComparison.Ordinal)) { selected = i; break; }
                string[] options = new string[field.EnumValues.Count + 1];
                options[0] = "(empty)";
                for (int i = 0; i < field.EnumValues.Count; i++) options[i + 1] = field.EnumValues[i];
                int popup = EditorGUI.Popup(valueRect, selected + 1, options);
                if (popup != selected + 1) next = popup == 0 ? string.Empty : options[popup];
            }
            else
            {
                string controlName = "CsvRecordField_" + viewId + "_" + selectedRecordIndex + "_" + field.ColumnIndex;
                string bufferKey = selectedRecordIndex + ":" + field.ColumnIndex;
                string buffered;
                if (!editBuffers.TryGetValue(bufferKey, out buffered)) buffered = current;
                GUI.SetNextControlName(controlName);
                if (activeRecord == selectedRecordIndex && activeColumn == field.ColumnIndex)
                    HandleAutocompleteKeyboard(Event.current, controlName);
                next = EditorGUI.TextField(valueRect, buffered);
                bool focused = string.Equals(GUI.GetNameOfFocusedControl(), controlName, StringComparison.Ordinal);
                bool changed = !string.Equals(next, buffered, StringComparison.Ordinal);
                if (focused && (activeRecord != selectedRecordIndex || activeColumn != field.ColumnIndex))
                    BeginEditSession(controlName, selectedRecordIndex, field.ColumnIndex, buffered);
                if (changed)
                {
                    if (activeRecord != selectedRecordIndex || activeColumn != field.ColumnIndex)
                        BeginEditSession(controlName, selectedRecordIndex, field.ColumnIndex, buffered);
                    editBuffers[bufferKey] = next;
                    autocompleteText = next;
                    autocompleteRecord = selectedRecordIndex;
                    autocompleteColumn = field.ColumnIndex;
                    activeControlName = controlName;
                    autocompleteSuppressed = string.IsNullOrEmpty(next);
                    if (autocompleteSuppressed) ClearAutocomplete();
                    else
                    {
                        autocompleteSuppressed = false;
                        if (AutocompleteWhileTyping) RefreshAutocomplete(false);
                    }
                }
                if (focused && AutocompleteWhileTyping && !autocompleteVisible && !autocompleteSuppressed)
                    RefreshAutocomplete(false);
                if (activeRecord == selectedRecordIndex && activeColumn == field.ColumnIndex)
                {
                    activeControlDrawnThisPass = true;
                    activeControlFocusedThisPass = focused;
                    // Convert while inside the scroll-view clip. This avoids mixing content and
                    // window coordinates, which otherwise offsets clicks after vertical scrolling.
                    autocompleteScreenAnchor = GUIUtility.GUIToScreenPoint(
                        new Vector2(valueRect.x, valueRect.yMax));
                    autocompleteRect = new Rect(autocompleteRect.x, autocompleteRect.y,
                        NeoAutocompleteOverlay.DefaultWidth,
                        FieldRowHeight * Mathf.Min(8, autocompleteSuggestions.Count));
                }
                if (editFocusPending && string.Equals(activeControlName, controlName, StringComparison.Ordinal) &&
                    Event.current.type == EventType.Repaint)
                {
                    EditorGUI.FocusTextInControl(controlName);
                    editFocusPending = false;
                    activeControlFocusedThisPass = true;
                }
            }
            EditorGUI.EndDisabledGroup();
            bool commitFieldValue = usePopup || field.ValueKind == CsvValueKind.Boolean;
            if (commitFieldValue && !string.Equals(next, current, StringComparison.Ordinal))
            {
                string editError;
                if (CsvRecordValueParser.IsValid(field, next, out editError)) TryCommitCell(selectedRecordIndex, field.ColumnIndex, next);
                else lastError = editError;
            }
            if (!valid)
                GUI.Label(new Rect(valueRect.x, valueRect.yMax + 1f, valueRect.width, 16f), validationError, NeoStyles.MiniDim);
            if (!string.IsNullOrEmpty(field.HelpText))
                GUI.Label(new Rect(valueRect.x, valueRect.yMax + 1f, valueRect.width, 16f), field.HelpText, NeoStyles.MiniDim);
        }

        private void HandleAutocompleteKeyboard(Event currentEvent, string controlName)
        {
            if (currentEvent == null || currentEvent.type != EventType.KeyDown ||
                activeRecord < 0 || activeColumn < 0) return;
            if (!editFocusPending && !string.Equals(GUI.GetNameOfFocusedControl(),
                controlName, StringComparison.Ordinal))
            {
                CommitEditing();
                return;
            }
            if ((currentEvent.control || currentEvent.command) && currentEvent.keyCode == KeyCode.Space)
            {
                RefreshAutocomplete(true); currentEvent.Use();
            }
            else if (autocompleteVisible && (currentEvent.keyCode == KeyCode.UpArrow || currentEvent.keyCode == KeyCode.DownArrow))
            {
                autocompleteSelection = Mathf.Clamp(autocompleteSelection +
                    (currentEvent.keyCode == KeyCode.DownArrow ? 1 : -1), 0, autocompleteSuggestions.Count - 1);
                currentEvent.Use();
            }
            else if (currentEvent.keyCode == KeyCode.Escape)
            {
                if (autocompleteVisible) { ClearAutocomplete(); autocompleteSuppressed = true; }
                else
                {
                    CancelEditing();
                    GUIUtility.keyboardControl = 0;
                }
                currentEvent.Use();
            }
            else if (currentEvent.keyCode == KeyCode.Return || currentEvent.keyCode == KeyCode.KeypadEnter || currentEvent.keyCode == KeyCode.Tab)
            {
                if (!AcceptAutocomplete())
                {
                    CommitEditing();
                    GUIUtility.keyboardControl = 0;
                }
                currentEvent.Use();
            }
        }

        private void RefreshAutocomplete(bool force)
        {
            if (AutocompleteProvider == null || activeRecord < 0 || activeColumn < 0 ||
                autocompleteRecord != activeRecord || autocompleteColumn != activeColumn)
            {
                ClearAutocomplete();
                return;
            }
            string query = autocompleteText ?? string.Empty;
            if (!force && autocompleteRecord == activeRecord && autocompleteColumn == activeColumn &&
                string.Equals(autocompleteQuery, query, StringComparison.Ordinal)) return;
            IReadOnlyList<string> provided = GetAutocompleteSuggestions(autocompleteRecord,
                autocompleteColumn, query);
            autocompleteSuggestions.Clear();
            if (provided != null)
                for (int i = 0; i < provided.Count; i++)
                    if (provided[i] != null) autocompleteSuggestions.Add(provided[i]);
            autocompleteQuery = query;
            autocompleteSelection = 0;
            autocompleteVisible = autocompleteSuggestions.Count > 0;
        }

        private bool AcceptAutocomplete()
        {
            if (!autocompleteVisible || autocompleteSuggestions.Count == 0) return false;
            autocompleteText = autocompleteSuggestions[Mathf.Clamp(autocompleteSelection, 0, autocompleteSuggestions.Count - 1)];
            editBuffers[autocompleteRecord + ":" + autocompleteColumn] = autocompleteText;
            autocompleteSuppressed = true;
            ClearAutocomplete();
            // A focused IMGUI TextField renders its TextEditor cache. Briefly release it so the
            // accepted value is adopted, then restore focus during the next repaint.
            GUIUtility.keyboardControl = 0;
            editFocusPending = true;
            GUI.changed = true;
            RepaintRequested?.Invoke();
            return true;
        }

        private void HandleAutocompleteMouse(Event currentEvent)
        {
            if (!autocompleteVisible) return;
            NeoAutocompleteOverlay.HandleInput(currentEvent, autocompleteRect,
                autocompleteSuggestions.Count, FieldRowHeight, ref autocompleteSelection,
                AcceptAutocompleteFromMouse, RepaintRequested);
        }

        private void DrawAutocompletePopup()
        {
            if (Event.current.type != EventType.Repaint || !autocompleteVisible || autocompleteSuggestions.Count == 0) return;
            NeoAutocompleteOverlay.Draw(autocompleteRect, autocompleteSuggestions,
                autocompleteSelection, FieldRowHeight);
        }

        private void PositionAutocomplete(Rect bounds)
        {
            Vector2 anchor = GUIUtility.ScreenToGUIPoint(autocompleteScreenAnchor);
            float width = Mathf.Min(NeoAutocompleteOverlay.DefaultWidth, bounds.width);
            float height = Mathf.Min(NeoAutocompleteOverlay.MaxVisibleSuggestions,
                autocompleteSuggestions.Count) * FieldRowHeight;
            float x = Mathf.Clamp(anchor.x, bounds.x, Mathf.Max(bounds.x, bounds.xMax - width));
            float y = anchor.y;
            if (y + height > bounds.yMax)
                y = Mathf.Max(bounds.y, anchor.y - FieldRowHeight - height);
            autocompleteRect = new Rect(x, y, width, height);
        }

        private void ClearAutocomplete()
        {
            autocompleteVisible = false;
            autocompleteQuery = null;
            autocompleteSelection = 0;
            autocompleteSuggestions.Clear();
        }

        private void AcceptAutocompleteFromMouse()
        {
            AcceptAutocomplete();
        }

        private void BeginEditSession(string controlName, int record, int column, string value)
        {
            if (activeRecord >= 0 && (activeRecord != record || activeColumn != column))
                CommitEditing();
            activeControlName = controlName;
            activeRecord = record;
            activeColumn = column;
            autocompleteText = value ?? string.Empty;
            autocompleteRecord = record;
            autocompleteColumn = column;
            // An empty value is still a valid autocomplete query: focusing an empty field
            // should offer the column's available values. Suppression is reserved for an
            // explicit dismissal (Escape) or an accepted suggestion, and is reset when a
            // fresh field-edit session begins.
            autocompleteSuppressed = false;
        }

        private void CommitIfFocusMoved()
        {
            if (!IsEditing || editFocusPending || Event.current.type != EventType.Repaint) return;
            if (!activeControlDrawnThisPass || !activeControlFocusedThisPass)
                CommitEditing();
        }

        private void ClearEditSession()
        {
            if (activeRecord >= 0 && activeColumn >= 0)
                editBuffers.Remove(activeRecord + ":" + activeColumn);
            activeControlName = null;
            activeRecord = -1;
            activeColumn = -1;
            autocompleteText = null;
            autocompleteRecord = -1;
            autocompleteColumn = -1;
            autocompleteSuppressed = false;
            editFocusPending = false;
            ClearAutocomplete();
        }

        private float EstimateContentHeight()
        {
            float height = 0f;
            for (int i = 0; i < groups.Count; i++)
            {
                CsvRecordFieldGroup group = groups[i];
                if (group == null) continue;
                bool open;
                if (!groupOpen.TryGetValue(group.Name, out open)) open = true;
                height += 24f;
                if (!open) continue;
                for (int j = 0; j < group.Fields.Count; j++)
                {
                    CsvRecordField field = group.Fields[j];
                    if (field != null && !field.Hidden) height += FieldRowHeight + (string.IsNullOrEmpty(field.HelpText) ? 2f : 18f);
                }
                height += 4f;
            }
            return Mathf.Max(60f, height);
        }

        private void RebuildRecordList()
        {
            filteredRecords.Clear();
            if (controller == null || !controller.IsLoaded) return;
            IReadOnlyList<int> visible = controller.VisibleRecordIndices;
            for (int i = 0; i < visible.Count; i++)
            {
                int physical = visible[i];
                string label = RecordLabel(physical);
                if (string.IsNullOrEmpty(recordSearch) || label.IndexOf(recordSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    RecordContains(physical, recordSearch)) filteredRecords.Add(physical);
            }
        }

        private bool RecordContains(int physicalIndex, string query)
        {
            if (string.IsNullOrEmpty(query) || controller == null || controller.Document == null) return false;
            CsvRecord record = controller.Document.Records[physicalIndex];
            for (int i = 0; i < record.CellCount; i++)
                if (record.GetValue(i).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private string RecordLabel(int physicalIndex)
        {
            if (controller == null || controller.Document == null || physicalIndex < 0 ||
                physicalIndex >= controller.Document.Records.Count) return "Row " + (physicalIndex + 1);
            CsvRecord record = controller.Document.Records[physicalIndex];
            int displayIndex = ResolveTableColumn(schema == null ? string.Empty : schema.DisplayColumn,
                schema == null ? -1 : schema.DisplayColumnIndex);
            if (displayIndex < 0) displayIndex = ResolveTableColumn(schema == null ? string.Empty : schema.IdentityColumn,
                schema == null ? -1 : schema.IdentityColumnIndex);
            string value = record.GetValue(displayIndex);
            if (string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < record.CellCount; i++)
                    if (!string.IsNullOrEmpty(record.GetValue(i))) { value = record.GetValue(i); break; }
            }
            return string.IsNullOrEmpty(value) ? "Row " + (physicalIndex + 1) : value;
        }

        private int ResolveTableColumn(string name, int index)
        {
            if (controller == null) return -1;
            if (index >= 0) return index < controller.Headers.Count ? index : -1;
            int physicalColumn;
            return controller.TryResolvePhysicalColumn(name, out physicalColumn) ? physicalColumn : -1;
        }

        private CsvRecordField FindField(int columnIndex)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                CsvRecordFieldGroup group = groups[i];
                if (group == null) continue;
                for (int j = 0; j < group.Fields.Count; j++)
                    if (group.Fields[j] != null && group.Fields[j].ColumnIndex == columnIndex) return group.Fields[j];
            }
            return null;
        }

        private bool IsVisible(int physicalIndex)
        {
            if (physicalIndex < 0) return false;
            return IndexInFiltered(physicalIndex) >= 0;
        }

        private int IndexInFiltered(int physicalIndex)
        {
            for (int i = 0; i < filteredRecords.Count; i++)
                if (filteredRecords[i] == physicalIndex) return i;
            return -1;
        }

        private static string FormatBoolean(string oldValue, bool value)
        {
            if (string.Equals(oldValue, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(oldValue, "0", StringComparison.OrdinalIgnoreCase)) return value ? "1" : "0";
            if (string.Equals(oldValue, "yes", StringComparison.OrdinalIgnoreCase) || string.Equals(oldValue, "no", StringComparison.OrdinalIgnoreCase)) return value ? "yes" : "no";
            return value ? "true" : "false";
        }
    }

    public struct CsvRecordViewCellEdit
    {
        public CsvRecordViewCellEdit(int recordIndex, int columnIndex, string oldValue, string newValue)
        {
            RecordIndex = recordIndex;
            ColumnIndex = columnIndex;
            OldValue = oldValue ?? string.Empty;
            NewValue = newValue ?? string.Empty;
        }

        public readonly int RecordIndex;
        public readonly int ColumnIndex;
        public readonly string OldValue;
        public readonly string NewValue;
    }
}
