using System;
using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Single-source form layout for rect-based IMGUI (ReorderableList elements, property drawers):
    /// ONE method describes the rows, and it runs in two modes — measure (accumulates the total
    /// height for an elementHeightCallback / GetPropertyHeight) and draw (lays out real rects).
    /// Height and drawing can never disagree because there is only one description — the classic
    /// "height callback must arithmetically mirror the draw callback" drift bug is structurally
    /// impossible. Standard gaps live here so every complex inspector spaces the same way.
    /// In measure mode row callbacks are NOT invoked (there is nothing to draw into), so a row's
    /// presence/height may depend on data but must not depend on what a previous row drew.
    /// </summary>
    public sealed class NeoForm
    {
        /// <summary> Vertical breathing room between logical field groups. </summary>
        public const float SectionGap = 6f;

        private bool _measuring;
        private float _x;
        private float _yOrigin;
        private float _width;
        private float _cursor;

        private static float RowSpacing => EditorGUIUtility.standardVerticalSpacing;

        private NeoForm() { }

        /// <summary> True while accumulating height — row callbacks are skipped in this mode. </summary>
        public bool IsMeasuring => _measuring;

        /// <summary> Total height of the described rows (context overload — avoids a per-call closure). </summary>
        public static float Measure<TContext>(TContext context, Action<NeoForm, TContext> rows)
        {
            var form = new NeoForm { _measuring = true };
            rows(form, context);
            return Mathf.Max(0f, form._cursor - RowSpacing);
        }

        /// <summary> Draws the described rows top-down inside <paramref name="rect"/>. </summary>
        public static void Draw<TContext>(Rect rect, TContext context, Action<NeoForm, TContext> rows)
        {
            var form = new NeoForm { _x = rect.x, _yOrigin = rect.y, _width = rect.width };
            rows(form, context);
        }

        public static float Measure(Action<NeoForm> rows) => Measure(rows, (form, r) => r(form));

        public static void Draw(Rect rect, Action<NeoForm> rows) => Draw(rect, rows, (form, r) => r(form));

        // ------------------------------------------------------------------ rows

        /// <summary> A single-line row with custom drawing (splits, popups, prefix labels…). </summary>
        public void Line(Action<Rect> draw) => Row(EditorGUIUtility.singleLineHeight, draw);

        /// <summary> A fixed-height row with custom drawing. </summary>
        public void Row(float height, Action<Rect> draw)
        {
            Rect rect = Advance(height);
            if (!_measuring) draw(rect);
        }

        /// <summary> Breathing room between logical field groups (no field). </summary>
        public void Gap(float height = SectionGap) => _cursor += height;

        /// <summary>
        /// A full-width property field whose height comes from
        /// <see cref="EditorGUI.GetPropertyHeight(SerializedProperty, bool)"/> — expandable
        /// children (arrays, foldouts) size themselves in BOTH modes automatically.
        /// </summary>
        public void Field(SerializedProperty property, string label = null)
        {
            float height = EditorGUI.GetPropertyHeight(property, includeChildren: true);
            Rect rect = Advance(height);
            if (!_measuring)
                EditorGUI.PropertyField(rect, property,
                    label == null ? null : new GUIContent(label), includeChildren: true);
        }

        /// <summary> Two narrow-labeled properties side by side on one line. </summary>
        public void Pair(SerializedProperty left, string leftLabel, float leftLabelWidth,
            SerializedProperty right, string rightLabel, float rightLabelWidth,
            float leftFraction = 0.5f)
        {
            Rect rect = Advance(EditorGUIUtility.singleLineHeight);
            if (_measuring) return;
            NeoGUI.SplitHorizontal(rect, out Rect leftRect, out Rect rightRect, leftFraction);
            NeoGUI.LabeledField(leftRect, left, leftLabel, leftLabelWidth);
            NeoGUI.LabeledField(rightRect, right, rightLabel, rightLabelWidth);
        }

        private Rect Advance(float height)
        {
            Rect rect = _measuring ? default : new Rect(_x, _yOrigin + _cursor, _width, height);
            _cursor += height + RowSpacing;
            return rect;
        }
    }
}
