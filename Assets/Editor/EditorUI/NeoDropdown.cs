using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Dropdown controls bound to string SerializedProperties, backed by <see cref="NeoSearchablePopup"/>.
    /// Option lists are produced by a provider that runs only when the dropdown is clicked — drawing
    /// a closed dropdown costs one button. Selection survives the popup outliving the IMGUI pass by
    /// re-resolving the property from (serializedObject, propertyPath).
    /// </summary>
    public static class NeoDropdown
    {
        private static readonly GUIContent TempContent = new GUIContent();

        /// <summary>
        /// Searchable string dropdown. <paramref name="onAddNew"/> being non-null enables the inline
        /// "+ Add" row; it should register the new value wherever the options come from (the property
        /// is set to the new value automatically). <paramref name="optionSwatch"/> being non-null draws
        /// a color swatch per option in the popup (color-token pickers).
        /// </summary>
        public static void StringPopup(Rect rect, SerializedProperty property,
            Func<List<string>> optionsProvider, string emptyLabel = "None", Action<string> onAddNew = null,
            Func<string, Color?> optionSwatch = null)
        {
            string current = property.stringValue;
            TempContent.text = string.IsNullOrEmpty(current) ? emptyLabel : current;
            TempContent.tooltip = null;

            if (!EditorGUI.DropdownButton(rect, TempContent, FocusType.Keyboard, EditorStyles.popup)) return;

            SerializedObject serializedObject = property.serializedObject;
            string path = property.propertyPath;

            void Apply(string value)
            {
                // the popup outlives the IMGUI pass — the inspector (and its SerializedObject) may
                // be gone by the time the user picks a value
                try
                {
                    if (serializedObject.targetObject == null) return;
                    serializedObject.Update();
                    SerializedProperty resolved = serializedObject.FindProperty(path);
                    if (resolved == null) return;
                    resolved.stringValue = value;
                    serializedObject.ApplyModifiedProperties();
                }
                catch (Exception)
                {
                    // disposed SerializedObject — selection changed while the popup was open
                }
            }

            NeoSearchablePopup.Show(rect, current, optionsProvider?.Invoke() ?? new List<string>(),
                Apply,
                onAddNew == null
                    ? (Action<string>)null
                    : value =>
                    {
                        onAddNew(value);
                        Apply(value);
                    },
                optionSwatch);
        }

        /// <summary>
        /// Searchable dropdown over an arbitrary value (no SerializedProperty) — for editor windows
        /// and toolbars. Returns true when the button was clicked (popup opened).
        /// </summary>
        public static bool ValuePopup(Rect rect, string current, Func<List<string>> optionsProvider,
            Action<string> onSelect, string emptyLabel = "None", Action<string> onAddNew = null,
            Func<string, Color?> optionSwatch = null)
        {
            TempContent.text = string.IsNullOrEmpty(current) ? emptyLabel : current;
            TempContent.tooltip = null;
            if (!EditorGUI.DropdownButton(rect, TempContent, FocusType.Keyboard, EditorStyles.popup)) return false;

            NeoSearchablePopup.Show(rect, current, optionsProvider?.Invoke() ?? new List<string>(),
                onSelect,
                onAddNew == null
                    ? (Action<string>)null
                    : value =>
                    {
                        onAddNew(value);
                        onSelect?.Invoke(value);
                    },
                optionSwatch);
            return true;
        }
    }
}
