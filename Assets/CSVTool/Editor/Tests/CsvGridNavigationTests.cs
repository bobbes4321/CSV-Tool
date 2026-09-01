using NUnit.Framework;
using UnityEngine;

namespace CsvTool.Editor.Tests
{
    public sealed class CsvGridNavigationTests
    {
        [Test]
        public void ColumnNavigationPreservesVerticalOffsetAndRevealsHorizontallyWithMinimumMovement()
        {
            Vector2 result = CsvGridNavigation.Reveal(new Vector2(120f, 275f),
                300f, 20f, 600f, 100f, 200f, 300f, false,
                CsvGridRevealMode.Preserve, CsvGridRevealMode.Minimal);

            Assert.AreEqual(275f, result.y);
            Assert.AreEqual(400f, result.x);
        }

        [Test]
        public void MinimalRevealDoesNotMoveAnAlreadyVisibleTarget()
        {
            Vector2 result = CsvGridNavigation.Reveal(new Vector2(100f, 200f),
                240f, 20f, 160f, 80f, 120f, 300f, false,
                CsvGridRevealMode.Minimal, CsvGridRevealMode.Minimal);

            Assert.AreEqual(new Vector2(100f, 200f), result);
        }

        [Test]
        public void DistantNavigationCentersRequestedAxes()
        {
            Vector2 result = CsvGridNavigation.Reveal(Vector2.zero,
                800f, 20f, 900f, 100f, 220f, 400f, false,
                CsvGridRevealMode.Center, CsvGridRevealMode.Center);

            Assert.AreEqual(700f, result.y);
            Assert.AreEqual(750f, result.x);
        }

        [Test]
        public void RowMapChangeRestoresTheSameScreenOffset()
        {
            Vector2 result = CsvGridNavigation.RestoreRowScreenOffset(
                new Vector2(300f, 500f), 920f, 65f);

            Assert.AreEqual(300f, result.x);
            Assert.AreEqual(855f, result.y);
            Assert.AreEqual(65f, 920f - result.y);
        }

        [Test]
        public void FrozenColumnNeverChangesHorizontalScroll()
        {
            Vector2 result = CsvGridNavigation.Reveal(new Vector2(250f, 0f),
                0f, 20f, 0f, 120f, 200f, 300f, true,
                CsvGridRevealMode.Preserve, CsvGridRevealMode.Center);

            Assert.AreEqual(250f, result.x);
        }
    }
}
