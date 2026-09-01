using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CsvTool.Core;
using CsvTool.Editor.Recovery;
using NUnit.Framework;

namespace CsvTool.Editor.Recovery.Tests
{
    public sealed class CsvRecoveryJournalTests
    {
        private string temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "csvtool-recovery-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, true);
        }

        [Test]
        public void RoundTripPreservesPathFingerprintTimestampAndChanges()
        {
            string path = WriteCsv("name,value\nalpha,one\n");
            CsvDocument document = Load(path);
            document.SetCell(1, 1, "two");
            document.SetCell(1, 0, "beta");
            DateTime timestamp = new DateTime(638922816000000000L, DateTimeKind.Utc);

            CsvRecoveryJournal.Write(path, document.Changes, timestamp, temporaryDirectory);

            CsvRecoveryJournal.Entry entry;
            Assert.IsTrue(CsvRecoveryJournal.TryRead(path, out entry, temporaryDirectory));
            Assert.AreEqual(Path.GetFullPath(path), entry.SourcePath);
            Assert.AreEqual(timestamp, entry.TimestampUtc);
            CollectionAssert.AreEqual(CsvRecoveryJournal.ComputeFingerprint(path), entry.BaselineFingerprint);
            Assert.AreEqual(2, entry.Changes.Count);
            Assert.AreEqual(1, entry.Changes[0].RecordIndex);
            Assert.AreEqual(0, entry.Changes[0].ColumnIndex);
            Assert.AreEqual("alpha", entry.Changes[0].OriginalValue);
            Assert.AreEqual("beta", entry.Changes[0].CurrentValue);
            Assert.AreEqual(1, entry.Changes[1].ColumnIndex);
            Assert.AreEqual("one", entry.Changes[1].OriginalValue);
            Assert.AreEqual("two", entry.Changes[1].CurrentValue);
            Assert.AreEqual(CsvRecoveryJournal.Compatibility.Compatible,
                CsvRecoveryJournal.Validate(path, entry, temporaryDirectory));
        }

        [Test]
        public void JournalsAreIsolatedByNormalizedSourcePath()
        {
            string first = Path.Combine(temporaryDirectory, "first.csv");
            string second = Path.Combine(temporaryDirectory, "second.csv");
            File.WriteAllText(first, "name\none\n", new UTF8Encoding(false));
            File.WriteAllText(second, "name\ntwo\n", new UTF8Encoding(false));
            List<CsvCellChange> changes = new List<CsvCellChange>
            {
                new CsvCellChange(1, 0, "one", "changed")
            };

            CsvRecoveryJournal.Write(first, changes, temporaryDirectory);
            CsvRecoveryJournal.Entry entry;
            Assert.IsTrue(CsvRecoveryJournal.TryRead(first, out entry, temporaryDirectory));
            Assert.IsFalse(CsvRecoveryJournal.TryRead(second, out entry, temporaryDirectory));
            Assert.AreNotEqual(CsvRecoveryJournal.GetJournalPath(first, temporaryDirectory),
                CsvRecoveryJournal.GetJournalPath(second, temporaryDirectory));
        }

        [Test]
        public void DiskMismatchRejectsRecoveryWithoutChangingDocument()
        {
            string path = WriteCsv("name,one,two\nalpha,a,b\n");
            CsvDocument document = Load(path);
            List<CsvCellChange> changes = new List<CsvCellChange>
            {
                new CsvCellChange(1, 1, "a", "A"),
                new CsvCellChange(1, 2, "b", "B")
            };
            CsvRecoveryJournal.Write(path, changes, temporaryDirectory);
            File.WriteAllText(path, "name,one,two\nalpha,x,b\n", new UTF8Encoding(false));

            CsvRecoveryJournal.Entry entry;
            Assert.IsTrue(CsvRecoveryJournal.TryRead(path, out entry, temporaryDirectory));
            Assert.AreEqual(CsvRecoveryJournal.Compatibility.BaselineFingerprintMismatch,
                CsvRecoveryJournal.Validate(path, entry, temporaryDirectory));
            Assert.IsFalse(CsvRecoveryJournal.TryApply(document, entry, temporaryDirectory));
            Assert.AreEqual("a", document.GetCell(1, 1));
            Assert.AreEqual("b", document.GetCell(1, 2));
            Assert.IsFalse(document.IsDirty);
        }

        [Test]
        public void OriginalValueMismatchRejectsWholeGroupedRecovery()
        {
            string path = WriteCsv("name,one,two\nalpha,a,b\n");
            CsvDocument document = Load(path);
            List<CsvCellChange> changes = new List<CsvCellChange>
            {
                new CsvCellChange(1, 1, "a", "A"),
                new CsvCellChange(1, 2, "b", "B")
            };
            CsvRecoveryJournal.Write(path, changes, temporaryDirectory);
            document.SetCell(1, 1, "already changed");

            CsvRecoveryJournal.Entry entry;
            Assert.IsTrue(CsvRecoveryJournal.TryRead(path, out entry, temporaryDirectory));
            Assert.IsFalse(CsvRecoveryJournal.TryApply(document, entry, temporaryDirectory));
            Assert.AreEqual("already changed", document.GetCell(1, 1));
            Assert.AreEqual("b", document.GetCell(1, 2));
        }

        [Test]
        public void CompatibleRecoveryAppliesAllEntriesAsOneUndoableOperation()
        {
            string path = WriteCsv("name,one,two\nalpha,a,b\n");
            CsvDocument document = Load(path);
            List<CsvCellChange> changes = new List<CsvCellChange>
            {
                new CsvCellChange(1, 1, "a", "A"),
                new CsvCellChange(1, 2, "b", "B")
            };
            CsvRecoveryJournal.Write(path, changes, temporaryDirectory);

            CsvRecoveryJournal.Entry entry;
            Assert.IsTrue(CsvRecoveryJournal.TryRead(path, out entry, temporaryDirectory));
            Assert.IsTrue(CsvRecoveryJournal.TryApply(document, entry, temporaryDirectory));
            Assert.AreEqual("A", document.GetCell(1, 1));
            Assert.AreEqual("B", document.GetCell(1, 2));
            Assert.AreEqual(1, document.History.Count);
            Assert.IsTrue(document.Undo());
            Assert.AreEqual("a", document.GetCell(1, 1));
            Assert.AreEqual("b", document.GetCell(1, 2));
        }

        [Test]
        public void DeleteAndCorruptJournalAreReportedAsUnreadable()
        {
            string path = WriteCsv("name\none\n");
            CsvRecoveryJournal.Write(path,
                new[] { new CsvCellChange(1, 0, "one", "changed") }, temporaryDirectory);
            string journalPath = CsvRecoveryJournal.GetJournalPath(path, temporaryDirectory);
            CsvRecoveryJournal.Delete(path, temporaryDirectory);
            CsvRecoveryJournal.Entry entry;
            Assert.IsFalse(File.Exists(journalPath));
            Assert.IsFalse(CsvRecoveryJournal.TryRead(path, out entry, temporaryDirectory));

            CsvRecoveryJournal.Write(path,
                new[] { new CsvCellChange(1, 0, "one", "changed") }, temporaryDirectory);
            File.WriteAllBytes(journalPath, new byte[] { 0x43, 0x56, 0x4A });
            Assert.IsFalse(CsvRecoveryJournal.TryRead(path, out entry, temporaryDirectory));
        }

        private CsvDocument Load(string path)
        {
            return CsvDocument.LoadFromFile(path, new CsvParseOptions { HasHeader = true });
        }

        private string WriteCsv(string contents)
        {
            string path = Path.Combine(temporaryDirectory, "table.csv");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return path;
        }
    }
}
