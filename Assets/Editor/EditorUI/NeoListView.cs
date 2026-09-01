using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Cached ReorderableList wrapper: lists are built once per (SerializedObject, propertyPath)
    /// and reused every frame — building a ReorderableList per OnGUI pass is the classic IMGUI
    /// inspector perf mistake. Elements draw through their PropertyDrawers, so dropdown-enhanced
    /// types (ids, triggers, view refs) automatically render with their custom UI inside lists.
    /// </summary>
    public static class NeoListView
    {
        private static readonly ConditionalWeakTable<SerializedObject, Dictionary<string, ReorderableList>> Cache =
            new ConditionalWeakTable<SerializedObject, Dictionary<string, ReorderableList>>();

        private const float CardPadding = 6f;
        private const float CardAccentWidth = 2f;
        private const float CardGap = 2f;

        /// <summary> Draws a reorderable list for an array/list property with a titled header. </summary>
        public static void Draw(SerializedProperty listProperty, string title = null)
        {
            Get(listProperty, title ?? listProperty.displayName).DoLayoutList();
        }

        /// <summary>
        /// Draws a reorderable list whose elements are <see cref="NeoForm"/> forms inside a padded
        /// card (subtle background + separator + optional family-accent strip). This is the standard
        /// treatment for multi-row list elements — one <paramref name="rows"/> method describes the
        /// element and the card's height/draw both derive from it, so they can never disagree.
        /// </summary>
        public static void DrawForm(SerializedProperty listProperty, string title,
            Action<NeoForm, SerializedProperty> rows, Color accent = default)
        {
            GetForm(listProperty, title, rows, accent).DoLayoutList();
        }

        /// <summary>
        /// Cached card-style form list (see <see cref="DrawForm"/>). Like <see cref="Get"/>, the
        /// first caller's <paramref name="rows"/>/<paramref name="accent"/> win for the lifetime of
        /// the cached list.
        /// </summary>
        public static ReorderableList GetForm(SerializedProperty listProperty, string title,
            Action<NeoForm, SerializedProperty> rows, Color accent = default)
        {
            return Get(listProperty, title, list =>
            {
                list.elementHeightCallback = index =>
                {
                    if (index >= list.serializedProperty.arraySize) return EditorGUIUtility.singleLineHeight;
                    SerializedProperty element = list.serializedProperty.GetArrayElementAtIndex(index);
                    return NeoForm.Measure(element, rows) + (CardPadding + CardGap) * 2f;
                };
                list.drawElementCallback = (rect, index, active, focused) =>
                {
                    if (index >= list.serializedProperty.arraySize) return;
                    SerializedProperty element = list.serializedProperty.GetArrayElementAtIndex(index);
                    var card = new Rect(rect.x, rect.y + CardGap, rect.width, rect.height - CardGap * 2f);
                    if (Event.current.type == EventType.Repaint)
                    {
                        EditorGUI.DrawRect(card, NeoColors.SectionBackground);
                        EditorGUI.DrawRect(new Rect(card.x, card.yMax - 1f, card.width, 1f), NeoColors.Separator);
                        if (accent.a > 0f)
                            EditorGUI.DrawRect(new Rect(card.x, card.y, CardAccentWidth, card.height),
                                accent.WithAlpha(0.55f));
                    }
                    var inner = new Rect(card.x + CardAccentWidth + CardPadding, card.y + CardPadding,
                        card.width - CardAccentWidth - CardPadding * 2f, card.height - CardPadding * 2f);
                    NeoForm.Draw(inner, element, rows);
                };
            });
        }

        /// <summary>
        /// Cached list for an array/list property. <paramref name="configure"/> runs ONCE, when the
        /// list is first built — the supported way to swap in custom element callbacks without
        /// per-frame reassignment (the old pattern was a ConditionalWeakTable guard at the call site).
        /// </summary>
        public static ReorderableList Get(SerializedProperty listProperty, string title,
            Action<ReorderableList> configure = null)
        {
            SerializedObject serializedObject = listProperty.serializedObject;
            Dictionary<string, ReorderableList> byPath = Cache.GetOrCreateValue(serializedObject);
            string path = listProperty.propertyPath;

            if (byPath.TryGetValue(path, out ReorderableList cached)) return cached;

            var list = new ReorderableList(serializedObject, listProperty.Copy(),
                draggable: true, displayHeader: true, displayAddButton: true, displayRemoveButton: true);

            list.drawHeaderCallback = rect =>
            {
                GUI.Label(rect, title, EditorStyles.boldLabel);
                var countRect = new Rect(rect.xMax - 40f, rect.y, 40f, rect.height);
                GUI.Label(countRect, list.serializedProperty.arraySize.ToString(), NeoStyles.MiniDim);
            };

            list.elementHeightCallback = index =>
            {
                if (index >= list.serializedProperty.arraySize) return EditorGUIUtility.singleLineHeight;
                SerializedProperty element = list.serializedProperty.GetArrayElementAtIndex(index);
                return EditorGUI.GetPropertyHeight(element, includeChildren: true) + 4f;
            };

            list.drawElementCallback = (rect, index, active, focused) =>
            {
                if (index >= list.serializedProperty.arraySize) return;
                SerializedProperty element = list.serializedProperty.GetArrayElementAtIndex(index);
                rect.y += 2f;
                rect.height -= 4f;
                EditorGUI.PropertyField(rect, element, includeChildren: true);
            };

            configure?.Invoke(list);
            byPath[path] = list;
            return list;
        }
    }
}
