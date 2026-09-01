using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Lazily created, cached GUIStyles for the editor kit. Styles are built once on first GUI
    /// access and reused forever — never allocate styles per frame.
    /// </summary>
    public static class NeoStyles
    {
        private static GUIStyle s_headerTitle;
        private static GUIStyle s_headerSubtitle;
        private static GUIStyle s_sectionTitle;
        private static GUIStyle s_miniDim;
        private static GUIStyle s_badge;
        private static GUIStyle s_popupItem;
        private static GUIStyle s_popupSearchHint;
        private static GUIStyle s_centeredBold;

        public static GUIStyle HeaderTitle =>
            s_headerTitle ?? (s_headerTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = NeoColors.TextTitle }
            });

        public static GUIStyle HeaderSubtitle =>
            s_headerSubtitle ?? (s_headerSubtitle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = NeoColors.TextDim }
            });

        public static GUIStyle SectionTitle =>
            s_sectionTitle ?? (s_sectionTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11,
                normal = { textColor = NeoColors.TextSubtle }
            });

        public static GUIStyle MiniDim =>
            s_miniDim ?? (s_miniDim = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = NeoColors.TextDim }
            });

        public static GUIStyle Badge =>
            s_badge ?? (s_badge = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 9,
                normal = { textColor = NeoColors.TextSubtle }
            });

        public static GUIStyle PopupItem =>
            s_popupItem ?? (s_popupItem = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 0, 0),
                fixedHeight = 20f
            });

        public static GUIStyle PopupSearchHint =>
            s_popupSearchHint ?? (s_popupSearchHint = new GUIStyle(EditorStyles.centeredGreyMiniLabel));

        public static GUIStyle CenteredBold =>
            s_centeredBold ?? (s_centeredBold = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter
            });
    }
}
