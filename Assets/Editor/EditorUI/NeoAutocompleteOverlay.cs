using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Shared non-modal autocomplete surface for IMGUI editor views. The caller owns values and
    /// edit state; this class keeps the popup's hit testing and Neo presentation consistent.
    /// </summary>
    public static class NeoAutocompleteOverlay
    {
        public const int MaxVisibleSuggestions = 8;
        public const float DefaultWidth = 280f;

        public static bool HandleInput(Event currentEvent, Rect rect, int suggestionCount,
            float rowHeight, ref int selection, Action acceptSelection, Action repaintRequested)
        {
            if (currentEvent == null || suggestionCount <= 0 || rowHeight <= 0f ||
                !rect.Contains(currentEvent.mousePosition)) return false;

            int visibleCount = Mathf.Min(MaxVisibleSuggestions, suggestionCount);
            int index = Mathf.Clamp(Mathf.FloorToInt((currentEvent.mousePosition.y - rect.y) / rowHeight),
                0, visibleCount - 1);
            if (currentEvent.type == EventType.MouseMove)
            {
                if (selection != index)
                {
                    selection = index;
                    repaintRequested?.Invoke();
                }
                return true;
            }

            if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0)
            {
                selection = index;
                acceptSelection?.Invoke();
                currentEvent.Use();
                return true;
            }
            return false;
        }

        public static void Draw(Rect rect, IReadOnlyList<string> suggestions, int selection, float rowHeight)
        {
            if (Event.current.type != EventType.Repaint || suggestions == null || suggestions.Count == 0 ||
                rowHeight <= 0f) return;

            int visibleCount = Mathf.Min(MaxVisibleSuggestions, suggestions.Count);
            rect.height = visibleCount * rowHeight;
            EditorGUI.DrawRect(rect, NeoColors.GridBackground);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), NeoColors.GridSelectionBorder);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), NeoColors.GridSelectionBorder);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), NeoColors.GridSelectionBorder);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), NeoColors.GridSelectionBorder);

            for (int i = 0; i < visibleCount; i++)
            {
                Rect item = new Rect(rect.x, rect.y + i * rowHeight, rect.width, rowHeight);
                if (i == selection) EditorGUI.DrawRect(item, NeoColors.GridSelectionFillStrong);
                GUI.Label(item, suggestions[i], NeoStyles.AutocompleteItem);
            }
        }
    }
}
