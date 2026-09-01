using System.Collections.Generic;
using CsvTool.Editor;
using CsvTool.Schema;
using NUnit.Framework;

namespace CsvTool.Editor.Tests
{
    public sealed class CsvGoToColumnTests
    {
        [Test]
        public void EntriesExposeConfiguredNameGroupValueAndPhysicalIndex()
        {
            string[] headers = { "id", "damage", "damage" };
            string[] values = { "herbivore", "12", "24" };
            CsvTableSchema schema = new CsvTableSchema("units", "units.csv");
            schema.Columns.Add(new CsvColumnSchema("damage") { DisplayName = "Attack Damage" });
            CsvRecordViewDefinition definition = new CsvRecordViewDefinition();
            definition.Fields.Add(new CsvRecordFieldDefinition(2, "Combat") { DisplayName = "Heavy Damage" });

            CsvGoToColumnModel model = CsvGoToColumnModel.ForColumns(headers, values, schema, definition);

            Assert.AreEqual(3, model.Entries.Count);
            Assert.AreEqual(0, model.Entries[0].PhysicalColumnIndex);
            Assert.AreEqual("herbivore", model.Entries[0].CurrentValue);
            // Name-only schema selectors are ambiguous for duplicate headers, so they do not
            // get guessed onto either physical column. The index-qualified definition is exact.
            Assert.AreEqual("damage", model.Entries[1].DisplayName);
            Assert.AreEqual("Heavy Damage", model.Entries[2].DisplayName);
            Assert.AreEqual("Combat", model.Entries[2].GroupName);
            Assert.AreEqual("24", model.Entries[2].ValuePreview);
        }

        [Test]
        public void QueryRanksDisplayNameBeforeValueAndRetainsPhysicalIndices()
        {
            CsvTableSchema schema = new CsvTableSchema("units", "units.csv");
            schema.Columns.Add(new CsvColumnSchema(0) { DisplayName = "Attack Damage" });
            CsvGoToColumnModel model = CsvGoToColumnModel.ForColumns(
                new[] { "damage", "description", "other" },
                new[] { "12", "Attack Damage notes", "unrelated" }, schema);

            model.SetQuery("attack damage");

            Assert.AreEqual(2, model.Results.Count);
            Assert.AreEqual(0, model.Results[0].PhysicalColumnIndex);
            Assert.AreEqual(1, model.Results[1].PhysicalColumnIndex);
            Assert.IsTrue(model.Results[0].Score > model.Results[1].Score);
        }

        [Test]
        public void FuzzySearchAndSelectionReturnPhysicalColumn()
        {
            CsvGoToColumnModel model = CsvGoToColumnModel.ForColumns(
                new[] { "hitPoints", "movementSpeed", "armor" },
                new[] { "40", "3.5", "10" });

            model.SetQuery("msp");
            Assert.AreEqual(1, model.Results.Count);
            Assert.AreEqual(1, model.Results[0].PhysicalColumnIndex);
            int physicalColumn;
            Assert.IsTrue(model.TryGetSelectedPhysicalColumn(out physicalColumn));
            Assert.AreEqual(1, physicalColumn);

            model.MoveSelection(1);
            Assert.AreEqual(0, model.SelectedIndex, "Selection wraps when a caller moves it past the sole result.");
        }

        [Test]
        public void EmptyQueryPreservesPhysicalOrderAndArrowNavigationStopsAtEnds()
        {
            CsvGoToColumnModel model = CsvGoToColumnModel.ForColumns(
                new[] { "first", "second", "third" }, new string[0]);
            Assert.AreEqual(3, model.Results.Count);
            Assert.AreEqual(0, model.Results[0].PhysicalColumnIndex);
            Assert.AreEqual(1, model.Results[1].PhysicalColumnIndex);

            model.MoveSelection(-1);
            Assert.AreEqual(0, model.SelectedEntry.PhysicalColumnIndex);
            model.MoveSelection(1);
            Assert.AreEqual(1, model.SelectedEntry.PhysicalColumnIndex);
        }

        [Test]
        public void EmptyQuerySelectsCurrentColumnAndReturnsToItAfterClearingSearch()
        {
            CsvGoToColumnModel model = CsvGoToColumnModel.ForColumns(
                new[] { "first", "second", "third" }, new string[0], null, null, 2);

            Assert.AreEqual(2, model.SelectedEntry.PhysicalColumnIndex);
            model.SetQuery("first");
            Assert.AreEqual(0, model.SelectedEntry.PhysicalColumnIndex);
            model.SetQuery(string.Empty);
            Assert.AreEqual(2, model.SelectedEntry.PhysicalColumnIndex);
        }

        [Test]
        public void ResultExplainsWhichFieldMatched()
        {
            CsvGoToColumnModel model = CsvGoToColumnModel.ForColumns(
                new[] { "id", "notes" }, new[] { "unit", "special value" });

            model.SetQuery("special");

            Assert.AreEqual(CsvGoToColumnMatchKind.CurrentValue, model.SelectedEntry.MatchKind);
            Assert.AreEqual("Current value", model.SelectedEntry.MatchLabel);
        }

        [Test]
        public void NoMatchesHaveNoPhysicalSelection()
        {
            CsvGoToColumnModel model = CsvGoToColumnModel.ForColumns(
                new[] { "first" }, new[] { "value" });
            model.SetQuery("missing");
            int physicalColumn;
            Assert.IsFalse(model.TryGetSelectedPhysicalColumn(out physicalColumn));
            Assert.AreEqual(-1, physicalColumn);
        }
    }
}
