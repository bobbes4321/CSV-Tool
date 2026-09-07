using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using CsvTool.Core;
using CsvTool.Editor;
using CsvTool.Editor.Index;
using CsvTool.Editor.Search;
using CsvTool.Schema;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CsvTool.Editor.Tests
{
    [TestFixture]
    [Category("Performance")]
    public sealed class CsvPerformanceTests
    {
        [Test]
        public void ProfileImportantCsvActions()
        {
            string path = Path.Combine(Application.dataPath, "Data", "units.csv");
            byte[] realBytes = File.ReadAllBytes(path);
            ProfileDocument("units.csv", realBytes, path);

            byte[] syntheticBytes = CreateSyntheticCsv(20000, 20);
            ProfileDocument("synthetic-20k-x20", syntheticBytes, null);
        }

        [UnityTest]
        public System.Collections.IEnumerator ProfileRealGridRepaint()
        {
            CsvParseOptions options = new CsvParseOptions
            {
                HasHeader = true,
                TrimClassificationWhitespace = true,
                StrictQuotes = false
            };
            CsvDocument document = CsvDocument.Load(CreateSyntheticCsv(20000, 20), options);
            CsvGridRenderProfileWindow window = CsvGridRenderProfileWindow.Open(document);
            double deadline = EditorApplication.timeSinceStartup + 10d;
            while (window.SampleCount < 20 && EditorApplication.timeSinceStartup < deadline)
            {
                window.Repaint();
                yield return null;
            }

            int sampleCount = window.SampleCount;
            double median = window.MedianMilliseconds;
            int drawnCells = window.DrawnCellCount;
            window.Close();
            Assert.That(sampleCount, Is.GreaterThanOrEqualTo(5),
                "The render profile window did not receive enough repaint frames.");
            UnityEngine.Debug.Log(string.Format("CSVRENDER grid-draw median_ms={0:F3} samples={1} drawn_cells={2}",
                median, sampleCount, drawnCells));
        }

        private static void ProfileDocument(string name, byte[] bytes, string path)
        {
            CsvParseOptions options = new CsvParseOptions
            {
                HasHeader = true,
                TrimClassificationWhitespace = true,
                StrictQuotes = false
            };

            Measure(name + ".load", () => CsvDocument.Load(bytes, options, path), 5,
                out CsvDocument document);
            Assert.That(document.Records.Count, Is.GreaterThan(0));

            Measure(name + ".is-dirty", () => { bool value = document.IsDirty; }, 50);
            Measure(name + ".column-count", () => { int value = document.ColumnCount; }, 50);
            Measure(name + ".get-changes-clean", () => { IReadOnlyList<CsvCellChange> value = document.GetChanges(); }, 5);

            if (!string.IsNullOrEmpty(path))
            {
                CsvTableController table = new CsvTableController(name, path,
                    new CsvTableSchema(name, Path.GetFileName(path)));
                Measure(name + ".table-open", table.Open, 5);
                int filterIteration = 0;
                Measure(name + ".filter-rebuild", () =>
                {
                    table.SetSearchQuery((filterIteration++ & 1) == 0 ? "unit" : "xyz");
                }, 5);

                CsvValueIndex index = new CsvValueIndex(table);
                Measure(name + ".distinct-values-rebuild", () =>
                {
                    index.InvalidateColumn(0);
                    index.GetDistinctValues(0);
                }, 3);
                Measure(name + ".contextual-search", () => CsvContextualCellFinder.Search(table, 1, "unit"), 3);
            }

            int recordIndex = document.Records.Count > 1 ? 1 : 0;
            string original = document.GetCell(recordIndex, 0);
            document.SetCell(recordIndex, 0, original + "-perf");
            Measure(name + ".serialize-one-cell", () => document.Serialize(), 5);

            ProfileGridActions(name, document);
        }

        private static void ProfileGridActions(string name, CsvDocument document)
        {
            CsvGrid grid = new CsvGrid(new CsvGridSettings { FrozenColumnCount = 1 });
            InvokePrivate(grid, "EnsureDocument", document);
            InvokePrivate(grid, "EnsureRowMap", (object)null);
            InvokePrivate(grid, "CalculateViewport", new Rect(0f, 0f, 1200f, 720f));
            grid.ScrollPosition = new Vector2(0f, 100000f);
            InvokePrivate(grid, "CalculateVisibleRanges");

            Measure(name + ".grid-scroll-range", () => InvokePrivate(grid, "CalculateVisibleRanges"), 50);

            int lastRecord = document.Records.Count - 1;
            Measure(name + ".grid-reveal-physical-cell", () =>
                grid.SelectPhysicalCellForNavigation(lastRecord, 10,
                    CsvGridRevealMode.Minimal, CsvGridRevealMode.Minimal, null), 5);
            Measure(name + ".grid-visibility-check", () =>
                grid.IsPhysicalRecordFullyVisible(lastRecord), 50);

            grid.SelectPhysicalCell(1, 0, false);
            MethodInfo beginEdit = typeof(CsvGrid).GetMethod("BeginEdit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo editingText = typeof(CsvGrid).GetField("_editingText",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Measure(name + ".grid-commit-text-entry", () =>
            {
                beginEdit.Invoke(grid, null);
                editingText.SetValue(grid, "typed-value");
                grid.CommitEditing();
            }, 50);

            string tsv = CreateSyntheticTsv(200, 10);
            Measure(name + ".grid-parse-tsv", () => CsvGridClipboard.ParseTsv(tsv), 5);
            List<List<string>> parsed = CsvGridClipboard.ParseTsv(tsv);
            List<IReadOnlyList<string>> formatRows = new List<IReadOnlyList<string>>(parsed.Count);
            for (int i = 0; i < parsed.Count; i++) formatRows.Add(parsed[i]);
            Measure(name + ".grid-format-tsv", () => CsvGridClipboard.FormatTsv(formatRows), 5);
            Measure(name + ".grid-paste-preflight", () =>
                CsvGridPastePreflight.Create(document, parsed, 1, 0,
                    row => row < document.Records.Count ? row : -1, null), 5);
        }

        private static object InvokePrivate(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            return method.Invoke(target, arguments);
        }

        private static string CreateSyntheticTsv(int rowCount, int columnCount)
        {
            StringBuilder builder = new StringBuilder(rowCount * columnCount * 8);
            for (int row = 0; row < rowCount; row++)
            {
                if (row > 0) builder.Append('\n');
                for (int column = 0; column < columnCount; column++)
                {
                    if (column > 0) builder.Append('\t');
                    builder.Append("typed_");
                    builder.Append(row);
                    builder.Append('_');
                    builder.Append(column);
                }
            }
            return builder.ToString();
        }

        private static byte[] CreateSyntheticCsv(int rowCount, int columnCount)
        {
            StringBuilder builder = new StringBuilder(rowCount * columnCount * 8);
            for (int column = 0; column < columnCount; column++)
            {
                if (column > 0) builder.Append(',');
                builder.Append("column");
                builder.Append(column);
            }
            builder.Append('\n');
            for (int row = 0; row < rowCount; row++)
            {
                for (int column = 0; column < columnCount; column++)
                {
                    if (column > 0) builder.Append(',');
                    builder.Append("value_");
                    builder.Append(column % 5);
                    builder.Append('_');
                    builder.Append(row);
                }
                builder.Append('\n');
            }
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        private static void Measure<T>(string label, Func<T> action, int iterations, out T last)
        {
            action();
            List<double> samples = new List<double>(iterations);
            last = default(T);
            for (int i = 0; i < iterations; i++)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                last = action();
                stopwatch.Stop();
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            double median = samples[samples.Count / 2];
            UnityEngine.Debug.Log(string.Format("CSVPERF {0} median_ms={1:F3} min_ms={2:F3} max_ms={3:F3} iterations={4}",
                label, median, samples[0], samples[samples.Count - 1], iterations));
        }

        private static void Measure(string label, Action action, int iterations)
        {
            bool ignored;
            Measure(label, () => { action(); return true; }, iterations, out ignored);
        }
    }

    internal sealed class CsvGridRenderProfileWindow : EditorWindow
    {
        private static CsvDocument s_document;
        private readonly CsvGrid grid = new CsvGrid(new CsvGridSettings { FrozenColumnCount = 1 });
        private readonly List<double> samples = new List<double>();
        private int drawnCellCount;
        private bool autoClose;

        public int SampleCount { get { return samples.Count; } }
        public int DrawnCellCount { get { return drawnCellCount; } }
        public double MedianMilliseconds
        {
            get
            {
                if (samples.Count == 0) return 0d;
                List<double> sorted = new List<double>(samples);
                sorted.Sort();
                return sorted[sorted.Count / 2];
            }
        }

        public static CsvGridRenderProfileWindow Open(CsvDocument document)
        {
            s_document = document;
            CsvGridRenderProfileWindow window = GetWindow<CsvGridRenderProfileWindow>(
                "CSV Tool Render Profile", true);
            window.samples.Clear();
            window.autoClose = false;
            window.minSize = new Vector2(900f, 500f);
            window.position = new Rect(window.position.x, window.position.y, 1200f, 720f);
            window.ShowUtility();
            return window;
        }

        public static void StartSyntheticProfile()
        {
            StringBuilder builder = new StringBuilder(20000 * 20 * 8);
            for (int column = 0; column < 20; column++)
            {
                if (column > 0) builder.Append(',');
                builder.Append("column");
                builder.Append(column);
            }
            builder.Append('\n');
            for (int row = 0; row < 20000; row++)
            {
                for (int column = 0; column < 20; column++)
                {
                    if (column > 0) builder.Append(',');
                    builder.Append("value_");
                    builder.Append(column % 5);
                    builder.Append('_');
                    builder.Append(row);
                }
                builder.Append('\n');
            }

            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes(builder.ToString()),
                new CsvParseOptions { HasHeader = true, TrimClassificationWhitespace = true,
                    StrictQuotes = false });
            CsvGridRenderProfileWindow window = Open(document);
            window.autoClose = true;
            window.Repaint();
        }

        public static void StartUnitsProfile()
        {
            string path = Path.Combine(Application.dataPath, "Data", "units.csv");
            CsvDocument document = CsvDocument.Load(File.ReadAllBytes(path),
                new CsvParseOptions { HasHeader = true, TrimClassificationWhitespace = true,
                    StrictQuotes = false }, path);
            CsvGridRenderProfileWindow window = Open(document);
            window.autoClose = true;
            window.Repaint();
        }

        private void OnGUI()
        {
            Rect rect = new Rect(0f, 0f, Mathf.Max(1f, position.width), Mathf.Max(1f, position.height));
            Stopwatch stopwatch = Stopwatch.StartNew();
            grid.Draw(rect, s_document);
            stopwatch.Stop();
            if (Event.current.type == EventType.Repaint)
            {
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                drawnCellCount = grid.VisibleStats.DrawnCellCount;
                if (autoClose)
                {
                    if (samples.Count >= 30)
                    {
                        UnityEngine.Debug.Log(string.Format("CSVRENDER grid-draw median_ms={0:F3} samples={1} drawn_cells={2}",
                            MedianMilliseconds, samples.Count, drawnCellCount));
                        Close();
                    }
                    else Repaint();
                }
            }
        }
    }
}
