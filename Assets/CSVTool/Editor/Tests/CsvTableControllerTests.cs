using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CsvTool.Core;
using CsvTool.Editor;
using CsvTool.Editor.Validation;
using CsvTool.Schema;
using NUnit.Framework;

namespace CsvTool.Editor.Tests
{
    public sealed class CsvTableControllerTests
    {
        private string temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "csvtool-editor-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
        }

        [Test]
        public void PhysicalToVisualMapExcludesHeaderAndPreservesPhysicalIndices()
        {
            CsvTableController controller = Open("name,value\n# note\n\nalpha,one\nbeta,two\n");

            Assert.AreEqual(0, controller.HeaderRecordIndex);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, controller.VisibleRecordIndices);
            Assert.IsFalse(controller.IsRecordVisible(0), "The header is not a visual data row.");
            Assert.AreEqual("alpha", controller.GetCell(3, 0));
        }

        [Test]
        public void SearchBuildsRowFilterAndCellMatchesAgainstPhysicalIndices()
        {
            CsvTableController controller = Open("name,value\n# note\n\nalpha,needle\nbeta,other\n");
            controller.SetSearchQuery("needle");

            CollectionAssert.AreEqual(new[] { 3 }, controller.VisibleRecordIndices);
            Assert.AreEqual(1, controller.SearchMatchCount);
            Assert.AreEqual(3, controller.SearchMatches[0].RecordIndex);
            Assert.AreEqual(1, controller.SearchMatches[0].ColumnIndex);
            Assert.AreEqual(3, controller.FindNextMatch(-1, -1, false));
        }

        [Test]
        public void NonDataRowsAreIncludedByDefaultAndCanBeHidden()
        {
            CsvTableController controller = Open("name\n# note\n\nalpha\n");
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, controller.VisibleRecordIndices);

            controller.IncludeNonDataRows = false;
            CollectionAssert.AreEqual(new[] { 3 }, controller.VisibleRecordIndices);
        }

        [Test]
        public void ReloadReplacesDocumentAndRebuildsSearchState()
        {
            string path = Write("name,value\nalpha,one\n");
            CsvTableController controller = new CsvTableController("table", path, null);
            controller.Open();
            Assert.AreEqual("one", controller.GetCell(1, 1));

            File.WriteAllText(path, "name,value\nbeta,two\n", new UTF8Encoding(false));
            controller.Reload();

            Assert.AreEqual("beta", controller.GetCell(1, 0));
            Assert.AreEqual("two", controller.GetCell(1, 1));
            CollectionAssert.AreEqual(new[] { 1 }, controller.VisibleRecordIndices);
            Assert.IsFalse(controller.HasExternalChange);
        }

        [Test]
        public void PollExternalChangeSurfacesModifiedFile()
        {
            string path = Write("name,value\nalpha,one\n");
            CsvTableController controller = new CsvTableController("table", path, null);
            controller.Open();
            Assert.IsFalse(controller.HasExternalChange);

            File.WriteAllText(path, "name,value\nalpha,two\n", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            controller.PollExternalChange();

            Assert.IsTrue(controller.HasExternalChange);
        }

        [Test]
        public void SetCellUsesPhysicalIndexAndPreservesExactWhitespace()
        {
            CsvTableController controller = Open("name,value\nalpha,old \n beta,keep\n");

            Assert.IsTrue(controller.SetCell(1, 1, "new  "));
            Assert.AreEqual("new  ", controller.GetCell(1, 1));
            Assert.AreEqual("name,value\nalpha,new  \n beta,keep\n", Encoding.UTF8.GetString(controller.Document.Serialize()));
            Assert.IsTrue(controller.IsDirty);
        }

        [Test]
        public void SetCellQuotesValuesWhenRequiredAndUndoRedoRestoresValue()
        {
            CsvTableController controller = Open("name,value\nalpha,old\n");

            Assert.IsTrue(controller.SetCell(1, 1, "a,b \"quoted\""));
            string expected = "name,value\nalpha," + "\"" + "a,b " + "\"\"" + "quoted" + "\"\"" + "\"" + "\n";
            Assert.AreEqual(expected, Encoding.UTF8.GetString(controller.Document.Serialize()));
            Assert.IsTrue(controller.CanUndo);
            Assert.IsFalse(controller.CanRedo);

            Assert.IsTrue(controller.Undo());
            Assert.AreEqual("old", controller.GetCell(1, 1));
            Assert.IsFalse(controller.IsDirty);
            Assert.IsFalse(controller.CanUndo);
            Assert.IsTrue(controller.CanRedo);

            Assert.IsTrue(controller.Redo());
            Assert.AreEqual("a,b \"quoted\"", controller.GetCell(1, 1));
            Assert.IsTrue(controller.IsDirty);
        }

        [Test]
        public void EditUndoRedoRebuildsSearchAndVisibleMaps()
        {
            CsvTableController controller = Open("name,value\nalpha,one\nbeta,two\n");
            controller.SetSearchQuery("needle");
            CollectionAssert.IsEmpty(controller.VisibleRecordIndices);

            controller.SetCell(2, 1, "needle");
            CollectionAssert.AreEqual(new[] { 2 }, controller.VisibleRecordIndices);
            Assert.AreEqual(1, controller.SearchMatchCount);
            Assert.AreEqual(2, controller.SearchMatches[0].RecordIndex);

            Assert.IsTrue(controller.Undo());
            CollectionAssert.IsEmpty(controller.VisibleRecordIndices);
            Assert.AreEqual(0, controller.SearchMatchCount);

            Assert.IsTrue(controller.Redo());
            CollectionAssert.AreEqual(new[] { 2 }, controller.VisibleRecordIndices);
            Assert.AreEqual(1, controller.SearchMatchCount);
        }

        [Test]
        public void InsertRowBeforeHeaderRebuildsHeaderProtectionAndPhysicalViewOnUndoRedo()
        {
            CsvTableController controller = Open("name,value\nalpha,one\n");
            Assert.AreEqual(0, controller.HeaderRecordIndex);
            int initialRevision = controller.StructuralRevision;

            Assert.AreEqual(0, controller.InsertDataRow(0));
            Assert.AreEqual(1, controller.HeaderRecordIndex);
            Assert.AreEqual("name", controller.GetCell(1, 0));
            CollectionAssert.AreEqual(new[] { 0, 2 }, controller.VisibleRecordIndices);
            Assert.Greater(controller.StructuralRevision, initialRevision);
            Assert.Throws<InvalidOperationException>(() => controller.SetCell(1, 0, "not a header edit"));
            Assert.IsTrue(controller.SetCell(0, 0, "inserted"));

            // Undo the inserted-row cell edit, then the structural insertion.
            Assert.IsTrue(controller.Undo());
            Assert.IsTrue(controller.Undo());
            Assert.AreEqual(0, controller.HeaderRecordIndex);
            CollectionAssert.AreEqual(new[] { 1 }, controller.VisibleRecordIndices);
            Assert.Throws<InvalidOperationException>(() => controller.SetCell(0, 0, "not a header edit"));

            Assert.IsTrue(controller.Redo());
            Assert.AreEqual(1, controller.HeaderRecordIndex);
            CollectionAssert.AreEqual(new[] { 0, 2 }, controller.VisibleRecordIndices);
            Assert.Throws<InvalidOperationException>(() => controller.SetCell(1, 0, "not a header edit"));
        }

        [Test]
        public void InsertColumnBeforeNameSelectedReadOnlyColumnReresolvesOnUndoRedo()
        {
            string path = Write("name,value\nalpha,one\n");
            CsvTableSchema schema = new CsvTableSchema("table", "table.csv");
            schema.Columns.Add(new CsvColumnSchema("name") { ReadOnly = true });
            CsvTableController controller = new CsvTableController("table", path, schema);
            controller.Open();

            controller.InsertColumn(0, "inserted");
            Assert.AreEqual("inserted", controller.GetCell(0, 0));
            Assert.AreEqual("name", controller.GetCell(0, 1));
            Assert.AreEqual(1, controller.ResolvedSchema.Columns[0].PhysicalIndex);
            Assert.Throws<InvalidOperationException>(() => controller.SetCell(1, 1, "blocked after insert"));
            Assert.IsTrue(controller.SetCell(1, 0, "allowed"));

            // Undo the allowed cell edit, then the structural insertion.
            Assert.IsTrue(controller.Undo());
            Assert.IsTrue(controller.Undo());
            Assert.AreEqual("name", controller.GetCell(0, 0));
            Assert.AreEqual(0, controller.ResolvedSchema.Columns[0].PhysicalIndex);
            Assert.Throws<InvalidOperationException>(() => controller.SetCell(1, 0, "blocked after undo"));

            // Return to the structural boundary and redo the insertion without
            // creating a new history branch from the undone state.
            Assert.IsTrue(controller.Redo());
            Assert.AreEqual("name", controller.GetCell(0, 1));
            Assert.AreEqual(1, controller.ResolvedSchema.Columns[0].PhysicalIndex);
            Assert.Throws<InvalidOperationException>(() => controller.SetCell(1, 1, "blocked after redo"));
        }

        [Test]
        public void SaveClearsDirtyStateAndRefreshesObservedFileState()
        {
            string path = Write("name,value\nalpha,old\n");
            CsvTableController controller = new CsvTableController("table", path, null);
            controller.Open();
            controller.SetCell(1, 1, "saved");

            controller.Save();

            Assert.IsFalse(controller.IsDirty);
            Assert.IsFalse(controller.CanUndo);
            Assert.IsFalse(controller.HasExternalChange);
            Assert.AreEqual("name,value\nalpha,saved\n", File.ReadAllText(path));
            controller.PollExternalChange();
            Assert.IsFalse(controller.HasExternalChange);
        }

        [Test]
        public void SavePropagatesSameLengthExternalConflict()
        {
            string path = Write("name,value\nalpha,old\n");
            CsvTableController controller = new CsvTableController("table", path, null);
            controller.Open();
            controller.SetCell(1, 1, "new");

            // Keep the byte length unchanged: the controller must rely on the
            // document snapshot, not only timestamp/length metadata.
            File.WriteAllText(path, "name,value\nalpha,oth\n", new UTF8Encoding(false));

            Assert.Throws<CsvExternalChangeException>(() => controller.Save());
            Assert.IsTrue(controller.HasExternalChange);
            Assert.IsTrue(controller.IsDirty);
            Assert.AreEqual("name,value\nalpha,oth\n", File.ReadAllText(path));
        }

        [Test]
        public void SaveGateBlocksInvalidDatasetWithoutWritingAndAllowsCorrectedData()
        {
            string path = Write("id,score\nunit,not-a-number\n");
            CsvTableSchema schema = new CsvTableSchema("table", "table.csv") { IdentityColumn = "id" };
            schema.Columns.Add(new CsvColumnSchema("score") { ValueKind = CsvValueKind.Integer, Required = true });
            CsvTableController controller = new CsvTableController("table", path, schema);
            controller.Open();
            controller.SetCell(1, 0, "unit-edited");

            CsvDatasetValidationException exception = Assert.Throws<CsvDatasetValidationException>(() => controller.Save());
            Assert.AreEqual("DATA_TYPE_INVALID", exception.Diagnostics[0].Code);
            Assert.AreEqual("id,score\nunit,not-a-number\n", File.ReadAllText(path),
                "The validation gate must run before any atomic-save write.");
            Assert.IsTrue(controller.IsDirty);

            controller.SetCell(1, 1, "42");
            controller.Save();
            Assert.AreEqual("id,score\nunit-edited,42\n", File.ReadAllText(path));
            Assert.IsFalse(controller.IsDirty);
        }

        [Test]
        public void SaveGateDoesNotBlockAnUnopenedUnrelatedWorkspaceTable()
        {
            string path = Write("id,value\nunit,old\n");
            CsvTableController current = new CsvTableController("current", path,
                new CsvTableSchema("current", "table.csv"));
            current.Open();
            current.SetCell(1, 1, "saved");
            CsvTableController unopened = new CsvTableController("unrelated",
                Path.Combine(temporaryDirectory, "unrelated.csv"),
                new CsvTableSchema("unrelated", "unrelated.csv"));

            current.Save(new[] { current, unopened });

            Assert.AreEqual("id,value\nunit,saved\n", File.ReadAllText(path));
            Assert.IsFalse(current.IsDirty);
        }

        [Test]
        public void FilteredPhysicalBatchEditsAreOneUndoableStep()
        {
            CsvTableController controller = Open("name,value\nalpha,one\nbeta,two\n");
            controller.SetSearchQuery("beta");
            CollectionAssert.AreEqual(new[] { 2 }, controller.VisibleRecordIndices);

            bool changed = controller.SetCells(new[]
            {
                new CsvCellAssignment(1, 1, "first"),
                new CsvCellAssignment(2, 1, "second")
            });

            Assert.IsTrue(changed);
            Assert.AreEqual("first", controller.GetCell(1, 1));
            Assert.AreEqual("second", controller.GetCell(2, 1));
            Assert.IsTrue(controller.CanUndo);
            Assert.IsTrue(controller.Undo());
            Assert.AreEqual("one", controller.GetCell(1, 1));
            Assert.AreEqual("two", controller.GetCell(2, 1));
            Assert.IsTrue(controller.CanRedo);
        }

        [Test]
        public void ProtectedBatchFailsAtomicallyAndStructuralToggleAllowsComments()
        {
            CsvTableController controller = Open("name,value\n# note,old\n\nalpha,one\n");
            Assert.Throws<InvalidOperationException>(() => controller.SetCells(new[]
            {
                new CsvCellAssignment(3, 1, "changed"),
                new CsvCellAssignment(1, 1, "should fail")
            }));
            Assert.AreEqual("one", controller.GetCell(3, 1));
            Assert.AreEqual("old", controller.GetCell(1, 1));
            Assert.IsFalse(controller.IsDirty);

            controller.AllowStructuralRowEdits = true;
            Assert.IsTrue(controller.SetCell(1, 1, "edited note"));
            Assert.AreEqual("edited note", controller.GetCell(1, 1));
            Assert.Throws<InvalidOperationException>(() => controller.SetCell(0, 0, "cannot edit header"));
            Assert.AreEqual("name", controller.GetCell(0, 0));
        }

        [Test]
        public void ReadOnlySchemaColumnIsEnforcedByHeaderNameAndBatchIsAtomic()
        {
            string path = Write("name,value\nalpha,one\nbeta,two\n");
            CsvTableSchema schema = new CsvTableSchema("table", "table.csv");
            schema.Columns.Add(new CsvColumnSchema("value") { ReadOnly = true });
            CsvTableController controller = new CsvTableController("table", path, schema);
            controller.Open();

            Assert.Throws<InvalidOperationException>(() => controller.SetCells(new[]
            {
                new CsvCellAssignment(1, 0, "changed name"),
                new CsvCellAssignment(2, 1, "blocked value")
            }));
            Assert.AreEqual("alpha", controller.GetCell(1, 0));
            Assert.AreEqual("two", controller.GetCell(2, 1));
            Assert.IsFalse(controller.IsDirty);
        }

        [Test]
        public void ChangesExposeEffectiveEditsAndRevertOperationsRefreshState()
        {
            CsvTableController controller = Open("name,value\nalpha,one\nbeta,two\n");
            Assert.AreEqual(0, controller.ChangeCount);

            controller.SetCells(new[]
            {
                new CsvCellAssignment(1, 0, "alpha revised"),
                new CsvCellAssignment(1, 1, "one revised")
            });
            Assert.AreEqual(2, controller.ChangeCount);
            Assert.AreEqual(2, controller.Changes.Count);

            Assert.IsTrue(controller.RevertCell(1, 0));
            Assert.AreEqual(1, controller.ChangeCount);
            Assert.AreEqual("alpha", controller.GetCell(1, 0));
            Assert.AreEqual("one revised", controller.GetCell(1, 1));

            Assert.IsTrue(controller.RevertAll());
            Assert.AreEqual(0, controller.ChangeCount);
            Assert.IsFalse(controller.IsDirty);
            Assert.AreEqual("one", controller.GetCell(1, 1));
            Assert.IsTrue(controller.CanUndo);
        }

        [Test]
        public void ConfiguredWorkspaceCanOptionallyIncludeUnlistedCsvFiles()
        {
            Write("name,value\nalpha,one\n");
            File.WriteAllText(Path.Combine(temporaryDirectory, "extra.csv"),
                "id,label\nx,Extra\n", new UTF8Encoding(false));
            CsvWorkspaceSchema schema = new CsvWorkspaceSchema { RootDirectory = temporaryDirectory };
            CsvTableSchema configured = new CsvTableSchema("Configured", "table.csv");
            configured.Columns.Add(new CsvColumnSchema("value") { ReadOnly = true });
            schema.Tables.Add(configured);

            CsvWorkspaceController strict = new CsvWorkspaceController(temporaryDirectory, schema, false);
            Assert.AreEqual(1, strict.Tables.Count);
            Assert.AreSame(configured, strict.Tables[0].Schema);

            CsvWorkspaceController inclusive = new CsvWorkspaceController(temporaryDirectory, schema, true);
            Assert.AreEqual(2, inclusive.Tables.Count);
            Assert.AreEqual(1, CountPath(inclusive, Path.Combine(temporaryDirectory, "table.csv")));
        }

        private static int CountPath(CsvWorkspaceController workspace, string path)
        {
            int count = 0;
            for (int i = 0; i < workspace.Tables.Count; i++)
                if (string.Equals(workspace.Tables[i].AbsolutePath, path,
                    StringComparison.OrdinalIgnoreCase)) count++;
            return count;
        }

        private CsvTableController Open(string contents)
        {
            string path = Write(contents);
            CsvTableController controller = new CsvTableController("table", path, null);
            controller.Open();
            return controller;
        }

        private string Write(string contents)
        {
            string path = Path.Combine(temporaryDirectory, "table.csv");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return path;
        }
    }
}
