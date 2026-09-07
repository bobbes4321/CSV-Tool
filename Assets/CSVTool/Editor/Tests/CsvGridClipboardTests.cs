using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using CsvTool.Core;
using CsvTool.Editor;
using UnityEngine;

namespace CsvTool.Editor.Tests
{
    public sealed class CsvGridClipboardTests
    {
        [Test]
        public void FormatAndParseQuotesTabsNewlinesAndQuotes()
        {
            List<IReadOnlyList<string>> source = new List<IReadOnlyList<string>>
            {
                new List<string> { "plain", "tab\tvalue", "line\none", "quote\"value" },
                new List<string> { "", "trailing", "" }
            };

            string tsv = CsvGridClipboard.FormatTsv(source);
            Assert.AreEqual("plain\t\"tab\tvalue\"\t\"line\n" +
                "one\"\t\"quote\"\"value\"\n\ttrailing\t", tsv);

            List<List<string>> parsed = CsvGridClipboard.ParseTsv(tsv);
            Assert.AreEqual(2, parsed.Count);
            CollectionAssert.AreEqual(new[] { "plain", "tab\tvalue", "line\none", "quote\"value" }, parsed[0]);
            CollectionAssert.AreEqual(new[] { "", "trailing", "" }, parsed[1]);
        }

        [Test]
        public void ParseAcceptsCrLfAndPreservesTrailingEmptyCellsAndRows()
        {
            List<List<string>> parsed = CsvGridClipboard.ParseTsv("a\tb\r\nc\t\r\n\r\n");

            Assert.AreEqual(3, parsed.Count);
            CollectionAssert.AreEqual(new[] { "a", "b" }, parsed[0]);
            CollectionAssert.AreEqual(new[] { "c", "" }, parsed[1]);
            CollectionAssert.AreEqual(new[] { "" }, parsed[2]);
        }

        [Test]
        public void FormatParseRoundTripPreservesRectangularShape()
        {
            List<IReadOnlyList<string>> source = new List<IReadOnlyList<string>>
            {
                new List<string> { "a", "b", "" },
                new List<string> { "multi\r\nline", "\"quoted\"", "last" }
            };

            List<List<string>> parsed = CsvGridClipboard.ParseTsv(CsvGridClipboard.FormatTsv(source));
            Assert.AreEqual(2, parsed.Count);
            Assert.AreEqual(3, parsed[0].Count);
            Assert.AreEqual(3, parsed[1].Count);
            CollectionAssert.AreEqual(new[] { "a", "b", "" }, parsed[0]);
            CollectionAssert.AreEqual(new[] { "multi\r\nline", "\"quoted\"", "last" }, parsed[1]);
        }

        [Test]
        public void ParseMalformedUnclosedQuoteDoesNotThrowOrDropInput()
        {
            List<List<string>> parsed = CsvGridClipboard.ParseTsv("ok\t\"unterminated\tvalue");

            Assert.AreEqual(1, parsed.Count);
            Assert.AreEqual(2, parsed[0].Count);
            Assert.AreEqual("ok", parsed[0][0]);
            Assert.AreEqual("unterminated\tvalue", parsed[0][1]);
        }

        [Test]
        public void PastePreflightReportsOverflowWithoutMutatingOrRetargetingPhysicalRows()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("id,value\none,old\n"));
            List<List<string>> clipboard = CsvGridClipboard.ParseTsv("first\nsecond");

            CsvGridPastePreflight preflight = CsvGridPastePreflight.Create(document, clipboard,
                0, 1, visualRow => visualRow == 0 ? 1 : -1, null);

            Assert.IsTrue(preflight.HasPartialApplicationConditions);
            Assert.AreEqual(1, preflight.OverflowRowCount);
            Assert.AreEqual(1, preflight.OmittedDestinationCount);
            Assert.AreEqual(1, preflight.Cells.Count);
            Assert.AreEqual(1, preflight.Cells[0].RecordIndex,
                "The eligible destination remains a physical document record.");
            Assert.AreEqual("old", document.GetCell(1, 1),
                "Planning a partial paste must not mutate the document.");
        }

        [Test]
        public void PastePreflightReportsProtectedDestinationsBeforeAnyMutation()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("id,value\none,old\n"));
            List<List<string>> clipboard = CsvGridClipboard.ParseTsv("first\tsecond");

            CsvGridPastePreflight preflight = CsvGridPastePreflight.Create(document, clipboard,
                0, 0, visualRow => 1, (record, column) => column == 1 ? "Read-only column." : string.Empty);

            Assert.IsTrue(preflight.HasPartialApplicationConditions);
            Assert.AreEqual(1, preflight.ProtectedCellCount);
            Assert.AreEqual(1, preflight.OmittedDestinationCount);
            Assert.AreEqual(1, preflight.Cells.Count);
            Assert.AreEqual(0, preflight.Cells[0].ColumnIndex);
            Assert.AreEqual("one", document.GetCell(1, 0));
            Assert.AreEqual("old", document.GetCell(1, 1));
        }

        [Test]
        public void AutocompleteProviderUsesPhysicalCoordinatesAndCapsResults()
        {
            CsvGridSettings settings = new CsvGridSettings { AutocompleteMaxSuggestions = 2 };
            CsvGrid grid = new CsvGrid(settings);
            int requestedRecord = -1;
            int requestedColumn = -1;
            string requestedText = null;
            grid.AutocompleteProvider = (record, column, text) =>
            {
                requestedRecord = record;
                requestedColumn = column;
                requestedText = text;
                return new[] { "alpha", "alpine", "also" };
            };

            IReadOnlyList<string> suggestions = grid.GetAutocompleteSuggestions(17, 4, "al");

            Assert.AreEqual(17, requestedRecord);
            Assert.AreEqual(4, requestedColumn);
            Assert.AreEqual("al", requestedText);
            CollectionAssert.AreEqual(new[] { "alpha", "alpine" }, suggestions);
        }

        [Test]
        public void BeginningGridEditClaimsFocusBeforeInspectorCanRedraw()
        {
            CsvGrid grid = new CsvGrid();
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("name,id\nunit,1\n"));
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            FieldInfo documentField = typeof(CsvGrid).GetField("_document", flags);
            FieldInfo selectionField = typeof(CsvGrid).GetField("_selection", flags);
            FieldInfo controlField = typeof(CsvGrid).GetField("_controlId", flags);
            MethodInfo beginEdit = typeof(CsvGrid).GetMethod("BeginEdit", flags);

            Assert.IsNotNull(documentField);
            Assert.IsNotNull(selectionField);
            Assert.IsNotNull(controlField);
            Assert.IsNotNull(beginEdit);
            documentField.SetValue(grid, document);
            selectionField.SetValue(grid, new CsvGridSelection(0, 0, 1));
            controlField.SetValue(grid, 24601);

            int previousFocus = GUIUtility.keyboardControl;
            try
            {
                GUIUtility.keyboardControl = 24600;
                beginEdit.Invoke(grid, null);
                Assert.AreEqual(24601, GUIUtility.keyboardControl,
                    "A grid double-click must release any Record-view TextField immediately.");
            }
            finally
            {
                GUIUtility.keyboardControl = previousFocus;
            }
        }

        [Test]
        public void RowAndColumnHeaderSelectionsCopyTheirCompleteVisibleBodyRange()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes(
                "id,name\none,A\ntwo,B\nthree,C\n"));
            CsvGrid grid = CreatePreparedGrid(document, new List<int> { 1, 3 });

            grid.SelectRow(1, false);
            Assert.AreEqual(CsvGridSelectionScope.Row, grid.SelectionScope);
            Assert.AreEqual(3, grid.Selection.RecordIndex,
                "A visual row selection must retain the physical record index from the filtered map.");
            Assert.AreEqual("three\tC", grid.CopySelectionTsv());

            grid.SelectColumn(1, false);
            Assert.AreEqual(CsvGridSelectionScope.Column, grid.SelectionScope);
            Assert.AreEqual(2, grid.RangeSelection.RowCount);
            Assert.AreEqual(1, grid.RangeSelection.ColumnCount);
            Assert.AreEqual("A\nC", grid.CopySelectionTsv());
        }

        private static CsvGrid CreatePreparedGrid(CsvDocument document, IReadOnlyList<int> visibleRecords)
        {
            CsvGrid grid = new CsvGrid();
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            MethodInfo ensureDocument = typeof(CsvGrid).GetMethod("EnsureDocument", flags);
            MethodInfo ensureRowMap = typeof(CsvGrid).GetMethod("EnsureRowMap", flags);
            Assert.IsNotNull(ensureDocument);
            Assert.IsNotNull(ensureRowMap);
            ensureDocument.Invoke(grid, new object[] { document });
            ensureRowMap.Invoke(grid, new object[] { visibleRecords });
            return grid;
        }
    }
}
