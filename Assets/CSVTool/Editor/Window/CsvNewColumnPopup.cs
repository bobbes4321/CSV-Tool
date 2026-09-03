using System;
using UnityEditor;
using UnityEngine;

namespace CsvTool.Editor
{
    /// <summary>Small keyboard-first prompt used before a structural column insertion.</summary>
    internal sealed class CsvNewColumnPopup : PopupWindowContent
    {
        private readonly Action<string> onConfirm;
        private string header = "New Column";
        private bool focus;

        public CsvNewColumnPopup(Action<string> onConfirm) { this.onConfirm = onConfirm; }
        public override Vector2 GetWindowSize() { return new Vector2(260f, 72f); }
        public override void OnGUI(Rect rect)
        {
            GUI.Label(new Rect(8f, 6f, rect.width - 16f, 18f), "New column header", EditorStyles.boldLabel);
            GUI.SetNextControlName("CsvNewColumnHeader");
            header = GUI.TextField(new Rect(8f, 26f, rect.width - 78f, 20f), header ?? string.Empty);
            if (!focus) { EditorGUI.FocusTextInControl("CsvNewColumnHeader"); focus = true; }
            if (GUI.Button(new Rect(rect.width - 64f, 26f, 56f, 20f), "Insert") ||
                (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
            {
                if (!string.IsNullOrWhiteSpace(header)) onConfirm?.Invoke(header);
                editorWindow.Close();
            }
        }
    }
}
