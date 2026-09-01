using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CsvTool.Editor.Search
{
    /// <summary>Keyboard-first popup for contextual physical-cell search.</summary>
    public sealed class CsvContextualFindPopup : PopupWindowContent
    {
        private const float SearchHeight = 27f;
        private const float ItemHeight = 38f;
        private const float MaximumListHeight = 380f;
        private const float FooterHeight = 20f;
        private const int MaximumDisplayedResults = 200;

        private readonly Func<string, IReadOnlyList<CsvContextualCellResult>> search;
        private readonly Action<CsvContextualCellResult> onSelect;
        private readonly float width;
        private IReadOnlyList<CsvContextualCellResult> results = new CsvContextualCellResult[0];
        private string query = string.Empty;
        private int selectedIndex;
        private Vector2 scroll;
        private bool focusPending = true;

        public CsvContextualFindPopup(Func<string, IReadOnlyList<CsvContextualCellResult>> search,
            Action<CsvContextualCellResult> onSelect, float width = 440f)
        {
            this.search = search;
            this.onSelect = onSelect;
            this.width = Mathf.Max(300f, width);
        }

        public static void Show(Rect activatorRect,
            Func<string, IReadOnlyList<CsvContextualCellResult>> search,
            Action<CsvContextualCellResult> onSelect)
        {
            PopupWindow.Show(activatorRect,
                new CsvContextualFindPopup(search, onSelect, Mathf.Max(activatorRect.width, 440f)));
        }

        public override Vector2 GetWindowSize()
        {
            int count = Math.Min(results.Count, MaximumDisplayedResults);
            float listHeight = Mathf.Clamp(count * ItemHeight, ItemHeight, MaximumListHeight);
            float footer = results.Count > MaximumDisplayedResults ? FooterHeight : 0f;
            return new Vector2(width, SearchHeight + listHeight + footer + 6f);
        }

        public override void OnOpen()
        {
            editorWindow.wantsMouseMove = true;
        }

        public override void OnGUI(Rect rect)
        {
            HandleKeyboard();
            DrawSearch(rect);
            DrawResults(new Rect(rect.x, rect.y + SearchHeight, rect.width,
                rect.height - SearchHeight));
            if (Event.current.type == EventType.MouseMove) editorWindow.Repaint();
        }

        private void DrawSearch(Rect rect)
        {
            Rect searchRect = new Rect(rect.x + 5f, rect.y + 4f, rect.width - 10f, 19f);
            GUI.SetNextControlName("CsvContextualFindSearch");
            EditorGUI.BeginChangeCheck();
            query = EditorGUI.TextField(searchRect, query, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                results = search == null ? new CsvContextualCellResult[0] :
                    search(query) ?? new CsvContextualCellResult[0];
                selectedIndex = 0;
                scroll = Vector2.zero;
                editorWindow.Repaint();
            }
            if (focusPending)
            {
                EditorGUI.FocusTextInControl("CsvContextualFindSearch");
                focusPending = false;
            }
        }

        private void DrawResults(Rect rect)
        {
            int count = Math.Min(results.Count, MaximumDisplayedResults);
            if (count == 0)
            {
                GUI.Label(rect, string.IsNullOrEmpty(query)
                    ? "Find a property or value in this table"
                    : "No matching cells", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            bool truncated = results.Count > MaximumDisplayedResults;
            Rect listRect = truncated
                ? new Rect(rect.x, rect.y, rect.width, Mathf.Max(1f, rect.height - FooterHeight))
                : rect;
            Rect content = new Rect(0f, 0f, Mathf.Max(1f, listRect.width - 16f), count * ItemHeight);
            scroll = GUI.BeginScrollView(listRect, scroll, content);
            for (int i = 0; i < count; i++) DrawResult(content, i, results[i]);
            GUI.EndScrollView();
            if (truncated)
                GUI.Label(new Rect(rect.x + 8f, listRect.yMax, rect.width - 16f, FooterHeight),
                    "Showing first " + MaximumDisplayedResults + " of " + results.Count + " results",
                    EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawResult(Rect content, int index, CsvContextualCellResult result)
        {
            Rect row = new Rect(0f, index * ItemHeight, content.width, ItemHeight);
            Event current = Event.current;
            bool hovered = row.Contains(current.mousePosition);
            if (hovered && (current.type == EventType.MouseMove || current.type == EventType.MouseDrag))
                selectedIndex = index;
            if (current.type == EventType.Repaint && index == selectedIndex)
                EditorGUI.DrawRect(row, new Color(0.22f, 0.38f, 0.58f, 0.45f));

            string scope = result.IsSelectedRow ? "Current record" : "Row " + (result.RecordIndex + 1);
            GUI.Label(new Rect(row.x + 8f, row.y + 2f, row.width - 16f, 17f),
                scope + "  |  " + result.HeaderPreview, EditorStyles.boldLabel);
            string value = string.IsNullOrEmpty(result.ValuePreview) ? "(empty)" : Preview(result.ValuePreview);
            GUI.Label(new Rect(row.x + 8f, row.y + 19f, row.width - 16f, 16f),
                value, EditorStyles.miniLabel);

            if (current.type == EventType.MouseDown && hovered)
            {
                Commit();
                current.Use();
            }
        }

        private void HandleKeyboard()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown) return;
            int count = Math.Min(results.Count, MaximumDisplayedResults);
            if (current.keyCode == KeyCode.DownArrow && count > 0)
            {
                selectedIndex = (selectedIndex + 1) % count;
                EnsureVisible();
                current.Use();
            }
            else if (current.keyCode == KeyCode.UpArrow && count > 0)
            {
                selectedIndex = (selectedIndex - 1 + count) % count;
                EnsureVisible();
                current.Use();
            }
            else if ((current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter ||
                current.keyCode == KeyCode.F3) && count > 0)
            {
                Commit();
                current.Use();
            }
            else if (current.keyCode == KeyCode.Escape)
            {
                editorWindow.Close();
                current.Use();
            }
        }

        private void EnsureVisible()
        {
            float y = selectedIndex * ItemHeight;
            if (y < scroll.y) scroll.y = y;
            if (y + ItemHeight > scroll.y + MaximumListHeight)
                scroll.y = y + ItemHeight - MaximumListHeight;
            editorWindow.Repaint();
        }

        private void Commit()
        {
            int count = Math.Min(results.Count, MaximumDisplayedResults);
            if (selectedIndex < 0 || selectedIndex >= count) return;
            CsvContextualCellResult selected = results[selectedIndex];
            editorWindow.Close();
            if (onSelect != null) onSelect(selected);
        }

        private static string Preview(string value)
        {
            string flattened = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            return flattened.Length <= 120 ? flattened : flattened.Substring(0, 119) + "\u2026";
        }
    }
}
