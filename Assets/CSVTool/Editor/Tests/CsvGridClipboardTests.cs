using System.Collections.Generic;
using NUnit.Framework;
using CsvTool.Editor;

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
    }
}
