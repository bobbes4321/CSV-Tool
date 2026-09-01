using System;
using UnityEditor;
using UnityEngine;

namespace CsvTool.Editor
{
    /// <summary>Reusable IMGUI popup for CsvGoToColumnModel (Ctrl+K/other shortcuts are caller-owned).</summary>
    public sealed class CsvGoToColumnPopup : PopupWindowContent
    {
        private const float ItemHeight = 34f;
        private const float SearchHeight = 24f;
        private const float MaxListHeight = 300f;
        private readonly CsvGoToColumnModel model;
        private readonly Action<int> onSelect;
        private readonly float width;
        private string search;
        private Vector2 scroll;
        private bool focusPending = true;

        public CsvGoToColumnPopup(CsvGoToColumnModel model, Action<int> onSelect, float width = 320f)
        {
            this.model = model ?? new CsvGoToColumnModel(null);
            this.onSelect = onSelect;
            this.width = Mathf.Max(220f, width);
            search = this.model.Query;
        }

        public static void Show(Rect activatorRect, CsvGoToColumnModel model, Action<int> onSelect)
        {
            PopupWindow.Show(activatorRect, new CsvGoToColumnPopup(model, onSelect,
                Mathf.Max(activatorRect.width, 320f)));
        }

        public override Vector2 GetWindowSize()
        {
            int rows = model.Results.Count;
            float listHeight = Mathf.Clamp(rows * ItemHeight, ItemHeight, MaxListHeight);
            return new Vector2(width, SearchHeight + listHeight + 8f);
        }

        public override void OnOpen()
        {
            editorWindow.wantsMouseMove = true;
            EnsureVisible(false);
        }

        public override void OnGUI(Rect rect)
        {
            HandleKeyboard();
            Rect searchRect = new Rect(rect.x + 4f, rect.y + 3f, rect.width - 8f, 18f);
            GUI.SetNextControlName("CsvGoToColumnSearch");
            EditorGUI.BeginChangeCheck();
            search = EditorGUI.TextField(searchRect, search ?? string.Empty, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck()) model.SetQuery(search);
            if (focusPending)
            {
                EditorGUI.FocusTextInControl("CsvGoToColumnSearch");
                focusPending = false;
            }

            Rect listRect = new Rect(rect.x, rect.y + SearchHeight, rect.width, rect.height - SearchHeight);
            Rect viewRect = new Rect(0f, 0f, listRect.width - 16f,
                Mathf.Max(ItemHeight, model.Results.Count * ItemHeight));
            scroll = GUI.BeginScrollView(listRect, scroll, viewRect);
            for (int i = 0; i < model.Results.Count; i++) DrawEntry(viewRect, i, model.Results[i]);
            GUI.EndScrollView();
            if (model.Results.Count == 0)
                GUI.Label(new Rect(listRect.x + 8f, listRect.y + 5f, listRect.width - 16f, ItemHeight),
                    "No matching columns", EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawEntry(Rect viewRect, int index, CsvGoToColumnEntry entry)
        {
            Rect row = new Rect(0f, index * ItemHeight, viewRect.width, ItemHeight);
            Event current = Event.current;
            bool hovered = row.Contains(current.mousePosition);
            if (hovered && (current.type == EventType.MouseMove || current.type == EventType.MouseDrag))
                model.SelectIndex(index);
            if (current.type == EventType.Repaint && model.SelectedIndex == index)
                EditorGUI.DrawRect(row, new Color(0.22f, 0.38f, 0.58f, 0.45f));
            GUI.Label(new Rect(row.x + 8f, row.y + 2f, row.width - 16f, 16f),
                entry.DisplayName + (entry.RawHeader == entry.DisplayName ? "" : "  [" + entry.HeaderLabel + "]"),
                EditorStyles.boldLabel);
            string match = string.IsNullOrEmpty(entry.MatchLabel) ? string.Empty : entry.MatchLabel + "  \u2022  ";
            string details = match + entry.GroupName + "  \u2022  " +
                (string.IsNullOrEmpty(entry.ValuePreview) ? "(empty)" : entry.ValuePreview);
            GUI.Label(new Rect(row.x + 8f, row.y + 17f, row.width - 16f, 15f), details,
                EditorStyles.miniLabel);
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
            switch (current.keyCode)
            {
                case KeyCode.DownArrow: model.MoveSelection(1); EnsureVisible(true); current.Use(); break;
                case KeyCode.UpArrow: model.MoveSelection(-1); EnsureVisible(true); current.Use(); break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter: Commit(); current.Use(); break;
                case KeyCode.Escape: editorWindow.Close(); current.Use(); break;
            }
        }

        private void EnsureVisible(bool repaint)
        {
            float y = model.SelectedIndex * ItemHeight;
            if (y < scroll.y) scroll.y = y;
            if (y + ItemHeight > scroll.y + MaxListHeight) scroll.y = y + ItemHeight - MaxListHeight;
            if (repaint && editorWindow != null) editorWindow.Repaint();
        }

        private void Commit()
        {
            int index;
            if (!model.TryGetSelectedPhysicalColumn(out index)) return;
            editorWindow.Close();
            if (onSelect != null) onSelect(index);
        }
    }
}
