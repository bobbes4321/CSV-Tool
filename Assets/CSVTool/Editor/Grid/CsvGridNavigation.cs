using UnityEngine;

namespace CsvTool.Editor
{
    public enum CsvGridRevealMode
    {
        Preserve = 0,
        Minimal = 1,
        Center = 2
    }

    /// <summary>Pure viewport calculations shared by grid navigation and EditMode tests.</summary>
    public static class CsvGridNavigation
    {
        public static Vector2 RestoreRowScreenOffset(Vector2 scroll, float rowTop, float screenOffset)
        {
            scroll.y = rowTop - screenOffset;
            return scroll;
        }

        public static Vector2 Reveal(Vector2 scroll, float rowTop, float rowHeight,
            float columnLeft, float columnWidth, float viewportHeight, float viewportWidth,
            bool frozenColumn, CsvGridRevealMode vertical, CsvGridRevealMode horizontal)
        {
            rowHeight = Mathf.Max(0f, rowHeight);
            columnWidth = Mathf.Max(0f, columnWidth);
            viewportHeight = Mathf.Max(0f, viewportHeight);
            viewportWidth = Mathf.Max(0f, viewportWidth);

            float rowBottom = rowTop + rowHeight;
            if (vertical == CsvGridRevealMode.Center)
                scroll.y = rowTop - Mathf.Max(0f, viewportHeight - rowHeight) * 0.5f;
            else if (vertical == CsvGridRevealMode.Minimal)
            {
                if (rowTop < scroll.y) scroll.y = rowTop;
                else if (rowBottom > scroll.y + viewportHeight) scroll.y = rowBottom - viewportHeight;
            }

            if (!frozenColumn && horizontal != CsvGridRevealMode.Preserve)
            {
                float columnRight = columnLeft + columnWidth;
                if (horizontal == CsvGridRevealMode.Center)
                    scroll.x = columnWidth >= viewportWidth
                        ? columnLeft
                        : columnLeft - (viewportWidth - columnWidth) * 0.5f;
                else
                {
                    if (columnLeft < scroll.x) scroll.x = columnLeft;
                    else if (columnRight > scroll.x + viewportWidth)
                        scroll.x = columnRight - viewportWidth;
                }
            }
            return scroll;
        }
    }
}
