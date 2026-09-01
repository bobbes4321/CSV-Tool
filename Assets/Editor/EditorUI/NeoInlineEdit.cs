using System;
using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Hierarchy-style single inline text-field editor for tree/list rows (rename, add-in-place).
    /// One edit is in flight at a time, keyed by a caller-chosen string so rows can ask
    /// "<see cref="IsEditing"/> me?". The caller owns the buffer/key; this is pure drawing + commit
    /// semantics: <b>Enter</b> commits, <b>Esc</b> cancels, focus-loss commits. Engine-only — no Neo.UI
    /// reference — so any Neo editor window can reuse the same rename interaction.
    /// </summary>
    public static class NeoInlineEdit
    {
        private const string ControlName = "NeoInlineEditField";
        private static bool s_focusPending;

        /// <summary>
        /// The result of one inline-edit frame. <see cref="Committed"/>/<see cref="Cancelled"/> are true
        /// only on the frame the edit ends; <see cref="Text"/> carries the (trimmed) committed value.
        /// </summary>
        public readonly struct Result
        {
            public readonly bool Committed;
            public readonly bool Cancelled;
            public readonly string Text;

            public Result(bool committed, bool cancelled, string text)
            {
                Committed = committed;
                Cancelled = cancelled;
                Text = text;
            }

            public bool Ended => Committed || Cancelled;
        }

        /// <summary>
        /// Call once when starting an edit so the field grabs keyboard focus on its next draw. The caller
        /// stores the key + seed buffer; pass them back into <see cref="Field"/> every frame after.
        /// </summary>
        public static void RequestFocus() => s_focusPending = true;

        /// <summary>
        /// Draws the inline text field into <paramref name="rect"/> and edits <paramref name="buffer"/>
        /// in place. Returns commit/cancel for THIS frame. Enter commits (trimmed), Esc cancels,
        /// click-away commits. The caller decides what committing means (rename, add, …) and must clear
        /// its editing-key when <see cref="Result.Ended"/>.
        /// </summary>
        public static Result Field(Rect rect, ref string buffer, GUIStyle style = null)
        {
            buffer ??= string.Empty;
            Event e = Event.current;

            bool enter = e.type == EventType.KeyDown &&
                         (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            bool escape = e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape;

            GUI.SetNextControlName(ControlName);
            string next = style != null
                ? EditorGUI.TextField(rect, buffer, style)
                : EditorGUI.TextField(rect, buffer);
            buffer = next;

            if (s_focusPending && e.type == EventType.Repaint)
            {
                EditorGUI.FocusTextInControl(ControlName);
                s_focusPending = false;
            }

            if (escape)
            {
                e.Use();
                return new Result(false, true, buffer);
            }
            if (enter)
            {
                e.Use();
                return new Result(true, false, (buffer ?? string.Empty).Trim());
            }

            // click-away (mouse down outside the field while it owns focus) commits, matching the
            // Hierarchy. Guard on the field still being the focused control so a row click elsewhere in
            // the window doesn't silently swallow an intentional cancel.
            if (e.type == EventType.MouseDown && !rect.Contains(e.mousePosition)
                && GUI.GetNameOfFocusedControl() == ControlName)
                return new Result(true, false, (buffer ?? string.Empty).Trim());

            return new Result(false, false, buffer);
        }
    }
}
