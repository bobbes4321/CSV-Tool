using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CsvTool.Editor;
using CsvTool.Editor.Validation;
using CsvTool.Schema;
using NUnit.Framework;

namespace CsvTool.Editor.Validation.Tests
{
    public sealed class CsvDatasetValidatorTests
    {
        private string temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(),
                "csvtool-dataset-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
        }

        [Test]
        public void ValidatesConfiguredTypesRequiredRangeAndEnumAtPhysicalCells()
        {
            CsvTableSchema schema = new CsvTableSchema("units", "units.csv")
            {
                IdentityColumn = "id"
            };
            schema.Columns.Add(new CsvColumnSchema("score")
            {
                ValueKind = CsvValueKind.Integer,
                Required = true,
                Minimum = 1,
                Maximum = 10
            });
            CsvColumnSchema kind = new CsvColumnSchema("kind") { ValueKind = CsvValueKind.Enum };
            kind.EnumValues.Add("Warrior");
            kind.EnumValues.Add("Mage");
            schema.Columns.Add(kind);

            CsvTableController table = Open("units.csv",
                "id,score,kind\n" +
                "one,not-a-number,Warrior\n" +
                "two,,Mage\n" +
                "three,11,Rogue\n", schema);

            IReadOnlyList<CsvDatasetDiagnostic> diagnostics = CsvDatasetValidator.Validate(table);

            AssertDiagnostic(diagnostics, "DATA_TYPE_INVALID", 1, 1);
            AssertDiagnostic(diagnostics, "DATA_REQUIRED_MISSING", 2, 1);
            AssertDiagnostic(diagnostics, "DATA_RANGE_ABOVE_MAXIMUM", 3, 1);
            AssertDiagnostic(diagnostics, "DATA_ENUM_INVALID", 3, 2);
        }

        [Test]
        public void ReportsDuplicateIdentityUsingPhysicalRecordCoordinates()
        {
            CsvTableSchema schema = new CsvTableSchema("units", "units.csv")
            {
                IdentityColumn = "id"
            };
            CsvTableController table = Open("units.csv",
                "id,value\nalpha,one\n# note,two\nalpha,three\n", schema);

            IReadOnlyList<CsvDatasetDiagnostic> diagnostics = CsvDatasetValidator.Validate(table);

            AssertDiagnostic(diagnostics, "DATA_IDENTITY_DUPLICATE", 3, 0);
            Assert.AreEqual(0, diagnostics[FindCode(diagnostics, "DATA_IDENTITY_DUPLICATE")].PhysicalColumnIndex);
        }

        [Test]
        public void OptionalMissingReferenceIsWarningAndRequiredMissingReferenceIsError()
        {
            CsvTableSchema abilities = new CsvTableSchema("abilities", "abilities.csv")
            {
                IdentityColumn = "name"
            };
            CsvTableSchema units = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema optional = new CsvColumnSchema("optionalAbility")
            {
                ValueKind = CsvValueKind.Reference,
                TokenSyntax = new CsvTokenSyntax(CsvTokenExtractionMode.WholeCell)
            };
            optional.References.Add(new CsvReferenceSpec("abilities", "name") { Required = false });
            units.Columns.Add(optional);
            CsvColumnSchema required = new CsvColumnSchema("requiredAbility")
            {
                ValueKind = CsvValueKind.Reference,
                TokenSyntax = new CsvTokenSyntax(CsvTokenExtractionMode.WholeCell)
            };
            required.References.Add(new CsvReferenceSpec("abilities", "name") { Required = true });
            units.Columns.Add(required);

            CsvTableController target = Open("abilities.csv", "name\nfire\n", abilities);
            CsvTableController source = Open("units.csv", "id,optionalAbility,requiredAbility\nu1,missing,missing\n", units);

            IReadOnlyList<CsvDatasetDiagnostic> diagnostics = CsvDatasetValidator.Validate(source, target);

            CsvDatasetDiagnostic optionalDiagnostic = Find(diagnostics, "REFERENCE_MISSING", 1, 1);
            CsvDatasetDiagnostic requiredDiagnostic = Find(diagnostics, "REFERENCE_MISSING", 1, 2);
            Assert.AreEqual(CsvDatasetDiagnosticSeverity.Warning, optionalDiagnostic.Severity);
            Assert.AreEqual(CsvDatasetDiagnosticSeverity.Error, requiredDiagnostic.Severity);
        }

        [Test]
        public void ExplicitTargetIdentityIndexResolvesDuplicateHeadersPhysically()
        {
            CsvTableSchema targetSchema = new CsvTableSchema("abilities", "abilities.csv")
            {
                IdentityColumn = "name",
                IdentityColumnIndex = 0
            };
            CsvTableSchema sourceSchema = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema reference = new CsvColumnSchema("ability")
            {
                ValueKind = CsvValueKind.Reference,
                TokenSyntax = new CsvTokenSyntax(CsvTokenExtractionMode.WholeCell)
            };
            reference.References.Add(new CsvReferenceSpec("abilities", "name"));
            sourceSchema.Columns.Add(reference);

            CsvTableController target = Open("abilities.csv", "name,name\nfire,display\n", targetSchema);
            CsvTableController source = Open("units.csv", "id,ability\nu1,fire\n", sourceSchema);

            IReadOnlyList<CsvDatasetDiagnostic> diagnostics = CsvDatasetValidator.Validate(source, target);

            Assert.IsFalse(HasCode(diagnostics, "REFERENCE_MISSING"));
            Assert.IsFalse(HasCode(diagnostics, "REFERENCE_AMBIGUOUS"));
        }

        private CsvTableController Open(string fileName, string contents, CsvTableSchema schema)
        {
            string path = Path.Combine(temporaryDirectory, fileName);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            CsvTableController controller = new CsvTableController(schema.Name, path, schema);
            controller.Open();
            return controller;
        }

        private static CsvDatasetDiagnostic Find(IReadOnlyList<CsvDatasetDiagnostic> diagnostics,
            string code, int record, int column)
        {
            for (int i = 0; i < diagnostics.Count; i++)
                if (diagnostics[i].Code == code
                    && diagnostics[i].PhysicalRecordIndex == record
                    && diagnostics[i].PhysicalColumnIndex == column) return diagnostics[i];
            Assert.Fail("Expected diagnostic " + code + " at " + record + "," + column);
            return null;
        }

        private static void AssertDiagnostic(IReadOnlyList<CsvDatasetDiagnostic> diagnostics,
            string code, int record, int column)
        {
            Find(diagnostics, code, record, column);
        }

        private static int FindCode(IReadOnlyList<CsvDatasetDiagnostic> diagnostics, string code)
        {
            for (int i = 0; i < diagnostics.Count; i++) if (diagnostics[i].Code == code) return i;
            Assert.Fail("Expected diagnostic " + code);
            return -1;
        }

        private static bool HasCode(IReadOnlyList<CsvDatasetDiagnostic> diagnostics, string code)
        {
            for (int i = 0; i < diagnostics.Count; i++) if (diagnostics[i].Code == code) return true;
            return false;
        }
    }
}
