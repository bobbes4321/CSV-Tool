using CsvTool.Schema;
using NUnit.Framework;

namespace CsvTool.Editor.Tests
{
    public sealed class CsvRecordViewTests
    {
        [Test]
        public void RecordFormIsSharedByFullAndCompactPresentations()
        {
            CsvRecordView view = new CsvRecordView(null);

            Assert.IsNotNull(view.Form);
            Assert.AreSame(view, view.Form.RecordView);
            Assert.AreEqual(-1, view.Form.SelectedRecordIndex);
        }

        [Test]
        public void ExplicitFieldsResolveByNameAndGroupInDeclaredOrder()
        {
            string[] headers = { "id", "hp", "speed", "unused" };
            CsvTableSchema schema = new CsvTableSchema("units", "units.csv");
            schema.Columns.Add(new CsvColumnSchema("hp") { ValueKind = CsvValueKind.Integer, Description = "Hit points" });
            schema.Columns.Add(new CsvColumnSchema("id") { ReadOnly = true });
            CsvRecordViewDefinition definition = new CsvRecordViewDefinition();
            definition.Fields.Add(new CsvRecordFieldDefinition("id", "Identity"));
            definition.Fields.Add(new CsvRecordFieldDefinition("hp", "Stats"));
            definition.Fields.Add(new CsvRecordFieldDefinition("speed", "Stats") { ValueKindOverride = CsvValueKind.Decimal });

            var groups = CsvRecordFieldResolver.Resolve(headers, schema, definition);

            Assert.AreEqual(3, groups.Count);
            Assert.AreEqual("Identity", groups[0].Name);
            Assert.AreEqual(0, groups[0].Fields[0].ColumnIndex);
            Assert.IsTrue(groups[0].Fields[0].ReadOnly);
            Assert.AreEqual("Stats", groups[1].Name);
            Assert.AreEqual(2, groups[1].Fields.Count);
            Assert.AreEqual(CsvValueKind.Integer, groups[1].Fields[0].ValueKind);
            Assert.AreEqual(CsvValueKind.Decimal, groups[1].Fields[1].ValueKind);
            Assert.AreEqual("Other", groups[2].Name);
            Assert.AreEqual(3, groups[2].Fields[0].ColumnIndex);
        }

        [Test]
        public void IndexSelectorHandlesUnnamedAndDuplicateHeaders()
        {
            string[] headers = { "", "value", "value" };
            CsvRecordViewDefinition definition = new CsvRecordViewDefinition { IncludeUnconfiguredColumns = false };
            definition.Fields.Add(new CsvRecordFieldDefinition(0, "Unnamed"));
            definition.Fields.Add(new CsvRecordFieldDefinition(2, "Second"));

            var groups = CsvRecordFieldResolver.Resolve(headers, null, definition);

            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual(0, groups[0].Fields[0].ColumnIndex);
            Assert.AreEqual(2, groups[1].Fields[0].ColumnIndex);
        }

        [Test]
        public void TypedParsersUseInvariantForms()
        {
            long integer;
            double decimalValue;
            bool boolean;
            Assert.IsTrue(CsvRecordValueParser.TryParseInteger("-12", out integer));
            Assert.AreEqual(-12L, integer);
            Assert.IsTrue(CsvRecordValueParser.TryParseDecimal("1.25", out decimalValue));
            Assert.AreEqual(1.25, decimalValue, 0.00001);
            Assert.IsTrue(CsvRecordValueParser.TryParseBoolean("yes", out boolean));
            Assert.IsTrue(boolean);
            Assert.IsFalse(CsvRecordValueParser.TryParseDecimal("1,25", out decimalValue));
        }
    }
}
