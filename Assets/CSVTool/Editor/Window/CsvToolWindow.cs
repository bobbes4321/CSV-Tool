using System;
using System.Collections.Generic;
using System.IO;
using CsvTool.Core;
using CsvTool.Editor.Configuration;
using CsvTool.Editor.Index;
using CsvTool.Editor.Recovery;
using CsvTool.Editor.Search;
using CsvTool.Schema;
using Neo.EditorUI;
using UnityEditor;
using UnityEngine;

namespace CsvTool.Editor
{
    /// <summary>
    /// Fast shell for the CSV editor. File/search state remains separate from
    /// the virtualized grid so record and form views can share the controller.
    /// </summary>
    public sealed class CsvToolWindow : EditorWindow
    {
        private const string TablePref = "CsvTool.Editor.SelectedTable";
        private const string FrozenColumnsPref = "CsvTool.Editor.FrozenColumns";
        private const string WorkspaceAssetPref = "CsvTool.Editor.WorkspaceAsset";
        private const string ViewModePref = "CsvTool.Editor.ViewMode";
        private const string QuickInspectorPref = "CsvTool.Editor.QuickInspector";
        private const string QuickInspectorWidthPref = "CsvTool.Editor.QuickInspectorWidth";
        private const float SidebarWidth = 190f;
        private const float MinimumGridWidth = 120f;
        private const float MinimumInspectorWidth = 210f;
        private const float InspectorSplitterWidth = 6f;
        private const float DefaultInspectorWidth = 340f;

        private CsvWorkspaceController workspace;
        private CsvTableController current;
        private CsvGrid grid;
        private CsvRecordView recordView;
        private CsvQuickRecordInspector quickInspector;
        [SerializeField] private CsvWorkspaceAsset workspaceAsset;
        private bool recordMode;
        private bool showQuickInspector;
        private bool syncingSelection;
        private readonly Dictionary<CsvTableController, CsvValueIndex> valueIndexes =
            new Dictionary<CsvTableController, CsvValueIndex>();
        private string search = string.Empty;
        private Vector2 sidebarScroll;
        private Vector2 changesScroll;
        private Vector2 issuesScroll;
        private double nextExternalPoll;
        private bool showChanges;
        private PendingCellNavigation pendingNavigation;
        private readonly HashSet<string> recoveryCheckedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string recoveryWarning = string.Empty;

        private enum NavigationSource
        {
            Column,
            Find,
            Change,
            Inspector,
            Reference
        }

        private sealed class PendingCellNavigation
        {
            public CsvTableController Table;
            public int RecordIndex;
            public int ColumnIndex;
            public CsvGridRevealMode Vertical;
            public CsvGridRevealMode Horizontal;
            public float? RowScreenOffset;
            public NavigationSource Source;
        }

        [MenuItem("Window/CSV Tool", priority = 2200)]
        public static void Open()
        {
            CsvToolWindow window = GetWindow<CsvToolWindow>();
            window.titleContent = new GUIContent("CSV Tool");
            window.minSize = new Vector2(560f, 300f);
            window.Show();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            grid = new CsvGrid();
            grid.FrozenColumnCount = EditorPrefs.GetInt(FrozenColumnsPref, 1);
            grid.CellEditCommitted += OnCellEditCommitted;
            grid.BatchEditCommitted += OnBatchEditCommitted;
            grid.PastePreflightRejected += OnPastePreflightRejected;
            grid.UndoRequested += UndoCurrent;
            grid.RedoRequested += RedoCurrent;
            grid.SaveRequested += SaveCurrentFromShortcut;
            grid.RepaintRequested += Repaint;
            grid.SelectionChanged += OnGridSelectionChanged;
            grid.ReferenceNavigationRequested += OnReferenceNavigationRequested;
            grid.StructureMenuRequested += OnStructureMenuRequested;
            grid.AutocompleteProvider = GetAutocompleteSuggestions;
            grid.PasteCellErrorProvider = GetPasteCellError;
            grid.Settings.AutocompleteWhileTyping = true;
            recordMode = false;
            showQuickInspector = EditorPrefs.GetBool(QuickInspectorPref, false);
            if (workspaceAsset == null) workspaceAsset = LoadRememberedWorkspaceAsset();
            if (workspaceAsset != null)
            {
                ApplyWorkspaceAsset(workspaceAsset);
                EditorApplication.update += PollExternalState;
                return;
            }
            EditorApplication.update += PollExternalState;
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollExternalState;
            // Domain reloads also disable the window. Commit whichever surface owns the live
            // field while callbacks are still connected so recovery can capture it.
            CommitActiveEditing();
            if (grid != null)
            {
                grid.CellEditCommitted -= OnCellEditCommitted;
                grid.BatchEditCommitted -= OnBatchEditCommitted;
                grid.PastePreflightRejected -= OnPastePreflightRejected;
                grid.UndoRequested -= UndoCurrent;
                grid.RedoRequested -= RedoCurrent;
                grid.SaveRequested -= SaveCurrentFromShortcut;
                grid.RepaintRequested -= Repaint;
                grid.SelectionChanged -= OnGridSelectionChanged;
                grid.ReferenceNavigationRequested -= OnReferenceNavigationRequested;
                grid.StructureMenuRequested -= OnStructureMenuRequested;
                grid.AutocompleteProvider = null;
                grid.PasteCellErrorProvider = null;
            }
            UnbindRecordView();
            UnbindQuickInspector();
        }

        private void OnLostFocus()
        {
            CommitActiveEditing();
            UpdateUnsavedState();
        }

        private void OnGUI()
        {
            HandleWindowShortcuts();
            NeoGUI.ComponentHeader("CSV Tool", workspace == null || string.IsNullOrEmpty(workspace.Folder)
                ? "Assign a CSV Workspace asset" : workspace.Folder, NeoColors.Data);
            DrawToolbar();
            NeoGUI.Splitter();

            Rect content = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (workspace == null || string.IsNullOrEmpty(workspace.Folder))
            {
                DrawEmptyState(content);
                UpdateUnsavedState();
                DrawStatusBar();
                return;
            }

            Rect sidebar = new Rect(content.x, content.y, SidebarWidth, content.height);
            Rect main = new Rect(content.x + SidebarWidth + 6f, content.y, content.width - SidebarWidth - 6f, content.height);
            DrawSidebar(sidebar);
            DrawMain(main);
            UpdateUnsavedState();
            DrawStatusBar();
        }

        private void HandleWindowShortcuts()
        {
            Event currentEvent = Event.current;
            if (currentEvent == null || currentEvent.type != EventType.KeyDown ||
                (!currentEvent.control && !currentEvent.command)) return;

            if (currentEvent.keyCode == KeyCode.F)
            {
                if (OpenContextualFind()) currentEvent.Use();
            }
            else if (currentEvent.keyCode == KeyCode.G)
            {
                if (OpenGoToColumn()) currentEvent.Use();
            }
        }

        private bool OpenContextualFind()
        {
            if (!EnsureCurrentLoaded()) return false;
            CommitActiveEditing();
            int selectedRecord = GetNavigationRecordIndex();
            Rect anchor = GetNavigationPopupAnchor(440f);
            CsvContextualFindPopup.Show(anchor,
                query => CsvContextualCellFinder.Search(current, selectedRecord, query),
                result => NavigateToPhysicalCell(result.PhysicalRecordIndex,
                    result.PhysicalColumnIndex, NavigationSource.Find));
            return true;
        }

        private bool OpenGoToColumn()
        {
            if (!EnsureCurrentLoaded()) return false;
            CommitActiveEditing();
            int selectedRecord = GetNavigationRecordIndex();
            int selectedColumn = grid != null && grid.Selection.IsValid ? grid.Selection.Column : 0;
            CsvGoToColumnModel model = CsvGoToColumnModel.ForTable(current, selectedRecord,
                BuildRecordDefinition(), selectedColumn);
            CsvGoToColumnPopup.Show(GetNavigationPopupAnchor(380f), model,
                column => NavigateToPhysicalCell(selectedRecord, column, NavigationSource.Column));
            return true;
        }

        private bool EnsureCurrentLoaded()
        {
            if (current == null) return false;
            if (current.IsLoaded) return true;
            try
            {
                current.Open();
                TryRecoverCurrent();
                return true;
            }
            catch (Exception exception)
            {
                ShowNotification(new GUIContent("Unable to open CSV: " + exception.Message));
                return false;
            }
        }

        private int GetNavigationRecordIndex()
        {
            CsvGridSelection selection = grid == null ? CsvGridSelection.Invalid : grid.Selection;
            if (selection.IsValid) return selection.RecordIndex;
            if (recordView != null && recordView.SelectedRecordIndex >= 0)
                return recordView.SelectedRecordIndex;
            if (quickInspector != null && quickInspector.SelectedRecordIndex >= 0)
                return quickInspector.SelectedRecordIndex;
            if (grid != null && grid.TopVisiblePhysicalRecordIndex >= 0)
                return grid.TopVisiblePhysicalRecordIndex;
            return current != null && current.VisibleRecordIndices.Count > 0
                ? current.VisibleRecordIndices[0] : -1;
        }

        private Rect GetNavigationPopupAnchor(float width)
        {
            float actualWidth = Mathf.Min(width, Mathf.Max(300f, position.width - 24f));
            return new Rect(Mathf.Max(8f, (position.width - actualWidth) * 0.5f), 48f,
                actualWidth, 20f);
        }

        private void NavigateToPhysicalCell(int physicalRecordIndex, int physicalColumnIndex,
            NavigationSource source)
        {
            if (current == null || physicalRecordIndex < 0 || physicalColumnIndex < 0) return;

            bool hasLiveGridViewport = !recordMode && grid != null;
            bool sameRecord = hasLiveGridViewport && grid.Selection.IsValid &&
                grid.Selection.RecordIndex == physicalRecordIndex;
            float screenOffset;
            float? capturedOffset = hasLiveGridViewport && grid.TryGetRecordScreenOffset(
                physicalRecordIndex, out screenOffset) ? (float?)screenOffset : null;
            bool wasVisible = hasLiveGridViewport && grid.IsPhysicalRecordFullyVisible(physicalRecordIndex);
            string visibilityMessage = string.Empty;
            if (!current.IsRecordVisible(physicalRecordIndex) && !string.IsNullOrEmpty(search))
            {
                search = string.Empty;
                current.SetSearchQuery(string.Empty);
                if (grid != null) grid.InvalidateVisibleRecordMap();
                RefreshRecordView();
                visibilityMessage = "Filter cleared to reveal row " + (physicalRecordIndex + 1) + ".";
            }
            if (!current.IsRecordVisible(physicalRecordIndex) && !current.IncludeNonDataRows)
            {
                current.IncludeNonDataRows = true;
                if (grid != null) grid.InvalidateVisibleRecordMap();
                RefreshRecordView();
                visibilityMessage = string.IsNullOrEmpty(visibilityMessage)
                    ? "All rows enabled to reveal row " + (physicalRecordIndex + 1) + "."
                    : visibilityMessage + " All rows was also enabled.";
            }

            CsvGridRevealMode vertical;
            CsvGridRevealMode horizontal;
            if ((source == NavigationSource.Column || source == NavigationSource.Inspector || sameRecord) &&
                capturedOffset.HasValue)
            {
                vertical = CsvGridRevealMode.Preserve;
                // A column chosen explicitly from Ctrl/Cmd+G should be comfortably visible.
                // Minimal reveal can leave a distant column flush against the clipped pane edge,
                // which makes it look missing when a frozen column is present.
                horizontal = source == NavigationSource.Column || source == NavigationSource.Change
                    ? CsvGridRevealMode.Center : CsvGridRevealMode.Minimal;
            }
            else if (source == NavigationSource.Reference)
            {
                vertical = CsvGridRevealMode.Center;
                horizontal = CsvGridRevealMode.Center;
            }
            else
            {
                vertical = wasVisible ? CsvGridRevealMode.Minimal : CsvGridRevealMode.Center;
                horizontal = source == NavigationSource.Change
                    ? CsvGridRevealMode.Center : CsvGridRevealMode.Minimal;
            }

            recordMode = false;
            EditorPrefs.SetBool(ViewModePref, false);
            pendingNavigation = new PendingCellNavigation
            {
                Table = current,
                RecordIndex = physicalRecordIndex,
                ColumnIndex = physicalColumnIndex,
                Vertical = vertical,
                Horizontal = horizontal,
                RowScreenOffset = vertical == CsvGridRevealMode.Preserve ? capturedOffset : null,
                Source = source
            };
            if (quickInspector != null) quickInspector.SelectRecord(physicalRecordIndex);
            if (!string.IsNullOrEmpty(visibilityMessage))
                ShowNotification(new GUIContent(visibilityMessage));
            Repaint();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUI.BeginChangeCheck();
            CsvWorkspaceAsset selectedAsset = (CsvWorkspaceAsset)EditorGUILayout.ObjectField(
                workspaceAsset, typeof(CsvWorkspaceAsset), false, GUILayout.Width(150f));
            if (EditorGUI.EndChangeCheck())
            {
                // Changing the workspace changes the number of toolbar controls.
                // Defer it until this IMGUI event is complete so Layout/Repaint
                // always observe the same control tree.
                CsvWorkspaceAsset requestedAsset = selectedAsset;
                EditorApplication.delayCall += () =>
                {
                    if (this != null && this) SwitchWorkspaceAsset(requestedAsset);
                };
            }
            string folderLabel = workspace == null || string.IsNullOrEmpty(workspace.Folder) ? "None selected" : workspace.Folder;
            GUILayout.Label(folderLabel, NeoStyles.MiniDim, GUILayout.ExpandWidth(true));
            if (workspace != null && GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(55f)))
                RescanWorkspace();
            if (current != null && GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(55f))) ReloadCurrent();
            EditorGUI.BeginDisabledGroup(current == null);
            if (GUILayout.Button(new GUIContent("+ Row", "Insert a blank data row after the selected row"), EditorStyles.toolbarButton, GUILayout.Width(48f))) InsertRowAfterSelection();
            if (GUILayout.Button(new GUIContent("+ Column", "Insert a column after the selected column"), EditorStyles.toolbarButton, GUILayout.Width(66f))) PromptInsertColumnAfterSelection();
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(current == null || !current.CanUndo);
            if (GUILayout.Button("Undo", EditorStyles.toolbarButton, GUILayout.Width(42f))) UndoCurrent();
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(current == null || !current.CanRedo);
            if (GUILayout.Button("Redo", EditorStyles.toolbarButton, GUILayout.Width(42f))) RedoCurrent();
            EditorGUI.EndDisabledGroup();
            EditorGUI.BeginDisabledGroup(current == null || (!current.IsDirty && !HasActiveEditing));
            if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(42f))) SaveCurrent();
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUI.SetNextControlName("CsvToolSearch");
            string newSearch = EditorGUILayout.TextField(new GUIContent("Filter"), search, EditorStyles.toolbarSearchField);
            if (!string.Equals(newSearch, search, StringComparison.Ordinal))
            {
                CommitActiveEditing();
                search = newSearch;
                if (current != null)
                {
                    current.SetSearchQuery(search);
                    PreserveVisibleSelection();
                    RefreshRecordView();
                }
            }
            if (GUILayout.Button("×", EditorStyles.toolbarButton, GUILayout.Width(22f)))
            {
                search = string.Empty;
                if (current != null)
                {
                    current.SetSearchQuery(search);
                    PreserveVisibleSelection();
                    RefreshRecordView();
                }
                GUI.FocusControl(null);
            }
            EditorGUI.BeginDisabledGroup(current == null);
            if (GUILayout.Button(new GUIContent("Find", "Ctrl/Cmd+F: find a property or value"),
                EditorStyles.toolbarButton, GUILayout.Width(38f))) OpenContextualFind();
            if (GUILayout.Button(new GUIContent("Column", "Ctrl/Cmd+G: go to a column"),
                EditorStyles.toolbarButton, GUILayout.Width(50f))) OpenGoToColumn();
            EditorGUI.EndDisabledGroup();
            GUILayout.FlexibleSpace();
            // Always reserve this control. The count can change while handling
            // the search TextField event; conditionally adding it would make
            // IMGUI's Layout and Repaint control trees disagree.
            string matchLabel = current != null && current.SearchMatchCount > 0
                ? current.SearchMatchCount + " matches" : string.Empty;
            GUILayout.Label(matchLabel, NeoStyles.MiniDim, GUILayout.Width(74f));
            if (current != null)
            {
                bool includeStructure = GUILayout.Toggle(current.IncludeNonDataRows, "All rows",
                    EditorStyles.toolbarButton, GUILayout.Width(58f));
                if (includeStructure != current.IncludeNonDataRows)
                {
                    CommitActiveEditing();
                    current.IncludeNonDataRows = includeStructure;
                    PreserveVisibleSelection();
                    RefreshRecordView();
                }
                bool allowStructure = GUILayout.Toggle(current.AllowStructuralRowEdits,
                    new GUIContent("Edit non-data rows",
                        "Allows editing existing comment, section, and blank rows. Does not add or remove rows or columns; the CSV header remains read-only."),
                    EditorStyles.toolbarButton, GUILayout.Width(118f));
                if (allowStructure != current.AllowStructuralRowEdits)
                    current.AllowStructuralRowEdits = allowStructure;
            }
            if (grid != null)
            {
                GUILayout.Label("Frozen", NeoStyles.MiniDim, GUILayout.Width(42f));
                if (GUILayout.Button("-", EditorStyles.toolbarButton, GUILayout.Width(20f)))
                    SetFrozenColumnCount(grid.FrozenColumnCount - 1);
                GUILayout.Label(grid.FrozenColumnCount.ToString(), NeoStyles.MiniDim, GUILayout.Width(18f));
                if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(20f)))
                    SetFrozenColumnCount(grid.FrozenColumnCount + 1);
            }
            if (current != null)
            {
                bool nextQuickInspector = GUILayout.Toggle(showQuickInspector, "Inspector",
                    EditorStyles.toolbarButton, GUILayout.Width(62f));
                if (nextQuickInspector != showQuickInspector)
                {
                    CommitActiveEditing();
                    showQuickInspector = nextQuickInspector;
                    EditorPrefs.SetBool(QuickInspectorPref, showQuickInspector);
                    if (!showQuickInspector) UnbindQuickInspector();
                }
                string changesLabel = current.IsDirty ? "Changes *" : "Changes";
                bool nextShowChanges = GUILayout.Toggle(showChanges, changesLabel,
                    EditorStyles.toolbarButton, GUILayout.Width(68f));
                if (nextShowChanges != showChanges)
                {
                    CommitActiveEditing();
                    showChanges = nextShowChanges;
                }
                int schemaIssueCount = GetSchemaIssueCount();
                EditorGUI.BeginDisabledGroup(schemaIssueCount == 0);
                if (GUILayout.Button(schemaIssueCount == 0 ? "Schema OK" : "Schema " + schemaIssueCount,
                    EditorStyles.toolbarButton, GUILayout.Width(72f))) ShowSchemaIssues();
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSidebar(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            Rect inner = new Rect(rect.x + 4f, rect.y + 4f,
                Mathf.Max(1f, rect.width - 8f), Mathf.Max(1f, rect.height - 8f));
            GUI.Label(new Rect(inner.x, inner.y, inner.width, 18f), "Tables", NeoStyles.SectionTitle);

            Rect listRect = new Rect(inner.x, inner.y + 20f, inner.width,
                Mathf.Max(1f, inner.height - 20f));
            float rowHeight = 22f;
            float contentHeight = Mathf.Max(listRect.height, workspace.Tables.Count * rowHeight);
            Rect contentRect = new Rect(0f, 0f, Mathf.Max(1f, listRect.width - 16f), contentHeight);
            sidebarScroll = GUI.BeginScrollView(listRect, sidebarScroll, contentRect);
            if (workspace.Tables.Count == 0)
            {
                GUI.Label(new Rect(0f, 0f, contentRect.width, rowHeight), "No CSV files found.", NeoStyles.MiniDim);
            }
            else
            {
                for (int i = 0; i < workspace.Tables.Count; i++)
                {
                    CsvTableController table = workspace.Tables[i];
                    bool active = current == table;
                    Color old = GUI.backgroundColor;
                    if (active) GUI.backgroundColor = NeoColors.GridSelectionFill;
                    Rect row = new Rect(0f, i * rowHeight, contentRect.width, rowHeight);
                    if (GUI.Button(row, new GUIContent(table.Name, table.AbsolutePath), EditorStyles.toolbarButton))
                        SelectTable(table);
                    GUI.backgroundColor = old;
                }
            }
            GUI.EndScrollView();
        }

        private void DrawMain(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            if (current == null)
            {
                GUI.Label(new Rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, 22f), "Select a table to begin.", NeoStyles.MiniDim);
                return;
            }
            if (!current.IsLoaded)
            {
                try
                {
                    current.Open();
                    TryRecoverCurrent();
                }
                catch (Exception exception)
                {
                    GUI.Label(new Rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, 40f), "Unable to open CSV: " + exception.Message, EditorStyles.helpBox);
                    return;
                }
            }
            if (!showChanges)
            {
                DrawCurrentView(rect);
                return;
            }

            const float panelHeight = 170f;
            Rect gridRect = new Rect(rect.x, rect.y, rect.width, Mathf.Max(80f, rect.height - panelHeight - 4f));
            Rect panelRect = new Rect(rect.x, gridRect.yMax + 4f, rect.width, Mathf.Max(60f, rect.yMax - gridRect.yMax - 4f));
            DrawCurrentView(gridRect);
            DrawChangesPanel(panelRect);
        }

        private void DrawCurrentView(Rect rect)
        {
            if (!showQuickInspector)
            {
                DrawGrid(rect);
                return;
            }

            EnsureQuickInspector();
            float availableWidth = Mathf.Max(1f, rect.width - InspectorSplitterWidth);
            float maxInspectorWidth = Mathf.Max(MinimumInspectorWidth,
                availableWidth - MinimumGridWidth);
            float defaultInspectorWidth = Mathf.Min(DefaultInspectorWidth,
                Mathf.Max(MinimumInspectorWidth, rect.width * 0.4f));
            float inspectorWidth = EditorPrefs.GetFloat(QuickInspectorWidthPref,
                defaultInspectorWidth);
            inspectorWidth = Mathf.Clamp(inspectorWidth, MinimumInspectorWidth, maxInspectorWidth);
            float gridWidth = Mathf.Max(MinimumGridWidth, availableWidth - inspectorWidth);
            Rect gridRect = new Rect(rect.x, rect.y, gridWidth, rect.height);
            Rect splitterRect = new Rect(gridRect.xMax, rect.y, InspectorSplitterWidth, rect.height);
            Rect inspectorRect = new Rect(splitterRect.xMax, rect.y,
                Mathf.Max(1f, rect.xMax - splitterRect.xMax), rect.height);
            DrawGrid(gridRect);
            DrawInspectorSplitter(splitterRect, rect, inspectorWidth, maxInspectorWidth);
            if (quickInspector != null) quickInspector.Draw(inspectorRect);
        }

        private void DrawInspectorSplitter(Rect splitterRect, Rect contentRect,
            float inspectorWidth, float maxInspectorWidth)
        {
            EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);
            EditorGUI.DrawRect(splitterRect, new Color(0f, 0f, 0f, 0.18f));

            int controlId = GUIUtility.GetControlID(FocusType.Passive, splitterRect);
            Event currentEvent = Event.current;
            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 &&
                splitterRect.Contains(currentEvent.mousePosition))
            {
                GUIUtility.hotControl = controlId;
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.MouseDrag && GUIUtility.hotControl == controlId)
            {
                float newInspectorWidth = Mathf.Clamp(contentRect.xMax - currentEvent.mousePosition.x,
                    MinimumInspectorWidth, maxInspectorWidth);
                if (!Mathf.Approximately(newInspectorWidth, inspectorWidth))
                {
                    EditorPrefs.SetFloat(QuickInspectorWidthPref, newInspectorWidth);
                    Repaint();
                }
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.MouseUp && GUIUtility.hotControl == controlId)
            {
                GUIUtility.hotControl = 0;
                currentEvent.Use();
            }
        }

        private void DrawGrid(Rect rect)
        {
            if (grid == null) grid = new CsvGrid();
            Rect viewport = new Rect(rect.x + 4f, rect.y + 4f,
                Mathf.Max(1f, rect.width - 8f), Mathf.Max(1f, rect.height - 8f));
            grid.Draw(viewport, current.Document, current.VisibleRecordIndices);
            if (pendingNavigation != null)
            {
                PendingCellNavigation request = pendingNavigation;
                pendingNavigation = null;
                bool selected = ReferenceEquals(current, request.Table) &&
                    grid.SelectPhysicalCellForNavigation(request.RecordIndex, request.ColumnIndex,
                        request.Vertical, request.Horizontal, request.RowScreenOffset);
                if (selected) grid.Focus();
                else
                    ShowNotification(new GUIContent("Could not reveal row " + (request.RecordIndex + 1) +
                        ", column " + (request.ColumnIndex + 1) + "."));
            }
        }

        private void DrawChangesPanel(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            Rect inner = new Rect(rect.x + 5f, rect.y + 4f,
                Mathf.Max(1f, rect.width - 10f), Mathf.Max(1f, rect.height - 8f));
            const float headerHeight = 20f;
            GUI.Label(new Rect(inner.x, inner.y, inner.width, headerHeight), "Changes", NeoStyles.SectionTitle);
            if (current != null && current.IsDirty)
            {
                Rect saveRect = new Rect(inner.xMax - 46f, inner.y, 46f, headerHeight);
                Rect revertAllRect = new Rect(saveRect.x - 76f, inner.y, 72f, headerHeight);
                if (GUI.Button(revertAllRect, "Revert All", EditorStyles.miniButton))
                    RevertAllChanges();
                if (GUI.Button(saveRect, "Save", EditorStyles.miniButton))
                    SaveCurrent();
            }

            IReadOnlyList<CsvCellChange> changes = current == null ? null : current.Changes;
            if (changes == null || changes.Count == 0)
            {
                GUI.Label(new Rect(inner.x, inner.y + headerHeight, inner.width, 18f),
                    "No unsaved cell changes.", NeoStyles.MiniDim);
                return;
            }

            Rect listRect = new Rect(inner.x, inner.y + headerHeight, inner.width,
                Mathf.Max(1f, inner.height - headerHeight));
            const float rowHeight = 21f;
            float contentHeight = Mathf.Max(listRect.height, changes.Count * rowHeight);
            Rect contentRect = new Rect(0f, 0f, Mathf.Max(1f, listRect.width - 16f), contentHeight);
            changesScroll = GUI.BeginScrollView(listRect, changesScroll, contentRect);
            for (int i = 0; i < changes.Count; i++)
            {
                CsvCellChange change = changes[i];
                string label = "Row " + (change.RecordIndex + 1) + "  |  " + current.GetHeader(change.ColumnIndex)
                    + "  |  " + PreviewValue(change.OriginalValue) + "  ->  " + PreviewValue(change.CurrentValue);
                Rect rowRect = new Rect(0f, i * rowHeight, contentRect.width, rowHeight);
                Rect changeRect = new Rect(rowRect.x, rowRect.y, Mathf.Max(1f, rowRect.width - 54f), rowRect.height);
                Rect revertRect = new Rect(changeRect.xMax + 2f, rowRect.y, 52f, rowRect.height);
                if (GUI.Button(changeRect,
                    new GUIContent(label, change.OriginalValue + "\n---\n" + change.CurrentValue),
                    EditorStyles.miniButtonLeft))
                    JumpToChange(change.RecordIndex, change.ColumnIndex);
                if (GUI.Button(revertRect, "Revert", EditorStyles.miniButtonRight))
                {
                    current.RevertCell(change.RecordIndex, change.ColumnIndex);
                    grid.InvalidateVisibleRecordMap();
                    UpdateRecoveryJournal();
                    UpdateUnsavedState();
                    Repaint();
                    break;
                }
            }
            GUI.EndScrollView();
        }

        private static string PreviewValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "(empty)";
            string singleLine = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return singleLine.Length <= 36 ? singleLine : singleLine.Substring(0, 33) + "...";
        }

        private void DrawEmptyState(Rect rect)
        {
            string message = workspaceAsset == null
                ? "Assign a CSV Workspace asset in the toolbar to begin."
                : string.IsNullOrEmpty(recoveryWarning)
                    ? "The selected CSV Workspace asset has no available root directory."
                    : "Workspace error: " + recoveryWarning;
            GUI.Label(new Rect(rect.x + 12f, rect.y + 12f, rect.width - 24f, 22f),
                message, NeoStyles.HeaderSubtitle);
        }

        private void DrawStatusBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (current == null) GUILayout.Label("No table selected", NeoStyles.MiniDim);
            else
            {
                string state = current.HasExternalChange ? "File changed on disk" : (current.IsDirty ? "Unsaved changes" : "Ready");
                if (!string.IsNullOrEmpty(recoveryWarning)) state += " | " + recoveryWarning;
                int visibleRows = current.VisibleRecordIndices.Count;
                int eligibleRows = GetEligibleRowCount();
                GUILayout.Label(current.Name + "  ·  " + visibleRows + " of " + eligibleRows +
                    " rows visible  ·  " + state, NeoStyles.MiniDim);
                GUILayout.FlexibleSpace();
                CsvGridSelection selection = grid == null ? CsvGridSelection.Invalid : grid.Selection;
                if (recordMode && recordView != null && recordView.SelectedRecordIndex >= 0)
                    GUILayout.Label(DescribeRecordSelection(recordView.SelectedRecordIndex) + " (record view)", NeoStyles.MiniDim);
                else if (selection.IsValid)
                    GUILayout.Label(DescribeSelection(selection), NeoStyles.MiniDim);
            }
            EditorGUILayout.EndHorizontal();
        }

        private int GetEligibleRowCount()
        {
            if (current == null || current.Document == null) return 0;
            int count = 0;
            for (int i = 0; i < current.Document.Records.Count; i++)
            {
                CsvRecord record = current.Document.Records[i];
                if (i == current.HeaderRecordIndex) continue;
                if (!current.IncludeNonDataRows && record.Kind != CsvRecordKind.Data) continue;
                count++;
            }
            return count;
        }

        private string DescribeSelection(CsvGridSelection selection)
        {
            string result = "Row " + (selection.RecordIndex + 1) + ", " + current.GetHeader(selection.Column);
            CsvGridRangeSelection range = grid == null ? CsvGridRangeSelection.Invalid : grid.RangeSelection;
            if (range.IsValid && !range.IsSingleCell)
                result += "  |  " + (range.RowCount * range.ColumnCount) + " cells selected";
            CsvResolvedColumn identity = current.ResolvedSchema == null ? null : current.ResolvedSchema.Identity;
            if (identity != null && identity.IsResolved)
                result += "  |  " + current.GetHeader(identity.PhysicalIndex) + " = " +
                    PreviewValue(current.GetCell(selection.RecordIndex, identity.PhysicalIndex));
            return result;
        }

        private string DescribeRecordSelection(int recordIndex)
        {
            string result = "Row " + (recordIndex + 1);
            CsvResolvedColumn identity = current.ResolvedSchema == null ? null : current.ResolvedSchema.Identity;
            if (identity != null && identity.IsResolved)
                result += "  |  " + current.GetHeader(identity.PhysicalIndex) + " = " +
                    PreviewValue(current.GetCell(recordIndex, identity.PhysicalIndex));
            return result;
        }

        private void SelectRememberedTable()
        {
            if (workspace == null || workspace.Tables.Count == 0) { current = null; return; }
            string remembered = EditorPrefs.GetString(TablePref, string.Empty);
            current = workspace.Find(remembered) ?? workspace.Tables[0];
            if (current != null) current.SetSearchQuery(search);
            ApplyConfiguredPresentation();
        }

        private void SelectTable(CsvTableController table)
        {
            if (table == current) return;
            if (!CanLeaveCurrent("switch tables")) return;
            if (grid != null) grid.Reset();
            UnbindRecordView();
            current = table;
            EditorPrefs.SetString(TablePref, current.Name);
            current.SetSearchQuery(search);
            ApplyConfiguredPresentation();
            Repaint();
        }

        private void ReloadCurrent()
        {
            if (current == null) return;
            CommitActiveEditing();
            if (current.IsDirty && !EditorUtility.DisplayDialog("Reload CSV", "Discard unsaved changes and reload this file?", "Reload", "Cancel")) return;
            try
            {
                current.Reload();
                DeleteRecoveryJournal();
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("CSV Tool", exception.Message, "OK");
                return;
            }
            if (grid != null) grid.Reset();
            RefreshRecordView();
            valueIndexes.Remove(current);
            Repaint();
        }

        private void PreserveVisibleSelection()
        {
            if (grid != null) grid.InvalidateVisibleRecordMap();
        }

        private void SetFrozenColumnCount(int count)
        {
            if (grid == null) return;
            grid.FrozenColumnCount = Mathf.Max(0, count);
            EditorPrefs.SetInt(FrozenColumnsPref, grid.FrozenColumnCount);
            Repaint();
        }

        private void OnStructureMenuRequested(CsvGridSelection selection, bool header, Vector2 position)
        {
            if (current == null || !selection.IsValid) return;
            GenericMenu menu = new GenericMenu();
            if (!header)
            {
                menu.AddItem(new GUIContent("Insert Row Above"), false, () => InsertRow(selection.RecordIndex));
                menu.AddItem(new GUIContent("Insert Row Below"), false, () => InsertRow(selection.RecordIndex + 1));
                menu.AddSeparator(string.Empty);
            }
            menu.AddItem(new GUIContent("Insert Column Left"), false, () => PromptInsertColumn(selection.Column, position));
            menu.AddItem(new GUIContent("Insert Column Right"), false, () => PromptInsertColumn(selection.Column + 1, position));
            menu.ShowAsContext();
        }

        private void InsertRowAfterSelection()
        {
            int insertAt = current == null || current.Document == null ? -1 : current.Document.Records.Count;
            if (grid != null && grid.Selection.IsValid) insertAt = grid.Selection.RecordIndex + 1;
            InsertRow(insertAt);
        }

        private void InsertRow(int physicalInsertIndex)
        {
            if (current == null || grid == null) return;
            CommitActiveEditing();
            try
            {
                int inserted = current.InsertDataRow(physicalInsertIndex);
                grid.InvalidateVisibleRecordMap();
                grid.InvalidateDocumentLayout();
                valueIndexes.Remove(current);
                RefreshRecordView();
                // A filtered view can legitimately hide the new blank row; retain the filter rather than changing edit targeting.
                grid.SelectPhysicalCell(inserted, Mathf.Max(0, grid.Selection.Column));
                UpdateRecoveryJournal();
                UpdateUnsavedState();
                Repaint();
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("Unable to insert row", exception.Message, "OK");
            }
        }

        private void PromptInsertColumnAfterSelection()
        {
            int column = grid != null && grid.Selection.IsValid ? grid.Selection.Column + 1 : (current == null || current.Document == null ? 0 : current.Document.ColumnCount);
            PromptInsertColumn(column, new Vector2(position.width * 0.5f, 32f));
        }

        private void PromptInsertColumn(int physicalColumnIndex, Vector2 position)
        {
            if (current == null || grid == null) return;
            CommitActiveEditing();
            PopupWindow.Show(new Rect(position, Vector2.zero), new CsvNewColumnPopup(header => InsertColumn(physicalColumnIndex, header)));
        }

        private void InsertColumn(int physicalColumnIndex, string header)
        {
            if (current == null || grid == null) return;
            try
            {
                current.InsertColumn(physicalColumnIndex, header);
                grid.InvalidateVisibleRecordMap();
                grid.InvalidateDocumentLayout();
                valueIndexes.Remove(current);
                RefreshRecordView();
                if (grid.Selection.IsValid) grid.SelectPhysicalCell(grid.Selection.RecordIndex, physicalColumnIndex);
                UpdateRecoveryJournal();
                UpdateUnsavedState();
                Repaint();
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("Unable to insert column", exception.Message, "OK");
            }
        }

        private void OnCellEditCommitted(CsvGridCellEdit edit)
        {
            if (current == null || current.Document == null || !ReferenceEquals(current.Document, edit.Document)) return;
            try
            {
                if (!current.SetCell(edit.RecordIndex, edit.ColumnIndex, edit.NewValue)) return;
                grid.InvalidateVisibleRecordMap();
                InvalidateValueIndex(current, edit.ColumnIndex);
                RefreshRecordView();
                UpdateRecoveryJournal();
                UpdateUnsavedState();
                Repaint();
            }
            catch (InvalidOperationException exception)
            {
                EditorUtility.DisplayDialog("Cell is read-only", exception.Message, "OK");
            }
        }

        private void OnBatchEditCommitted(CsvGridBatchEdit batch)
        {
            if (current == null || current.Document == null || batch == null ||
                !ReferenceEquals(current.Document, batch.Document)) return;
            List<CsvCellAssignment> assignments = new List<CsvCellAssignment>(batch.Count);
            for (int i = 0; i < batch.Edits.Count; i++)
            {
                CsvGridCellEdit edit = batch.Edits[i];
                assignments.Add(new CsvCellAssignment(edit.RecordIndex, edit.ColumnIndex, edit.NewValue));
            }
            try
            {
                if (!current.SetCells(assignments)) return;
                grid.InvalidateVisibleRecordMap();
                if (batch.ExtendsColumns) grid.InvalidateDocumentLayout();
                for (int i = 0; i < batch.Edits.Count; i++)
                    InvalidateValueIndex(current, batch.Edits[i].ColumnIndex);
                RefreshRecordView();
                UpdateRecoveryJournal();
                UpdateUnsavedState();
                Repaint();
            }
            catch (InvalidOperationException exception)
            {
                EditorUtility.DisplayDialog("Paste rejected", exception.Message, "OK");
            }
        }

        private string GetPasteCellError(int recordIndex, int columnIndex)
        {
            return current == null ? "No CSV table is selected." : current.GetEditError(recordIndex, columnIndex);
        }

        private void OnPastePreflightRejected(CsvGridPastePreflight preflight)
        {
            if (preflight == null) return;
            EditorUtility.DisplayDialog("Paste not applied", preflight.Summary, "OK");
        }

        private void JumpToChange(int recordIndex, int columnIndex)
        {
            NavigateToPhysicalCell(recordIndex, columnIndex, NavigationSource.Change);
        }

        private void RevertAllChanges()
        {
            if (current == null || !current.IsDirty) return;
            if (!EditorUtility.DisplayDialog("Revert all CSV changes",
                "Restore every changed cell in " + current.Name + " to its loaded value?", "Revert All", "Cancel")) return;
            if (!current.RevertAll()) return;
            grid.InvalidateVisibleRecordMap();
            grid.InvalidateDocumentLayout();
            valueIndexes.Remove(current);
            RefreshRecordView();
            UpdateRecoveryJournal();
            UpdateUnsavedState();
            Repaint();
        }

        private void UndoCurrent()
        {
            if (current == null) return;
            CommitActiveEditing();
            int structuralRevision = current.StructuralRevision;
            if (!current.Undo()) return;
            RefreshAfterHistory(structuralRevision != current.StructuralRevision);
            UpdateRecoveryJournal();
            UpdateUnsavedState();
            Repaint();
        }

        private void RedoCurrent()
        {
            if (current == null) return;
            CommitActiveEditing();
            int structuralRevision = current.StructuralRevision;
            if (!current.Redo()) return;
            RefreshAfterHistory(structuralRevision != current.StructuralRevision);
            UpdateRecoveryJournal();
            UpdateUnsavedState();
            Repaint();
        }

        private void RefreshAfterHistory(bool structuralChange)
        {
            if (grid != null)
            {
                grid.InvalidateVisibleRecordMap();
                if (structuralChange) grid.InvalidateDocumentLayout();
            }
            valueIndexes.Remove(current);
            RefreshRecordView();
        }

        private void SaveCurrentFromShortcut()
        {
            SaveCurrent();
        }

        private bool SaveCurrent()
        {
            if (current == null) return true;
            CommitActiveEditing();
            if (!current.IsDirty) { UpdateUnsavedState(); return true; }
            try
            {
                current.Save();
                DeleteRecoveryJournal();
                UpdateUnsavedState();
                Repaint();
                return true;
            }
            catch (CsvExternalChangeException)
            {
                int choice = EditorUtility.DisplayDialogComplex("CSV changed on disk",
                    "The file changed after it was opened. Your in-memory edits are still intact.",
                    "Save Copy...", "Cancel", "Reload Disk");
                if (choice == 0) SaveConflictCopy();
                else if (choice == 2)
                {
                    try
                    {
                        current.Reload();
                        DeleteRecoveryJournal();
                        if (grid != null) grid.Reset();
                        RefreshRecordView();
                    }
                    catch (Exception exception)
                    {
                        EditorUtility.DisplayDialog("Unable to reload CSV", exception.Message, "OK");
                        choice = 1;
                    }
                }
                UpdateUnsavedState();
                Repaint();
                return choice == 2;
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("Unable to save CSV", exception.Message, "OK");
                UpdateUnsavedState();
                return false;
            }
        }

        private void SaveConflictCopy()
        {
            if (current == null || current.Document == null) return;
            string directory = Path.GetDirectoryName(current.AbsolutePath);
            string name = Path.GetFileNameWithoutExtension(current.AbsolutePath) + "-local-copy.csv";
            string path = EditorUtility.SaveFilePanel("Save local CSV copy", directory, name, "csv");
            if (string.IsNullOrEmpty(path)) return;
            try { File.WriteAllBytes(path, current.Document.Serialize()); }
            catch (Exception exception) { EditorUtility.DisplayDialog("Unable to save copy", exception.Message, "OK"); }
        }

        private bool CanLeaveCurrent(string action)
        {
            if (current == null) return true;
            CommitActiveEditing();
            if (!current.IsDirty) return true;
            int choice = EditorUtility.DisplayDialogComplex("Unsaved CSV changes",
                "Save changes to " + current.Name + " before you " + action + "?",
                "Save", "Cancel", "Discard");
            if (choice == 0) return SaveCurrent();
            if (choice == 1) return false;
            try
            {
                current.Reload();
                DeleteRecoveryJournal();
                if (grid != null) grid.Reset();
                UpdateUnsavedState();
                return true;
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("Unable to discard changes", exception.Message, "OK");
                return false;
            }
        }

        private void RescanWorkspace()
        {
            if (workspace == null || !CanLeaveCurrent("rescan the workspace")) return;
            workspace.Discover();
            valueIndexes.Clear();
            if (grid != null) grid.Reset();
            UnbindRecordView();
            SelectRememberedTable();
            Repaint();
        }

        private bool HasActiveEditing
        {
            get
            {
                return (grid != null && grid.IsEditing) ||
                    (recordView != null && recordView.IsEditing) ||
                    (quickInspector != null && quickInspector.RecordView.IsEditing);
            }
        }

        private void CommitActiveEditing()
        {
            if (grid != null) grid.CommitEditing();
            if (recordView != null && recordView.IsEditing) recordView.CommitEditing();
            if (quickInspector != null && quickInspector.RecordView.IsEditing)
                quickInspector.RecordView.CommitEditing();
        }

        private void UpdateUnsavedState()
        {
            bool unsaved = (current != null && current.IsDirty) || HasActiveEditing;
            hasUnsavedChanges = unsaved;
            saveChangesMessage = current == null
                ? "The CSV tool has unsaved changes."
                : "Save changes to " + current.Name + "?";
        }

        public override void SaveChanges()
        {
            if (SaveCurrent()) base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            if (grid != null) grid.CancelEditing();
            if (recordView != null) recordView.CancelEditing();
            if (quickInspector != null) quickInspector.RecordView.CancelEditing();
            if (current != null && current.IsDirty)
            {
                try
                {
                    current.Reload();
                    DeleteRecoveryJournal();
                }
                catch (Exception exception)
                {
                    EditorUtility.DisplayDialog("Unable to discard changes", exception.Message, "OK");
                    return;
                }
            }
            UpdateUnsavedState();
            base.DiscardChanges();
        }

        private void UpdateRecoveryJournal()
        {
            if (current == null || !current.IsLoaded) return;
            try
            {
                if (current.Document.HasStructuralChanges)
                {
                    // Version 1 journals address only loaded cell coordinates. Replaying one after an insertion
                    // could target a shifted cell, so never write a deceptively recoverable journal.
                    CsvRecoveryJournal.Delete(current.AbsolutePath);
                    recoveryWarning = "Structural edits are not recoverable until saved";
                    return;
                }
                if (current.IsDirty) CsvRecoveryJournal.Write(current.AbsolutePath, current.Changes);
                else CsvRecoveryJournal.Delete(current.AbsolutePath);
                recoveryWarning = string.Empty;
            }
            catch (Exception exception)
            {
                recoveryWarning = "Recovery journal failed";
                Debug.LogWarning("CSV Tool recovery journal: " + exception.Message);
            }
        }

        private void DeleteRecoveryJournal()
        {
            if (current == null) return;
            try
            {
                CsvRecoveryJournal.Delete(current.AbsolutePath);
                recoveryWarning = string.Empty;
            }
            catch (Exception exception)
            {
                recoveryWarning = "Recovery cleanup failed";
                Debug.LogWarning("CSV Tool recovery cleanup: " + exception.Message);
            }
        }

        private void TryRecoverCurrent()
        {
            if (current == null || current.Document == null || !recoveryCheckedPaths.Add(current.AbsolutePath)) return;
            CsvRecoveryJournal.Entry entry;
            if (!CsvRecoveryJournal.TryRead(current.AbsolutePath, out entry)) return;
            CsvRecoveryJournal.Compatibility compatibility = CsvRecoveryJournal.Validate(current.AbsolutePath, entry);
            if (compatibility != CsvRecoveryJournal.Compatibility.Compatible)
            {
                int incompatibleChoice = EditorUtility.DisplayDialogComplex("CSV recovery found",
                    "Unsaved edits were found, but the CSV changed on disk and they cannot be applied safely (" +
                    compatibility + "). The journal can be kept for manual recovery.",
                    "Keep Journal", "Delete Journal", "Cancel");
                if (incompatibleChoice == 1) DeleteRecoveryJournal();
                return;
            }

            int choice = EditorUtility.DisplayDialogComplex("Recover unsaved CSV edits",
                entry.Changes.Count + " unsaved cell change(s) from " + entry.TimestampUtc.ToLocalTime() +
                " were found for " + current.Name + ".",
                "Recover", "Discard", "Cancel");
            if (choice == 0)
            {
                if (CsvRecoveryJournal.TryApply(current.Document, entry))
                {
                    current.RefreshView();
                    grid.InvalidateVisibleRecordMap();
                    grid.InvalidateDocumentLayout();
                    showChanges = true;
                    UpdateRecoveryJournal();
                }
                else
                    EditorUtility.DisplayDialog("Recovery could not be applied",
                        "The document no longer matches the saved recovery values. No cells were changed.", "OK");
            }
            else if (choice == 1) DeleteRecoveryJournal();
            UpdateUnsavedState();
        }

        private void PollExternalState()
        {
            if (this == null || !this) return;
            if (EditorApplication.timeSinceStartup < nextExternalPoll) return;
            nextExternalPoll = EditorApplication.timeSinceStartup + 0.5d;
            if (current != null) current.PollExternalChange();
            Repaint();
        }

        private static CsvWorkspaceAsset LoadRememberedWorkspaceAsset()
        {
            string guid = EditorPrefs.GetString(WorkspaceAssetPref, string.Empty);
            if (string.IsNullOrEmpty(guid)) return null;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<CsvWorkspaceAsset>(path);
        }

        private void SwitchWorkspaceAsset(CsvWorkspaceAsset asset)
        {
            if (asset == workspaceAsset) return;
            if (!CanLeaveCurrent("switch CSV workspaces")) return;
            workspaceAsset = asset;
            if (asset == null)
            {
                EditorPrefs.DeleteKey(WorkspaceAssetPref);
                workspace = null;
                valueIndexes.Clear();
                current = null;
                if (grid != null) grid.Reset();
                UnbindRecordView();
                SelectRememberedTable();
                Repaint();
                return;
            }
            ApplyWorkspaceAsset(asset);
            Repaint();
        }

        private void ApplyWorkspaceAsset(CsvWorkspaceAsset asset)
        {
            string root;
            string error;
            if (!CsvWorkspacePathResolver.TryResolveRootDirectory(asset.RootDirectory,
                CsvWorkspacePathResolver.GetProjectRoot(), out root, out error))
            {
                workspace = null;
                current = null;
                recoveryWarning = error;
                return;
            }
            string assetPath = AssetDatabase.GetAssetPath(asset);
            string guid = string.IsNullOrEmpty(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
            if (!string.IsNullOrEmpty(guid)) EditorPrefs.SetString(WorkspaceAssetPref, guid);
            workspace = new CsvWorkspaceController(root, asset.ToSchema(), asset.includeUnconfiguredTables);
            recoveryWarning = string.Empty;
            valueIndexes.Clear();
            current = null;
            search = string.Empty;
            if (grid != null) grid.Reset();
            UnbindRecordView();
            SelectRememberedTable();
        }

        private CsvWorkspaceTableConfig GetCurrentTableConfig()
        {
            if (workspaceAsset == null || current == null || workspaceAsset.tables == null) return null;
            for (int i = 0; i < workspaceAsset.tables.Count; i++)
            {
                CsvWorkspaceTableConfig item = workspaceAsset.tables[i];
                if (item != null && string.Equals(item.name, current.Name, StringComparison.OrdinalIgnoreCase)) return item;
            }
            return null;
        }

        private void ApplyConfiguredPresentation()
        {
            CsvWorkspaceTableConfig config = GetCurrentTableConfig();
            if (grid != null)
            {
                int frozen = EditorPrefs.GetInt(FrozenColumnsPref, 1);
                if (config != null)
                {
                    frozen = config.FrozenColumnCount;
                    // The grid freezes the leading physical columns.  Preserve the
                    // explicit workspace indices when they form that prefix.
                    while (config.FrozenColumns != null && config.FrozenColumns.Contains(frozen))
                        frozen++;
                }
                grid.FrozenColumnCount = Mathf.Max(0, frozen);
            }
        }

        private CsvRecordViewDefinition BuildRecordDefinition()
        {
            CsvWorkspaceTableConfig config = GetCurrentTableConfig();
            if (config == null || config.columns == null || config.columns.Count == 0) return null;
            List<CsvWorkspaceColumnConfig> columns = new List<CsvWorkspaceColumnConfig>();
            for (int i = 0; i < config.columns.Count; i++)
                if (config.columns[i] != null) columns.Add(config.columns[i]);
            columns.Sort((left, right) =>
            {
                int groupComparison = left.group.CompareTo(right.group);
                return groupComparison != 0 ? groupComparison : left.order.CompareTo(right.order);
            });
            CsvRecordViewDefinition definition = new CsvRecordViewDefinition();
            for (int i = 0; i < columns.Count; i++)
            {
                CsvWorkspaceColumnConfig column = columns[i];
                if (column == null) continue;
                string group = column.group <= 0 ? "General" : "Group " + column.group;
                CsvRecordFieldDefinition field = column.index >= 0
                    ? new CsvRecordFieldDefinition(column.index, group)
                    : new CsvRecordFieldDefinition(column.name, group);
                field.DisplayName = column.displayName;
                field.HelpText = column.helpText;
                definition.Fields.Add(field);
            }
            return definition;
        }

        private void EnsureRecordView()
        {
            if (current == null || !current.IsLoaded) return;
            if (recordView != null && ReferenceEquals(recordView.Controller, current)) return;
            UnbindRecordView();
            recordView = new CsvRecordView(current, current.Schema, BuildRecordDefinition());
            recordView.AutocompleteProvider = GetAutocompleteSuggestions;
            recordView.CellEditCommitted += OnRecordCellEditCommitted;
            recordView.RecordSelected += OnRecordSelected;
            recordView.RepaintRequested += Repaint;
            CsvGridSelection selection = grid == null ? CsvGridSelection.Invalid : grid.Selection;
            if (selection.IsValid) recordView.SelectRecord(selection.RecordIndex);
        }

        private void RefreshRecordView()
        {
            if (current == null || !current.IsLoaded) return;
            CsvRecordViewDefinition definition = BuildRecordDefinition();
            if (recordView != null) recordView.Bind(current, current.Schema, definition);
            if (quickInspector != null) quickInspector.Bind(current, current.Schema, definition);
        }

        private void UnbindRecordView()
        {
            if (recordView != null)
            {
                recordView.CellEditCommitted -= OnRecordCellEditCommitted;
                recordView.RecordSelected -= OnRecordSelected;
                recordView.RepaintRequested -= Repaint;
                recordView = null;
            }
            UnbindQuickInspector();
        }

        private void EnsureQuickInspector()
        {
            if (!showQuickInspector || current == null || !current.IsLoaded) return;
            if (quickInspector != null && ReferenceEquals(quickInspector.Controller, current)) return;
            UnbindQuickInspector();
            quickInspector = new CsvQuickRecordInspector(current, current.Schema, BuildRecordDefinition());
            quickInspector.RecordView.AutocompleteProvider = GetAutocompleteSuggestions;
            quickInspector.CellEditCommitted += OnQuickInspectorCellEditCommitted;
            quickInspector.RecordSelected += OnRecordSelected;
            quickInspector.ColumnSelected += OnQuickInspectorColumnSelected;
            quickInspector.CloseRequested += OnQuickInspectorCloseRequested;
            quickInspector.RepaintRequested += Repaint;
            CsvGridSelection selection = grid == null ? CsvGridSelection.Invalid : grid.Selection;
            int recordIndex = selection.IsValid ? selection.RecordIndex : GetNavigationRecordIndex();
            if (recordIndex >= 0) quickInspector.SelectRecord(recordIndex);
        }

        private void UnbindQuickInspector()
        {
            if (quickInspector == null) return;
            quickInspector.CellEditCommitted -= OnQuickInspectorCellEditCommitted;
            quickInspector.RecordSelected -= OnRecordSelected;
            quickInspector.ColumnSelected -= OnQuickInspectorColumnSelected;
            quickInspector.CloseRequested -= OnQuickInspectorCloseRequested;
            quickInspector.RepaintRequested -= Repaint;
            quickInspector.Unbind();
            quickInspector = null;
        }

        private void OnGridSelectionChanged(CsvGridSelection selection)
        {
            if (syncingSelection || !selection.IsValid) return;
            syncingSelection = true;
            // Grid selection transfers interaction ownership. Record views must finish their
            // field session before they mirror the new physical record, otherwise a popup could
            // remain attached to a field which is no longer the active editor control.
            if (recordView != null) recordView.CommitEditing();
            if (quickInspector != null) quickInspector.RecordView.CommitEditing();
            if (recordView != null) recordView.SelectRecord(selection.RecordIndex);
            if (quickInspector != null) quickInspector.SelectRecord(selection.RecordIndex);
            syncingSelection = false;
        }

        private void OnRecordSelected(int physicalRecordIndex)
        {
            if (syncingSelection || grid == null) return;
            syncingSelection = true;
            int column = grid.Selection.IsValid ? grid.Selection.Column : 0;
            grid.SelectPhysicalCell(physicalRecordIndex, column, false);
            syncingSelection = false;
        }

        private void OnRecordCellEditCommitted(CsvRecordViewCellEdit edit)
        {
            if (current == null || recordView == null || !ReferenceEquals(recordView.Controller, current)) return;
            HandleRecordCellEditCommitted(edit);
        }

        private void OnQuickInspectorCellEditCommitted(CsvRecordViewCellEdit edit)
        {
            if (current == null || quickInspector == null ||
                !ReferenceEquals(quickInspector.Controller, current)) return;
            HandleRecordCellEditCommitted(edit);
        }

        private void HandleRecordCellEditCommitted(CsvRecordViewCellEdit edit)
        {
            if (grid != null) grid.InvalidateVisibleRecordMap();
            InvalidateValueIndex(current, edit.ColumnIndex);
            RefreshRecordView();
            UpdateRecoveryJournal();
            UpdateUnsavedState();
            Repaint();
        }

        private void OnQuickInspectorColumnSelected(int physicalRecordIndex, int physicalColumnIndex)
        {
            NavigateToPhysicalCell(physicalRecordIndex, physicalColumnIndex, NavigationSource.Inspector);
        }

        private void OnQuickInspectorCloseRequested()
        {
            showQuickInspector = false;
            EditorPrefs.SetBool(QuickInspectorPref, false);
            UnbindQuickInspector();
            Repaint();
        }

        private int GetSchemaIssueCount()
        {
            return current == null || current.ResolvedSchema == null ? 0 : current.ResolvedSchema.Diagnostics.Count;
        }

        private void ShowSchemaIssues()
        {
            if (current == null || current.ResolvedSchema == null ||
                current.ResolvedSchema.Diagnostics.Count == 0) return;
            System.Text.StringBuilder message = new System.Text.StringBuilder();
            for (int i = 0; i < current.ResolvedSchema.Diagnostics.Count; i++)
            {
                CsvSchemaIssue issue = current.ResolvedSchema.Diagnostics[i];
                if (message.Length > 0) message.AppendLine().AppendLine();
                message.Append(issue.Severity).Append("  ").Append(issue.Code).AppendLine();
                message.Append(issue.Message);
                if (!string.IsNullOrEmpty(issue.ColumnName))
                    message.AppendLine().Append("Column: ").Append(issue.ColumnName);
            }
            EditorUtility.DisplayDialog("Schema issues — " + current.Name, message.ToString(), "OK");
        }

        private IReadOnlyList<string> GetAutocompleteSuggestions(int physicalRecord, int columnIndex, string query)
        {
            if (current == null || !current.IsLoaded) return EmptySuggestions;
            const int cap = 12;
            List<string> suggestions = new List<string>(cap);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CsvColumnSchema configuredColumn = GetConfiguredColumn(current, columnIndex);
            string lookupQuery = query ?? string.Empty;
            string replacementPrefix = string.Empty;
            bool tokenCompletion = TryGetDelimitedCompletion(configuredColumn, lookupQuery,
                out lookupQuery, out replacementPrefix);
            if (configuredColumn != null)
            {
                for (int i = 0; i < configuredColumn.EnumValues.Count && suggestions.Count < cap; i++)
                    AddSuggestion(suggestions, seen, configuredColumn.EnumValues[i], lookupQuery, replacementPrefix);
                for (int i = 0; i < configuredColumn.References.Count && suggestions.Count < cap; i++)
                {
                    CsvReferenceSpec reference = configuredColumn.References[i];
                    CsvTableController target = EnsureReferenceTargetOpen(reference);
                    int keyColumn = ResolveTargetColumn(target, reference == null ? null : reference.TargetKeyColumn);
                    if (target == null || keyColumn < 0) continue;
                    IReadOnlyList<CsvIndexedValue> keys = GetValueIndex(target).LookupPrefix(keyColumn, lookupQuery, cap);
                    for (int keyIndex = 0; keyIndex < keys.Count && suggestions.Count < cap; keyIndex++)
                        AddSuggestion(suggestions, seen, keys[keyIndex].Value, lookupQuery, replacementPrefix);
                }
            }
            // Whole-cell values are useful for ordinary text columns. For a
            // delimited list they would replace unrelated tokens, so only the
            // explicit enum/reference token sources participate.
            if (!tokenCompletion)
            {
                IReadOnlyList<CsvIndexedValue> local = GetValueIndex(current).LookupPrefix(columnIndex, lookupQuery, cap);
                for (int i = 0; i < local.Count && suggestions.Count < cap; i++)
                    AddSuggestion(suggestions, seen, local[i].Value, lookupQuery, replacementPrefix);
            }
            return suggestions;
        }

        private static readonly IReadOnlyList<string> EmptySuggestions = new string[0];

        private static void AddSuggestion(List<string> destination, HashSet<string> seen, string value,
            string query, string replacementPrefix)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith(query ?? string.Empty,
                StringComparison.OrdinalIgnoreCase) || !seen.Add(value)) return;
            destination.Add((replacementPrefix ?? string.Empty) + value);
        }

        private static bool TryGetDelimitedCompletion(CsvColumnSchema column, string text,
            out string tokenQuery, out string replacementPrefix)
        {
            tokenQuery = text ?? string.Empty;
            replacementPrefix = string.Empty;
            CsvTokenSyntax syntax = column == null ? null : column.TokenSyntax;
            if (syntax == null || syntax.Mode != CsvTokenExtractionMode.Delimited ||
                string.IsNullOrEmpty(syntax.Separator)) return false;
            int separator = tokenQuery.LastIndexOf(syntax.Separator, StringComparison.Ordinal);
            int tokenStart = separator < 0 ? 0 : separator + syntax.Separator.Length;
            while (tokenStart < tokenQuery.Length && char.IsWhiteSpace(tokenQuery[tokenStart])) tokenStart++;
            replacementPrefix = tokenQuery.Substring(0, tokenStart);
            tokenQuery = tokenQuery.Substring(tokenStart);
            return true;
        }

        private CsvValueIndex GetValueIndex(CsvTableController table)
        {
            CsvValueIndex index;
            if (!valueIndexes.TryGetValue(table, out index))
            {
                index = new CsvValueIndex(table);
                valueIndexes.Add(table, index);
            }
            return index;
        }

        private void InvalidateValueIndex(CsvTableController table, int columnIndex)
        {
            CsvValueIndex index;
            if (table != null && valueIndexes.TryGetValue(table, out index)) index.InvalidateColumn(columnIndex);
        }

        private static CsvColumnSchema GetConfiguredColumn(CsvTableController table, int physicalColumn)
        {
            return table == null ? null : table.GetConfiguredColumn(physicalColumn);
        }

        private CsvTableController EnsureReferenceTargetOpen(CsvReferenceSpec reference)
        {
            if (reference == null || workspace == null) return null;
            CsvTableController target = workspace.Find(reference.TargetTable);
            if (target == null) return null;
            if (!target.IsLoaded)
            {
                try { target.Open(); }
                catch (Exception) { return null; }
            }
            return target;
        }

        private static int ResolveTargetColumn(CsvTableController target, string selector)
        {
            int physicalColumn;
            return target != null && target.TryResolvePhysicalColumn(selector, out physicalColumn)
                ? physicalColumn : -1;
        }

        private void OnReferenceNavigationRequested(int recordIndex, int columnIndex, string value)
        {
            if (current == null || workspace == null) return;
            CsvColumnSchema column = GetConfiguredColumn(current, columnIndex);
            if (column == null || column.References.Count == 0)
            {
                ShowNotification(new GUIContent("No reference is configured for this column."));
                return;
            }
            for (int i = 0; i < column.References.Count; i++) EnsureReferenceTargetOpen(column.References[i]);
            IReadOnlyList<CsvReferenceResolution> resolutions =
                CsvReferenceResolver.ResolveAll(current, recordIndex, columnIndex, workspace);
            List<CsvReferenceResolution> resolved = new List<CsvReferenceResolution>();
            for (int i = 0; i < resolutions.Count; i++)
                if (resolutions[i].Status == CsvReferenceResolutionStatus.Resolved) resolved.Add(resolutions[i]);
            if (resolved.Count == 1) NavigateToReference(resolved[0]);
            else if (resolved.Count > 1)
            {
                GenericMenu menu = new GenericMenu();
                for (int i = 0; i < resolved.Count; i++)
                {
                    CsvReferenceResolution result = resolved[i];
                    string label = result.TargetTableName + "/" +
                        (string.IsNullOrEmpty(result.TargetDisplay) ? result.TargetKey : result.TargetDisplay);
                    menu.AddItem(new GUIContent(label), false, () => NavigateToReference(result));
                }
                menu.ShowAsContext();
            }
            else ShowNotification(new GUIContent("Reference not found: " + value));
        }

        private void NavigateToReference(CsvReferenceResolution reference)
        {
            CsvTableController target = workspace == null ? null : workspace.Find(reference.TargetTableName);
            if (target == null) return;
            SelectTable(target);
            if (!ReferenceEquals(current, target)) return;
            if (!target.IsLoaded) target.Open();
            NavigateToPhysicalCell(reference.TargetRecordIndex, reference.TargetKeyColumnIndex,
                NavigationSource.Reference);
        }
    }
}
