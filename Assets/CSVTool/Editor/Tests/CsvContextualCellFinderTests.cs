using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CsvTool.Editor.Search;
using NUnit.Framework;

namespace CsvTool.Editor.Tests
{
    public sealed class CsvContextualCellFinderTests
    {
        private string temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(),
                "csvtool-contextual-finder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
        }

        [Test]
        public void SelectedRowHeaderMatchesRankBeforeSelectedValuesAndOtherRows()
        {
            CsvTableController table = Open(
                "id,attackDamage,description\n" +
                "herbivore,12,melee\n" +
                "carnivore,20,attack specialist\n");

            IReadOnlyList<CsvContextualCellResult> results = CsvContextualCellFinder.Search(
                table, 1, "attack");

            Assert.GreaterOrEqual(results.Count, 2);
            Assert.AreEqual(1, results[0].RecordIndex);
            Assert.AreEqual(1, results[0].ColumnIndex);
            Assert.AreEqual(CsvContextualMatchCategory.SelectedRowHeader, results[0].Category);
            Assert.AreEqual("attackDamage", results[0].HeaderPreview);
            Assert.AreEqual("12", results[0].ValuePreview);
            Assert.IsTrue(results[0].IsSelectedRow);
            Assert.AreEqual(2, results[1].RecordIndex);
            Assert.AreEqual(2, results[1].ColumnIndex);
            Assert.AreEqual(CsvContextualMatchCategory.OtherRowValue, results[1].Category);
            Assert.Greater(results[0].Rank, results[1].Rank);
        }

        [Test]
        public void SelectedValueMatchKeepsPhysicalRowWhenFilterHidesOtherRows()
        {
            CsvTableController table = Open(
                "id,diet,notes\n" +
                "wolf,meat,fast\n" +
                "herbivore,plants,graze\n" +
                "deer,plants,quiet\n");
            table.SetSearchQuery("quiet");

            IReadOnlyList<CsvContextualCellResult> results = CsvContextualCellFinder.Search(
                table, 2, "plants");

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(2, results[0].RecordIndex);
            Assert.AreEqual(1, results[0].ColumnIndex);
            Assert.AreEqual(CsvContextualMatchCategory.SelectedRowValue, results[0].Category);
            Assert.AreEqual("diet", results[0].HeaderPreview);
            Assert.AreEqual("plants", results[0].ValuePreview);
            Assert.AreEqual(3, results[1].RecordIndex);
            Assert.AreEqual(CsvContextualMatchCategory.OtherRowValue, results[1].Category);
            Assert.Greater(results[0].Rank, results[1].Rank);
        }

        [Test]
        public void ConfiguredDisplayHeaderIsSearchableButResultTargetsPhysicalColumn()
        {
            CsvTool.Schema.CsvTableSchema schema = new CsvTool.Schema.CsvTableSchema("units", "units.csv");
            schema.Columns.Add(new CsvTool.Schema.CsvColumnSchema(1) { Name = "hp", DisplayName = "Hit Points" });
            string path = Write("id,hp,hp\nunit,10,20\n");
            CsvTableController table = new CsvTableController("units", path, schema);
            table.Open();

            IReadOnlyList<CsvContextualCellResult> results = CsvContextualCellFinder.Search(
                table, 1, "hit points");

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(1, results[0].RecordIndex);
            Assert.AreEqual(1, results[0].ColumnIndex);
            Assert.AreEqual("Hit Points", results[0].HeaderPreview);
            Assert.AreEqual("10", results[0].ValuePreview);
        }

        [Test]
        public void NullOrEmptyInputReturnsNoResultsWithoutChangingTableSearch()
        {
            CsvTableController table = Open("id,value\nalpha,one\n");
            table.SetSearchQuery("alpha");

            Assert.IsEmpty(CsvContextualCellFinder.Search(table, 1, null));
            Assert.IsEmpty(CsvContextualCellFinder.Search(table, 1, string.Empty));
            Assert.AreEqual("alpha", table.SearchQuery);
        }

        private CsvTableController Open(string contents)
        {
            string path = Write(contents);
            CsvTableController table = new CsvTableController("table", path, null);
            table.Open();
            return table;
        }

        private string Write(string contents)
        {
            string path = Path.Combine(temporaryDirectory, "table.csv");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return path;
        }
    }
}
