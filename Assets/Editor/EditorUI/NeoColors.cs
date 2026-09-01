using UnityEditor;
using UnityEngine;

namespace Neo.EditorUI
{
    /// <summary>
    /// Semantic color palette for editor tooling, skin-aware (pro/light). Colors are grouped the
    /// Doozy way — one accent per component family — but resolved as plain constants: no
    /// ScriptableObject palettes, no generated lookups, no load cost.
    /// </summary>
    public static class NeoColors
    {
        private static bool Dark => EditorGUIUtility.isProSkin;

        // ------------------------------------------------------------------ family accents

        /// <summary> Blue — interactive components (buttons, toggles, sliders). </summary>
        public static Color Interactive => Dark ? FromHex(0x4A9EFF) : FromHex(0x1B6FD4);

        /// <summary> Cyan — containers (views, popups, tooltips). </summary>
        public static Color Containers => Dark ? FromHex(0x41C9E2) : FromHex(0x0E8FA8);

        /// <summary> Orange — animation and animators. </summary>
        public static Color Animation => Dark ? FromHex(0xFFA94D) : FromHex(0xD9730D);

        /// <summary> Purple — flow graphs and controllers. </summary>
        public static Color Flow => Dark ? FromHex(0xB197FC) : FromHex(0x7048E8);

        /// <summary> Pink — theming. </summary>
        public static Color Theming => Dark ? FromHex(0xF783AC) : FromHex(0xD6336C);

        /// <summary> Teal — signals and streams. </summary>
        public static Color Signals => Dark ? FromHex(0x63E6BE) : FromHex(0x099268);

        /// <summary> Yellow — data assets (databases, settings). </summary>
        public static Color Data => Dark ? FromHex(0xFFD43B) : FromHex(0xB08D0B);

        /// <summary> Lime — rendering primitives (shapes, gradients, effects). </summary>
        public static Color Rendering => Dark ? FromHex(0xA9E34B) : FromHex(0x66A80F);

        // ------------------------------------------------------------------ intent colors

        public static Color Add => Dark ? FromHex(0x5FD068) : FromHex(0x2E933C);
        public static Color Remove => Dark ? FromHex(0xFF6B6B) : FromHex(0xC73E3E);
        public static Color Warning => Dark ? FromHex(0xFFC078) : FromHex(0xB35C00);

        // ------------------------------------------------------------------ chrome

        public static Color TextTitle => Dark ? new Color(0.92f, 0.92f, 0.92f) : new Color(0.1f, 0.1f, 0.1f);
        public static Color TextSubtle => Dark ? new Color(0.65f, 0.65f, 0.65f) : new Color(0.35f, 0.35f, 0.35f);
        public static Color TextDim => Dark ? new Color(0.5f, 0.5f, 0.5f) : new Color(0.45f, 0.45f, 0.45f);

        public static Color HeaderBackground => Dark ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.82f, 0.82f, 0.82f);
        public static Color SectionBackground => Dark ? new Color(1f, 1f, 1f, 0.03f) : new Color(0f, 0f, 0f, 0.03f);
        public static Color Separator => Dark ? new Color(0f, 0f, 0f, 0.4f) : new Color(0f, 0f, 0f, 0.15f);
        public static Color RowHover => Dark ? new Color(1f, 1f, 1f, 0.06f) : new Color(0f, 0f, 0f, 0.06f);
        public static Color RowSelected => Dark ? new Color(0.24f, 0.42f, 0.69f, 0.6f) : new Color(0.24f, 0.49f, 0.91f, 0.35f);

        // ------------------------------------------------------------------ CSV grid

        // The grid uses opaque, skin-specific surfaces instead of stacking translucent fills.
        // This keeps text contrast predictable when selection, crosshair, and frozen panes meet.
        // The interaction model follows spreadsheet conventions: quiet neutral gridlines, a
        // light selection fill, and a stronger active-cell border/fill.
        public static Color GridBackground => Dark ? FromHex(0x24282D) : FromHex(0xFAFBFD);
        public static Color GridHeaderBackground => Dark ? FromHex(0x30363D) : FromHex(0xE8EDF3);
        public static Color GridRowNumberBackground => Dark ? FromHex(0x2A2F35) : FromHex(0xF1F4F7);
        public static Color GridCellText => Dark ? FromHex(0xE6EDF3) : FromHex(0x242A31);
        public static Color GridHeaderText => Dark ? FromHex(0xF0F3F6) : FromHex(0x26313C);
        public static Color GridRowNumberText => Dark ? FromHex(0xB9C3CE) : FromHex(0x5C6875);
        public static Color GridLine => Dark ? FromHex(0x4C5661) : FromHex(0xBCC6D1);
        public static Color GridCrosshairFill => Dark ? FromHex(0x2C333B) : FromHex(0xEAF2F9);
        public static Color GridSelectionFill => Dark ? FromHex(0x254B73) : FromHex(0xD6E8FC);
        public static Color GridSelectionFillStrong => Dark ? FromHex(0x2F6FA4) : FromHex(0xBCD9F7);
        public static Color GridSelectionBorder => Dark ? FromHex(0xA0D5FF) : FromHex(0x1264A3);

        // ------------------------------------------------------------------ helpers

        public static Color WithAlpha(this Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        private static Color FromHex(int rgb) => new Color(
            ((rgb >> 16) & 0xFF) / 255f,
            ((rgb >> 8) & 0xFF) / 255f,
            (rgb & 0xFF) / 255f);
    }
}
