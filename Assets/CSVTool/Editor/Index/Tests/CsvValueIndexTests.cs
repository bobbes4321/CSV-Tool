using System.IO;
using NUnit.Framework;
using CsvTool.Core;
using CsvTool.Schema;
using CsvTool.Editor;

namespace CsvTool.Editor.Index.Tests
{
    public sealed class CsvValueIndexTests
    {
        private string folder;
        [SetUp] public void SetUp() { folder = Path.Combine(Path.GetTempPath(), "csvtool-index-tests"); Directory.CreateDirectory(folder); }
        [TearDown] public void TearDown() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

        [Test]
        public void DistinctValuesExcludeStructuralRowsAndPrefixIsInsensitive()
        {
            string path = Path.Combine(folder, "items.csv"); File.WriteAllText(path, "id,name\n1,Apple\n# note,Apple\n2,Apricot\n");
            CsvTableController table = new CsvTableController("items", path, new CsvTableSchema("items", "items.csv")); table.Open();
            CsvValueIndex index = new CsvValueIndex(table);
            Assert.AreEqual(2, index.GetDistinctValues(1).Count);
            Assert.AreEqual("Apple", index.LookupPrefix(1, "app", 1)[0].Value);
        }

        [Test]
        public void ReferenceReturnsPhysicalTargetRecord()
        {
            string sourcePath = Path.Combine(folder, "units.csv"), targetPath = Path.Combine(folder, "abilities.csv");
            File.WriteAllText(targetPath, "id,name\nfire,Fireball\nheal,Heal\n");
            CsvTableSchema sourceSchema = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema ability = new CsvColumnSchema("ability") { TokenSyntax = new CsvTokenSyntax(";", true, true) };
            ability.References.Add(new CsvReferenceSpec("abilities", "id") { TargetDisplayColumn = "name" }); sourceSchema.Columns.Add(ability);
            File.WriteAllText(sourcePath, "id,ability\nu1, fire ; heal\n");
            CsvTableController source = new CsvTableController("units", sourcePath, sourceSchema); source.Open();
            CsvTableController target = new CsvTableController("abilities", targetPath, new CsvTableSchema("abilities", "abilities.csv")); target.Open();
            var results = CsvReferenceResolver.ResolveAll(source, 1, 1, new[] { target });
            Assert.AreEqual(CsvReferenceResolutionStatus.Resolved, results[0].Status); Assert.AreEqual(1, results[0].TargetRecordIndex);
            Assert.AreEqual("Fireball", results[0].TargetDisplay);
        }

        [Test]
        public void SourceReferenceUsesResolvedColumnWhenDisplayNameCollidesWithAnotherHeader()
        {
            string sourcePath = Path.Combine(folder, "units.csv"), targetPath = Path.Combine(folder, "abilities.csv");
            File.WriteAllText(targetPath, "id,name\nfire,Fireball\n");
            CsvTableSchema sourceSchema = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema ability = new CsvColumnSchema("ability")
            {
                DisplayName = "id",
                TokenSyntax = new CsvTokenSyntax(";", true, true)
            };
            ability.References.Add(new CsvReferenceSpec("abilities", "id") { TargetDisplayColumn = "name" });
            sourceSchema.Columns.Add(ability);
            File.WriteAllText(sourcePath, "id,ability\nu1,fire\n");

            CsvTableController source = new CsvTableController("units", sourcePath, sourceSchema); source.Open();
            CsvTableController target = new CsvTableController("abilities", targetPath,
                new CsvTableSchema("abilities", "abilities.csv")); target.Open();
            var results = CsvReferenceResolver.ResolveAll(source, 1, 1, new[] { target });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(CsvReferenceResolutionStatus.Resolved, results[0].Status);
            Assert.AreEqual(1, results[0].TargetRecordIndex);
            Assert.AreEqual(0, results[0].TargetKeyColumnIndex);
        }

        [Test]
        public void TargetReferenceDoesNotUseDisplayNameAsRawHeader()
        {
            string sourcePath = Path.Combine(folder, "units.csv"), targetPath = Path.Combine(folder, "abilities.csv");
            File.WriteAllText(targetPath, "key,name,id\nk1,Fireball,fire\n");
            CsvTableSchema targetSchema = new CsvTableSchema("abilities", "abilities.csv");
            targetSchema.Columns.Add(new CsvColumnSchema("name") { Index = 1, DisplayName = "id" });
            File.WriteAllText(sourcePath, "id,ability\nu1,fire\n");
            CsvTableSchema sourceSchema = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema ability = new CsvColumnSchema("ability");
            ability.References.Add(new CsvReferenceSpec("abilities", "id"));
            sourceSchema.Columns.Add(ability);

            CsvTableController source = new CsvTableController("units", sourcePath, sourceSchema); source.Open();
            CsvTableController target = new CsvTableController("abilities", targetPath, targetSchema); target.Open();
            var results = CsvReferenceResolver.ResolveAll(source, 1, 1, new[] { target });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(CsvReferenceResolutionStatus.Resolved, results[0].Status);
            Assert.AreEqual(2, results[0].TargetKeyColumnIndex);
        }

        [Test]
        public void DuplicateRawTargetHeadersRemainUnresolvedWithoutAnIndex()
        {
            string sourcePath = Path.Combine(folder, "units.csv"), targetPath = Path.Combine(folder, "abilities.csv");
            File.WriteAllText(targetPath, "id,id\nfire,other\n");
            File.WriteAllText(sourcePath, "id,ability\nu1,fire\n");
            CsvTableSchema sourceSchema = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema ability = new CsvColumnSchema("ability");
            ability.References.Add(new CsvReferenceSpec("abilities", "id"));
            sourceSchema.Columns.Add(ability);

            CsvTableController source = new CsvTableController("units", sourcePath, sourceSchema); source.Open();
            CsvTableController target = new CsvTableController("abilities", targetPath,
                new CsvTableSchema("abilities", "abilities.csv")); target.Open();
            var results = CsvReferenceResolver.ResolveAll(source, 1, 1, new[] { target });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(CsvReferenceResolutionStatus.Missing, results[0].Status);
            Assert.AreEqual(-1, results[0].TargetKeyColumnIndex);
        }

        [Test]
        public void ExplicitConfiguredTargetIndexWinsOverDuplicateRawHeaderName()
        {
            string sourcePath = Path.Combine(folder, "units.csv"), targetPath = Path.Combine(folder, "abilities.csv");
            File.WriteAllText(targetPath, "key,key\nwrong,fire\n");
            CsvTableSchema targetSchema = new CsvTableSchema("abilities", "abilities.csv");
            targetSchema.Columns.Add(new CsvColumnSchema("key") { Index = 1 });
            File.WriteAllText(sourcePath, "id,ability\nu1,fire\n");
            CsvTableSchema sourceSchema = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema ability = new CsvColumnSchema("ability");
            ability.References.Add(new CsvReferenceSpec("abilities", "key"));
            sourceSchema.Columns.Add(ability);

            CsvTableController source = new CsvTableController("units", sourcePath, sourceSchema); source.Open();
            CsvTableController target = new CsvTableController("abilities", targetPath, targetSchema); target.Open();
            var results = CsvReferenceResolver.ResolveAll(source, 1, 1, new[] { target });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(CsvReferenceResolutionStatus.Resolved, results[0].Status);
            Assert.AreEqual(1, results[0].TargetKeyColumnIndex);
            Assert.AreEqual("fire", results[0].TargetKey);
        }

        [Test]
        public void RegexReferenceTimeoutReturnsInvalidResolutionInsteadOfStalling()
        {
            string sourcePath = Path.Combine(folder, "units.csv"), targetPath = Path.Combine(folder, "abilities.csv");
            File.WriteAllText(targetPath, "id\nfire\n");
            string pathological = new string('a', 8192) + "!";
            File.WriteAllText(sourcePath, "id,ability\nu1," + pathological + "\n");
            CsvTableSchema sourceSchema = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema ability = new CsvColumnSchema("ability")
            {
                TokenSyntax = new CsvTokenSyntax(CsvTokenExtractionMode.RegexCapture)
                {
                    RegexPattern = "^(a+)+$",
                    RegexCaptureGroup = 1
                }
            };
            ability.References.Add(new CsvReferenceSpec("abilities", "id"));
            sourceSchema.Columns.Add(ability);

            CsvTableController source = new CsvTableController("units", sourcePath, sourceSchema); source.Open();
            CsvTableController target = new CsvTableController("abilities", targetPath,
                new CsvTableSchema("abilities", "abilities.csv")); target.Open();
            var results = CsvReferenceResolver.ResolveAll(source, 1, 1, new[] { target });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(CsvReferenceResolutionStatus.Invalid, results[0].Status);
            StringAssert.Contains("evaluation limit", results[0].Message);
        }

        [Test]
        public void DuplicateTargetTableNamesRemainAmbiguousInsteadOfSelectingTheFirstTable()
        {
            string sourcePath = Path.Combine(folder, "units.csv");
            string firstPath = Path.Combine(folder, "abilities-a.csv");
            string secondPath = Path.Combine(folder, "abilities-b.csv");
            File.WriteAllText(sourcePath, "id,ability\nu1,fire\n");
            File.WriteAllText(firstPath, "id\nfire\n");
            File.WriteAllText(secondPath, "id\nfire\n");
            CsvTableSchema sourceSchema = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema ability = new CsvColumnSchema("ability");
            ability.References.Add(new CsvReferenceSpec("abilities", "id"));
            sourceSchema.Columns.Add(ability);

            CsvTableController source = new CsvTableController("units", sourcePath, sourceSchema); source.Open();
            CsvTableController first = new CsvTableController("abilities", firstPath,
                new CsvTableSchema("abilities", "abilities-a.csv")); first.Open();
            CsvTableController second = new CsvTableController("abilities", secondPath,
                new CsvTableSchema("abilities", "abilities-b.csv")); second.Open();

            var results = CsvReferenceResolver.ResolveAll(source, 1, 1, new[] { first, second });

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(CsvReferenceResolutionStatus.Ambiguous, results[0].Status);
            StringAssert.Contains("target table", results[0].Message);
        }
    }
}
