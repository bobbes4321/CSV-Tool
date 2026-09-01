using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Searchable dropdown popup: a search field, a filtered scroll list with keyboard navigation
    /// (up/down/enter/escape) and an optional inline "+ Add 'text'" row that creates a new entry
    /// from the search text — no modal dialogs. Options are provided once when the popup opens,
    /// never polled per frame.
    /// </summary>
    public class NeoSearchablePopup : PopupWindowContent
    {
        private const float ItemHeight = 20f;
        private const float SearchHeight = 24f;
        private const float MaxListHeight = 280f;
        private const float SwatchSize = 12f;
        private const float SwatchPadding = 4f;

        private readonly IReadOnlyList<string> _options;
        private readonly string _current;
        private readonly Action<string> _onSelect;
        private readonly Action<string> _onAddNew;
        private readonly Dictionary<string, Color> _swatches; // resolved once on open, never per repaint
        private readonly float _width;

        private readonly List<string> _filtered = new List<string>();
        private string _search = "";
        private Vector2 _scroll;
        private int _highlight;
        private bool _focusPending = true;

        /// <summary>
        /// Opens the popup under <paramref name="activatorRect"/>. <paramref name="onAddNew"/> being
        /// non-null enables the inline add row. <paramref name="optionSwatch"/> being non-null draws a
        /// small color swatch before each option it returns a color for (color-token pickers) — options
        /// it returns null for keep the indentation so the list stays aligned.
        /// </summary>
        public static void Show(Rect activatorRect, string current, IReadOnlyList<string> options,
            Action<string> onSelect, Action<string> onAddNew = null, Func<string, Color?> optionSwatch = null)
        {
            PopupWindow.Show(activatorRect,
                new NeoSearchablePopup(options, current, onSelect, onAddNew, optionSwatch,
                    Mathf.Max(activatorRect.width, 200f)));
        }

        private NeoSearchablePopup(IReadOnlyList<string> options, string current, Action<string> onSelect,
            Action<string> onAddNew, Func<string, Color?> optionSwatch, float width)
        {
            _options = options ?? Array.Empty<string>();
            _current = current;
            _onSelect = onSelect;
            _onAddNew = onAddNew;
            _width = width;
            if (optionSwatch != null)
            {
                _swatches = new Dictionary<string, Color>();
                foreach (string option in _options)
                    if (!string.IsNullOrEmpty(option) && !_swatches.ContainsKey(option) &&
                        optionSwatch(option) is Color swatch)
                        _swatches.Add(option, swatch);
            }
            Filter();
        }

        public override Vector2 GetWindowSize()
        {
            int rows = _filtered.Count + (ShowAddRow() ? 1 : 0);
            float listHeight = Mathf.Clamp(rows * ItemHeight, ItemHeight, MaxListHeight);
            return new Vector2(_width, SearchHeight + listHeight + 6f);
        }

        public override void OnOpen()
        {
            editorWindow.wantsMouseMove = true; // hover highlight without a constant repaint loop
        }

        public override void OnGUI(Rect rect)
        {
            HandleKeyboard();
            DrawSearch(rect);
            DrawList(new Rect(rect.x, rect.y + SearchHeight, rect.width, rect.height - SearchHeight));
            if (Event.current.type == EventType.MouseMove) editorWindow.Repaint();
        }

        private void DrawSearch(Rect rect)
        {
            var searchRect = new Rect(rect.x + 4f, rect.y + 3f, rect.width - 8f, 18f);
            GUI.SetNextControlName("NeoSearch");
            EditorGUI.BeginChangeCheck();
            _search = EditorGUI.TextField(searchRect, _search, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck())
            {
                Filter();
                _highlight = 0;
            }
            if (_focusPending)
            {
                EditorGUI.FocusTextInControl("NeoSearch");
                _focusPending = false;
            }
        }

        private void DrawList(Rect rect)
        {
            int rows = _filtered.Count + (ShowAddRow() ? 1 : 0);
            var viewRect = new Rect(0f, 0f, rect.width - 16f, rows * ItemHeight);
            _scroll = GUI.BeginScrollView(rect, _scroll, viewRect);

            for (int i = 0; i < _filtered.Count; i++)
                DrawRow(new Rect(0f, i * ItemHeight, viewRect.width, ItemHeight), i, _filtered[i], isAddRow: false);

            if (ShowAddRow())
                DrawRow(new Rect(0f, _filtered.Count * ItemHeight, viewRect.width, ItemHeight),
                    _filtered.Count, $"+ Add '{_search.Trim()}'", isAddRow: true);

            GUI.EndScrollView();

            if (rows == 0)
                GUI.Label(rect, "no matches", NeoStyles.PopupSearchHint);
        }

        private void DrawRow(Rect rowRect, int index, string text, bool isAddRow)
        {
            Event current = Event.current;
            bool hovered = rowRect.Contains(current.mousePosition);
            // only mouse movement steals the highlight — otherwise hover would stomp keyboard nav
            if (hovered && (current.type == EventType.MouseMove || current.type == EventType.MouseDrag))
                _highlight = index;

            if (current.type == EventType.Repaint)
            {
                if (index == _highlight)
                    EditorGUI.DrawRect(rowRect, NeoColors.RowSelected);
                else if (!isAddRow && text == _current)
                    EditorGUI.DrawRect(rowRect, NeoColors.RowHover);

                Rect labelRect = rowRect;
                if (_swatches != null)
                {
                    // indent every row (even ones without a color, e.g. a "(none)" sentinel) so
                    // labels stay aligned down the list
                    float inset = SwatchSize + SwatchPadding * 2f;
                    labelRect = new Rect(rowRect.x + inset, rowRect.y, rowRect.width - inset, rowRect.height);
                    if (!isAddRow && _swatches.TryGetValue(text, out Color swatch))
                    {
                        var swatchRect = new Rect(rowRect.x + SwatchPadding,
                            rowRect.y + (rowRect.height - SwatchSize) * 0.5f, SwatchSize, SwatchSize);
                        EditorGUI.DrawRect(new Rect(swatchRect.x - 1f, swatchRect.y - 1f,
                            swatchRect.width + 2f, swatchRect.height + 2f), NeoColors.Separator);
                        EditorGUI.DrawRect(swatchRect, swatch);
                    }
                }

                Color previousColor = GUI.contentColor;
                if (isAddRow) GUI.contentColor = NeoColors.Add;
                NeoStyles.PopupItem.Draw(labelRect, text, isHover: hovered, isActive: false,
                    on: !isAddRow && text == _current, hasKeyboardFocus: false);
                GUI.contentColor = previousColor;
            }

            if (current.type == EventType.MouseDown && hovered)
            {
                Commit(index);
                current.Use();
            }
        }

        private void HandleKeyboard()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown) return;

            int rows = _filtered.Count + (ShowAddRow() ? 1 : 0);
            switch (current.keyCode)
            {
                case KeyCode.DownArrow:
                    _highlight = rows == 0 ? 0 : (_highlight + 1) % rows;
                    EnsureVisible();
                    current.Use();
                    editorWindow.Repaint();
                    break;
                case KeyCode.UpArrow:
                    _highlight = rows == 0 ? 0 : (_highlight - 1 + rows) % rows;
                    EnsureVisible();
                    current.Use();
                    editorWindow.Repaint();
                    break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (rows > 0) Commit(Mathf.Clamp(_highlight, 0, rows - 1));
                    current.Use();
                    break;
                case KeyCode.Escape:
                    editorWindow.Close();
                    current.Use();
                    break;
            }
        }

        private void EnsureVisible()
        {
            float y = _highlight * ItemHeight;
            if (y < _scroll.y) _scroll.y = y;
            float viewBottom = _scroll.y + MaxListHeight - ItemHeight;
            if (y > viewBottom) _scroll.y = y - MaxListHeight + ItemHeight * 2f;
        }

        private void Commit(int index)
        {
            if (index >= 0 && index < _filtered.Count)
                _onSelect?.Invoke(_filtered[index]);
            else if (ShowAddRow())
                _onAddNew?.Invoke(_search.Trim());
            editorWindow.Close();
        }

        private bool ShowAddRow()
        {
            if (_onAddNew == null) return false;
            string trimmed = _search.Trim();
            if (string.IsNullOrEmpty(trimmed)) return false;
            for (int i = 0; i < _filtered.Count; i++)
                if (string.Equals(_filtered[i], trimmed, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        private void Filter()
        {
            _filtered.Clear();
            for (int i = 0; i < _options.Count; i++)
            {
                string option = _options[i];
                if (string.IsNullOrEmpty(option)) continue;
                if (_search.Length == 0 || option.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0)
                    _filtered.Add(option);
            }
        }
    }
}
