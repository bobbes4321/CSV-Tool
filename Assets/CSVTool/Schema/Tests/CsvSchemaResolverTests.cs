using System.Collections.Generic;
using NUnit.Framework;

namespace CsvTool.Schema.Tests
{
    public sealed class CsvSchemaResolverTests
    {
        [Test]
        public void IndexTakesPrecedenceAndReportsNameHintMismatch()
        {
            CsvTableSchema table = new CsvTableSchema("units", "units.csv");
            table.Columns.Add(new CsvColumnSchema("old") { Index = 1 });
            CsvResolvedTableSchema resolved = CsvSchemaResolver.Resolve(table, new[] { "id", "name" });

            Assert.IsTrue(resolved.Columns[0].IsResolved);
            Assert.AreEqual(1, resolved.Columns[0].PhysicalIndex);
            Assert.AreEqual("name", resolved.Columns[0].HeaderName);
            Assert.AreEqual("SCHEMA_INDEX_NAME_MISMATCH", resolved.Diagnostics[0].Code);
        }

        [Test]
        public void NameOnlyDuplicateIsAmbiguous()
        {
            CsvTableSchema table = new CsvTableSchema("units", "units.csv");
            table.Columns.Add(new CsvColumnSchema("name"));
            CsvResolvedTableSchema resolved = CsvSchemaResolver.Resolve(table, new[] { "name", "NAME" });

            Assert.AreEqual(CsvSchemaResolutionStatus.Ambiguous, resolved.Columns[0].Status);
            Assert.AreEqual("SCHEMA_HEADER_AMBIGUOUS", resolved.Diagnostics[0].Code);
        }

        [Test]
        public void IdentityAndDisplayUsePhysicalIndicesForBlankHeaders()
        {
            CsvTableSchema table = new CsvTableSchema("units", "units.csv")
            {
                IdentityColumn = "ignored",
                IdentityColumnIndex = 0,
                DisplayColumnIndex = 1
            };
            CsvResolvedTableSchema resolved = CsvSchemaResolver.Resolve(table, new[] { "", "" });

            Assert.AreEqual(0, resolved.Identity.PhysicalIndex);
            Assert.AreEqual(1, resolved.Display.PhysicalIndex);
            Assert.AreNotEqual(resolved.Labels[0], resolved.Labels[1]);
        }

        [Test]
        public void NameOnlyMissingProducesError()
        {
            CsvTableSchema table = new CsvTableSchema("units", "units.csv") { DisplayColumn = "label" };
            CsvResolvedTableSchema resolved = CsvSchemaResolver.Resolve(table, new[] { "id" });

            Assert.AreEqual(CsvSchemaResolutionStatus.Missing, resolved.Display.Status);
            Assert.IsFalse(resolved.IsValid);
        }
    }
}
