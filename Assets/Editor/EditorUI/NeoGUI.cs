using System;
using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Layout building blocks for consistent inspectors: component headers with a family accent,
    /// titled sections, persistent foldout sections, persistent tab bars, badges and splitters.
    /// IMGUI with cached styles — drawing is flat and allocation-light, no animated chrome.
    /// </summary>
    public static class NeoGUI
    {
        public const float Spacing = 4f;
        private const float HeaderHeight = 34f;
        private const float AccentStripWidth = 3f;

        private static readonly GUIContent TempContent = new GUIContent();

        private static GUIContent Temp(string text, string tooltip = null)
        {
            TempContent.text = text;
            TempContent.tooltip = tooltip;
            TempContent.image = null;
            return TempContent;
        }

        // ------------------------------------------------------------------ component header

        /// <summary>
        /// Flat component header: accent strip, bold title, dim subtitle. Replaces Doozy's animated
        /// FluidComponentHeader with a zero-cost equivalent.
        /// </summary>
        public static void ComponentHeader(string title, string subtitle, Color accent)
        {
            Rect rect = GUILayoutUtility.GetRect(0f, HeaderHeight, GUILayout.ExpandWidth(true));
            rect.xMin -= 14f; // bleed across the inspector's left margin
            rect.xMax += 4f;

            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, NeoColors.HeaderBackground);
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, AccentStripWidth, rect.height), accent);
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), accent.WithAlpha(0.35f));
            }

            var titleRect = new Rect(rect.x + 14f, rect.y + 3f, rect.width - 20f, 16f);
            var subtitleRect = new Rect(titleRect.x, titleRect.yMax - 1f, titleRect.width, 14f);
            GUI.Label(titleRect, Temp(title), NeoStyles.HeaderTitle);
            if (!string.IsNullOrEmpty(subtitle))
                GUI.Label(subtitleRect, Temp(subtitle), NeoStyles.HeaderSubtitle);

            GUILayout.Space(Spacing);
        }

        // ------------------------------------------------------------------ sections

        /// <summary> Boxed section with a bold title. Use in a using-block. </summary>
        public sealed class SectionScope : GUI.Scope
        {
            public SectionScope(string title)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                if (!string.IsNullOrEmpty(title))
                    GUILayout.Label(Temp(title), NeoStyles.SectionTitle);
            }

            protected override void CloseScope()
            {
                EditorGUILayout.EndVertical();
                GUILayout.Space(Spacing * 0.5f);
            }
        }

        /// <summary>
        /// Collapsible boxed section whose open state persists for the session. Returns true when
        /// expanded; always pair with the using-pattern: contents only when it returns true, and the
        /// box closes itself.
        /// </summary>
        public static bool BeginFoldoutSection(string key, string title, string summary = null, bool defaultOpen = false)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            bool open = SessionState.GetBool(key, defaultOpen);
            Rect rect = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));

            bool newOpen = EditorGUI.Foldout(new Rect(rect.x + 12f, rect.y, rect.width - 12f, rect.height),
                open, Temp(title), toggleOnLabelClick: true, NeoStyles.SectionTitle);
            if (newOpen != open) SessionState.SetBool(key, newOpen);

            if (!newOpen && !string.IsNullOrEmpty(summary))
            {
                var summaryRect = new Rect(rect.x + rect.width * 0.45f, rect.y + 1f, rect.width * 0.55f, rect.height);
                GUI.Label(summaryRect, Temp(summary), NeoStyles.MiniDim);
            }
            return newOpen;
        }

        public static void EndFoldoutSection()
        {
            EditorGUILayout.EndVertical();
            GUILayout.Space(Spacing * 0.5f);
        }

        // ------------------------------------------------------------------ tabs

        /// <summary> Toolbar tab bar whose selected index persists for the session. </summary>
        public static int Tabs(string key, string[] labels)
        {
            int index = Mathf.Clamp(SessionState.GetInt(key, 0), 0, labels.Length - 1);
            int newIndex = GUILayout.Toolbar(index, labels, EditorStyles.toolbarButton);
            if (newIndex != index) SessionState.SetInt(key, newIndex);
            GUILayout.Space(Spacing * 0.5f);
            return newIndex;
        }

        // ------------------------------------------------------------------ small pieces

        public static void Splitter()
        {
            Rect rect = GUILayoutUtility.GetRect(0f, 1f, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, NeoColors.Separator);
        }

        /// <summary> Inline colored pill, e.g. a state badge. </summary>
        public static void Badge(string text, Color color)
        {
            Vector2 size = NeoStyles.Badge.CalcSize(Temp(text));
            Rect rect = GUILayoutUtility.GetRect(size.x + 12f, 16f, GUILayout.ExpandWidth(false));
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, color.WithAlpha(0.18f));
                EditorGUI.DrawRect(new Rect(rect.x, rect.y, 2f, rect.height), color);
            }
            GUI.Label(rect, Temp(text), NeoStyles.Badge);
        }

        /// <summary> Full-width button tinted with an accent color. </summary>
        public static bool AccentButton(string label, Color accent, float height = 24f)
        {
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = accent;
            bool pressed = GUILayout.Button(Temp(label), GUILayout.Height(height));
            GUI.backgroundColor = previous;
            return pressed;
        }

        // ------------------------------------------------------------------ card grids

        /// <summary>
        /// Px a card grid inside a vertical-scrolling view should reserve for the scrollbar + scroll-view
        /// padding when deriving its available width from a KNOWN outer width (the window's
        /// <c>position.width</c>, a split pane's column width). Deliberately a constant, never a rect
        /// measured inside the scroll view: an inner measurement gets inflated by the grid's own
        /// fixed-width rows, so it can grow with the window but never shrink back (a one-way ratchet).
        /// Reserving it unconditionally costs nothing visible — <see cref="FitColumns"/> stretches the
        /// cards into whatever width it is given.
        /// </summary>
        public const float ScrollbarAllowance = 15f;

        /// <summary>
        /// Fits a card grid to <paramref name="availableWidth"/>: as many <paramref name="baseCardWidth"/>
        /// columns as fit, with the leftover width distributed INTO the cards so every row fills the
        /// available width exactly — never dead space at the right edge. <paramref name="cardWidth"/> is
        /// therefore ≥ the base width, except in a too-narrow single column where the lone card shrinks
        /// to fit instead of clipping.
        /// </summary>
        public static int FitColumns(float availableWidth, float baseCardWidth, float gap, out float cardWidth)
        {
            int columns = Mathf.Max(1, Mathf.FloorToInt((availableWidth + gap) / (baseCardWidth + gap)));
            cardWidth = Mathf.Max(40f, (availableWidth - (columns - 1) * gap) / columns);
            return columns;
        }

        // ------------------------------------------------------------------ property helpers

        /// <summary> Draws every visible property except m_Script and any excluded names. </summary>
        public static void DrawProperties(SerializedObject serializedObject, params string[] exclude)
        {
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;
                if (IsExcluded(iterator.name, exclude)) continue;
                EditorGUILayout.PropertyField(iterator, includeChildren: true);
            }
        }

        private static bool IsExcluded(string name, string[] exclude)
        {
            for (int i = 0; i < exclude.Length; i++)
                if (string.Equals(name, exclude[i], StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>
        /// PropertyField with a temporarily narrowed label — for half-width paired fields where the
        /// default ~150px label width would leave almost no room for the value itself.
        /// </summary>
        public static void LabeledField(Rect rect, SerializedProperty property, string label, float labelWidth)
        {
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = labelWidth;
            EditorGUI.PropertyField(rect, property, Temp(label));
            EditorGUIUtility.labelWidth = previousLabelWidth;
        }

        // ------------------------------------------------------------------ rect helpers

        /// <summary> Splits a rect into two horizontal halves with a small gutter. </summary>
        public static void SplitHorizontal(Rect rect, out Rect left, out Rect right, float leftFraction = 0.5f, float gutter = 2f)
        {
            float leftWidth = rect.width * leftFraction - gutter * 0.5f;
            left = new Rect(rect.x, rect.y, leftWidth, rect.height);
            right = new Rect(rect.x + leftWidth + gutter, rect.y, rect.width - leftWidth - gutter, rect.height);
        }

        public static Rect NextLine(ref Rect line)
        {
            Rect current = line;
            line.y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            return current;
        }
    }
}
