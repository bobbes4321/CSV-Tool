using System;
using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Minimal anchored one-field entry popup: a title, a focused text field with a dim hint of the
    /// expected format, Enter (or the accent button) commits, Escape closes — the non-modal
    /// counterpart to a text prompt dialog, for one-shot affordances like the id quick-add next to
    /// category/name dropdowns. The commit callback runs after the popup closes; the value is
    /// trimmed and an empty commit is dropped.
    /// </summary>
    public class NeoInputPopup : PopupWindowContent
    {
        private const float Pad = 6f;
        private const float TitleHeight = 16f;
        private const float FieldHeight = 18f;
        private const float FooterHeight = 20f;

        private readonly string _title;
        private readonly string _hint;
        private readonly Action<string> _onCommit;
        private readonly float _width;
        private string _value;
        private bool _focusPending = true;

        /// <summary>
        /// Opens the popup under <paramref name="activatorRect"/>. <paramref name="hint"/> shows dim
        /// inside the empty field (e.g. a format like "Name or Category/Name").
        /// </summary>
        public static void Show(Rect activatorRect, string title, string hint, Action<string> onCommit,
            string initialValue = "")
        {
            PopupWindow.Show(activatorRect,
                new NeoInputPopup(title, hint, onCommit, Mathf.Max(activatorRect.width, 260f), initialValue));
        }

        private NeoInputPopup(string title, string hint, Action<string> onCommit, float width, string initialValue)
        {
            _title = title;
            _hint = hint;
            _onCommit = onCommit;
            _width = width;
            _value = initialValue ?? "";
        }

        public override Vector2 GetWindowSize() =>
            new Vector2(_width, Pad + TitleHeight + FieldHeight + 4f + FooterHeight + Pad);

        public override void OnGUI(Rect rect)
        {
            HandleKeyboard();

            var titleRect = new Rect(rect.x + Pad, rect.y + Pad, rect.width - Pad * 2f, TitleHeight);
            GUI.Label(titleRect, _title, EditorStyles.boldLabel);

            var fieldRect = new Rect(rect.x + Pad, titleRect.yMax, rect.width - Pad * 2f, FieldHeight);
            GUI.SetNextControlName("NeoInputPopupField");
            _value = EditorGUI.TextField(fieldRect, _value);
            if (string.IsNullOrEmpty(_value) && !string.IsNullOrEmpty(_hint) &&
                Event.current.type == EventType.Repaint)
            {
                var hintRect = new Rect(fieldRect.x + 3f, fieldRect.y, fieldRect.width - 3f, fieldRect.height);
                GUI.Label(hintRect, _hint, NeoStyles.MiniDim);
            }
            if (_focusPending)
            {
                EditorGUI.FocusTextInControl("NeoInputPopupField");
                _focusPending = false;
            }

            var addRect = new Rect(rect.xMax - Pad - 60f, fieldRect.yMax + 4f, 60f, FooterHeight - 2f);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_value)))
                if (GUI.Button(addRect, "Add", EditorStyles.miniButton))
                    Commit();
        }

        private void HandleKeyboard()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown) return;
            switch (current.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    current.Use();
                    Commit();
                    break;
                case KeyCode.Escape:
                    current.Use();
                    editorWindow.Close();
                    break;
            }
        }

        private void Commit()
        {
            string value = (_value ?? "").Trim();
            editorWindow.Close();
            if (value.Length > 0) _onCommit?.Invoke(value);
        }
    }
}
