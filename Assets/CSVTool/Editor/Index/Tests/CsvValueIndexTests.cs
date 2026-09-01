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
    }
}
