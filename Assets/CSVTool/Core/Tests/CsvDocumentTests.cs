using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace CsvTool.Core.Tests
{
    public sealed class CsvDocumentTests
    {
        [Test]
        public void ParsesMultilineAndDoubledQuotes()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("name,description\r\nA,\"line 1\r\nline 2\"\r\n");
            CsvDocument document = CsvDocument.Load(bytes);

            Assert.AreEqual(2, document.Records.Count);
            Assert.AreEqual("line 1\r\nline 2", document.GetCell(1, 1));
            Assert.AreEqual("name,description\r\nA,\"line 1\r\nline 2\"\r\n", Encoding.UTF8.GetString(document.Serialize()));
        }

        [Test]
        public void UnchangedDocumentReturnsOriginalBytesIncludingBom()
        {
            byte[] content = Encoding.UTF8.GetBytes("a,b\r\n1,2");
            byte[] bytes = new byte[content.Length + 3];
            bytes[0] = 0xEF; bytes[1] = 0xBB; bytes[2] = 0xBF;
            Buffer.BlockCopy(content, 0, bytes, 3, content.Length);

            CsvDocument document = CsvDocument.Load(bytes);
            CollectionAssert.AreEqual(bytes, document.Serialize());
        }

        [Test]
        public void EditUndoRedoAndSaveOnlyChangeValues()
        {
            byte[] original = Encoding.UTF8.GetBytes("h1,h2\nvalue,old\n");
            CsvDocument document = CsvDocument.Load(original);

            Assert.IsTrue(document.SetCell(1, 1, "new"));
            Assert.AreEqual("h1,h2\nvalue,new\n", Encoding.UTF8.GetString(document.Serialize()));
            Assert.IsTrue(document.Undo());
            Assert.IsFalse(document.IsDirty);
            CollectionAssert.AreEqual(original, document.Serialize());
            Assert.IsTrue(document.Redo());
            Assert.AreEqual("new", document.GetCell(1, 1));
        }

        [Test]
        public void UndoExtendedCellRestoresOriginalRowWidth()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("a,b\n1,2\n"));
            Assert.IsTrue(document.SetCell(1, 4, "extended"));
            Assert.AreEqual(5, document.Records[1].CellCount);
            Assert.IsTrue(document.Undo());
            Assert.AreEqual(2, document.Records[1].CellCount);
            Assert.IsFalse(document.IsDirty);
            Assert.IsTrue(document.Redo());
            Assert.AreEqual(5, document.Records[1].CellCount);
        }

        [Test]
        public void BlankClassificationRequiresAllFieldsEmpty()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("a,b,c\n,,\n,,later\n"));
            Assert.AreEqual(CsvRecordKind.Blank, document.Records[1].Kind);
            Assert.AreEqual(CsvRecordKind.Data, document.Records[2].Kind);
        }

        [Test]
        public void NoOpEditDoesNotDirtyOrChangeBytes()
        {
            byte[] original = Encoding.UTF8.GetBytes("a,b\r\n\"kept\" ,value\r\n");
            CsvDocument document = CsvDocument.Load(original);
            Assert.IsFalse(document.SetCell(1, 0, "kept "));
            Assert.IsFalse(document.IsDirty);
            CollectionAssert.AreEqual(original, document.Serialize());
        }

        [Test]
        public void RepeatedSavesPreserveNonCanonicalUntouchedRecord()
        {
            string path = Path.Combine(Path.GetTempPath(), "csvtool-" + Guid.NewGuid().ToString("N") + ".csv");
            byte[] original = Encoding.UTF8.GetBytes("h1,h2\r\n\"kept\" ,value\r\nchanged,old\r\n");
            try
            {
                File.WriteAllBytes(path, original);
                CsvDocument document = CsvDocument.LoadFromFile(path);
                document.SetCell(2, 1, "first");
                document.SaveToFile();
                document.SetCell(2, 1, "second");
                document.SaveToFile();

                string saved = Encoding.UTF8.GetString(File.ReadAllBytes(path));
                StringAssert.Contains("\"kept\" ,value", saved);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void DeletedOriginalSourceIsAnExternalChange()
        {
            string path = Path.Combine(Path.GetTempPath(), "csvtool-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes("a\n1\n"));
                CsvDocument document = CsvDocument.LoadFromFile(path);
                document.SetCell(1, 0, "2");
                File.Delete(path);
                Assert.Throws<CsvExternalChangeException>(() => document.SaveToFile());
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void ConflictPathComparisonUsesPlatformCaseRules()
        {
            string path = Path.Combine(Path.GetTempPath(), "CsvToolCaseProbe");
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT ||
                Environment.OSVersion.Platform == PlatformID.Win32S ||
                Environment.OSVersion.Platform == PlatformID.Win32Windows ||
                Environment.OSVersion.Platform == PlatformID.WinCE;
            bool expected = windows || string.Equals(Path.GetFullPath(path), Path.GetFullPath(path.ToUpperInvariant()), StringComparison.Ordinal);
            Assert.AreEqual(expected, CsvDocument.PathsEqualForConflict(path, path.ToUpperInvariant()));
            Assert.IsTrue(CsvDocument.PathsEqualForConflict(path, path));
        }

        [Test]
        public void ClassifiesConfiguredCommentsAndSections()
        {
            CsvParseOptions options = new CsvParseOptions
            {
                CommentPrefixes = new[] { ";" },
                SectionPrefixes = new[] { "[" }
            };
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("name\n; note\n[Units]\nrifle\n"), options);

            Assert.AreEqual(CsvRecordKind.Header, document.Records[0].Kind);
            Assert.AreEqual(CsvRecordKind.Comment, document.Records[1].Kind);
            Assert.AreEqual(CsvRecordKind.Section, document.Records[2].Kind);
            Assert.AreEqual(CsvRecordKind.Data, document.Records[3].Kind);
        }

        [Test]
        public void ProjectCsvCorpusRoundTripsByteForByteWhenUnchanged()
        {
            string root = FindProjectRoot(TestContext.CurrentContext.TestDirectory);
            if (root == null) Assert.Ignore("Assets/Data was not found from the test runner directory.");
            string dataPath = Path.Combine(root, "Assets", "Data");
            string[] paths = Directory.GetFiles(dataPath, "*.csv");
            Assert.That(paths.Length, Is.GreaterThan(0));
            for (int i = 0; i < paths.Length; i++)
            {
                byte[] bytes = File.ReadAllBytes(paths[i]);
                CsvDocument document = CsvDocument.Load(bytes);
                CollectionAssert.AreEqual(bytes, document.Serialize(), Path.GetFileName(paths[i]));
            }
        }

        [Test]
        public void TrailingEmptyCellsAreRetained()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("a,b,,\n1,2,,\n"));
            Assert.AreEqual(4, document.Records[0].CellCount);
            Assert.AreEqual(4, document.Records[1].CellCount);
            Assert.AreEqual(string.Empty, document.GetCell(1, 3));
        }

        [Test]
        public void BatchAssignmentsAreOneUndoStepAndIgnoreNoOps()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n"));
            bool changed = document.SetCells(new[]
            {
                new CsvCellAssignment(1, 0, "one"),
                new CsvCellAssignment(1, 1, "two"),
                new CsvCellAssignment(1, 2, "3"),
                new CsvCellAssignment(1, 0, "final")
            });

            Assert.IsTrue(changed);
            Assert.AreEqual(1, document.History.Count);
            Assert.AreEqual("final", document.GetCell(1, 0));
            Assert.AreEqual("two", document.GetCell(1, 1));
            Assert.IsTrue(document.Undo());
            Assert.AreEqual("1", document.GetCell(1, 0));
            Assert.AreEqual("2", document.GetCell(1, 1));
            Assert.IsFalse(document.Undo());
            Assert.IsTrue(document.Redo());
            Assert.AreEqual("final", document.GetCell(1, 0));
        }

        [Test]
        public void BatchPreservesQuotedAndMultilineValuesAcrossUndoRedo()
        {
            byte[] original = Encoding.UTF8.GetBytes("name,description\r\nA,old\r\n");
            CsvDocument document = CsvDocument.Load(original);
            Assert.IsTrue(document.SetCells(new[]
            {
                new CsvCellAssignment(1, 0, "A, revised"),
                new CsvCellAssignment(1, 1, "first line\r\nsecond \"line\"")
            }));

            string edited = Encoding.UTF8.GetString(document.Serialize());
            StringAssert.Contains("\"A, revised\"", edited);
            StringAssert.Contains("\"first line\r\nsecond \"\"line\"\"\"", edited);
            Assert.IsTrue(document.Undo());
            CollectionAssert.AreEqual(original, document.Serialize());
            Assert.IsTrue(document.Redo());
            Assert.AreEqual("first line\r\nsecond \"line\"", document.GetCell(1, 1));
        }

        [Test]
        public void BatchExtensionUndoRedoRestoresEveryRowWidth()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("a,b\n1,2\n3,4\n"));
            Assert.IsTrue(document.SetCells(new[]
            {
                new CsvCellAssignment(1, 4, "r1"),
                new CsvCellAssignment(1, 5, "r1b"),
                new CsvCellAssignment(2, 3, "r2")
            }));
            Assert.AreEqual(6, document.Records[1].CellCount);
            Assert.AreEqual(4, document.Records[2].CellCount);
            Assert.IsTrue(document.Undo());
            Assert.AreEqual(2, document.Records[1].CellCount);
            Assert.AreEqual(2, document.Records[2].CellCount);
            Assert.IsTrue(document.Redo());
            Assert.AreEqual("r1b", document.GetCell(1, 5));
            Assert.AreEqual("r2", document.GetCell(2, 3));
        }

        [Test]
        public void InsertedRecordUsesPhysicalIndexAndUndoRestoresOriginalBytes()
        {
            byte[] original = Encoding.UTF8.GetBytes("name,value\r\nalpha,one\r\nbeta,two");
            CsvDocument document = CsvDocument.Load(original);

            document.InsertRecord(2, new[] { "new", "row" });
            Assert.AreEqual("new", document.GetCell(2, 0));
            Assert.AreEqual("beta", document.GetCell(3, 0));
            Assert.AreEqual("name,value\r\nalpha,one\r\nnew,row\r\nbeta,two", Encoding.UTF8.GetString(document.Serialize()));

            Assert.IsTrue(document.Undo());
            Assert.IsFalse(document.IsDirty);
            CollectionAssert.AreEqual(original, document.Serialize());
        }

        [Test]
        public void InsertedColumnHandlesRaggedRowsAndUndoRestoresWidths()
        {
            byte[] original = Encoding.UTF8.GetBytes("a,b\n1\n2,3\n");
            CsvDocument document = CsvDocument.Load(original);

            document.InsertColumn(1, new[] { 0, 1, 2 }, "new");
            Assert.AreEqual("a,new,b\n1,\n2,,3\n", Encoding.UTF8.GetString(document.Serialize()));
            Assert.AreEqual(2, document.Records[1].CellCount);

            Assert.IsTrue(document.Undo());
            Assert.AreEqual(1, document.Records[1].CellCount);
            CollectionAssert.AreEqual(original, document.Serialize());
        }

        [Test]
        public void ChangesExposeOriginalAndCurrentValuesAndRevertCellIsUndoable()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("a,b\nold,keep\n"));
            Assert.IsTrue(document.SetCell(1, 0, "new"));
            IReadOnlyList<CsvCellChange> changes = document.Changes;
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual(1, changes[0].RecordIndex);
            Assert.AreEqual(0, changes[0].ColumnIndex);
            Assert.AreEqual("old", changes[0].OriginalValue);
            Assert.AreEqual("new", changes[0].CurrentValue);

            Assert.IsTrue(document.RevertCell(1, 0));
            Assert.IsFalse(document.IsDirty);
            Assert.AreEqual(2, document.History.Count);
            Assert.IsTrue(document.Undo());
            Assert.AreEqual("new", document.GetCell(1, 0));
            Assert.IsTrue(document.Redo());
            Assert.AreEqual("old", document.GetCell(1, 0));
        }

        [Test]
        public void RevertAllIsOneUndoableStepAndRestoresExtendedRows()
        {
            CsvDocument document = CsvDocument.Load(Encoding.UTF8.GetBytes("a,b\nold,keep\n"));
            document.SetCells(new[]
            {
                new CsvCellAssignment(1, 0, "new"),
                new CsvCellAssignment(1, 4, "extra")
            });
            Assert.AreEqual(2, document.Changes.Count);
            Assert.IsTrue(document.RevertAll());
            Assert.IsFalse(document.IsDirty);
            Assert.AreEqual(2, document.Records[1].CellCount);
            Assert.AreEqual(2, document.History.Count);
            Assert.IsTrue(document.Undo());
            Assert.AreEqual("new", document.GetCell(1, 0));
            Assert.AreEqual("extra", document.GetCell(1, 4));
            Assert.AreEqual(5, document.Records[1].CellCount);
            Assert.IsTrue(document.Redo());
            Assert.IsFalse(document.IsDirty);
        }

        private static string FindProjectRoot(string start)
        {
            DirectoryInfo directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Assets", "Data"))) return directory.FullName;
                directory = directory.Parent;
            }
            return null;
        }
    }
}
