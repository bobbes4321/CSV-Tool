using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CsvTool.Core;

namespace CsvTool.Editor.Recovery
{
    /// <summary>
    /// A durable, deliberately small journal for edits which would otherwise be lost during
    /// an editor/domain reload. Journals contain only effective cell changes; the CSV itself
    /// remains the source of truth.
    /// </summary>
    public static class CsvRecoveryJournal
    {
        private const int FormatMagic = 0x314A5643; // ASCII "CVJ1" in little-endian form.
        private const ushort FormatVersion = 1;
        private const int FingerprintLength = 32;
        private const int MaxPathBytes = 32768;
        private const int MaxValueBytes = 16 * 1024 * 1024;
        private const int MaxChangeCount = 1000000;
        private const long MaxJournalBytes = 128L * 1024L * 1024L;
        private const string JournalExtension = ".csvtool-recovery";

        public const string DefaultDirectoryName = "Library/CsvTool/Recovery";

        public enum Compatibility
        {
            Compatible,
            Missing,
            Invalid,
            SourcePathMismatch,
            SourceMissing,
            BaselineFingerprintMismatch
        }

        public sealed class Entry
        {
            private readonly List<CsvCellChange> changes;

            internal Entry(string sourcePath, byte[] baselineFingerprint, DateTime timestampUtc,
                List<CsvCellChange> changes)
            {
                SourcePath = sourcePath;
                BaselineFingerprint = (byte[])baselineFingerprint.Clone();
                TimestampUtc = timestampUtc;
                this.changes = changes;
            }

            public string SourcePath { get; private set; }
            public byte[] BaselineFingerprint { get; private set; }
            public DateTime TimestampUtc { get; private set; }
            public IReadOnlyList<CsvCellChange> Changes { get { return changes; } }
        }

        /// <summary>Returns the deterministic journal path for a normalized source path.</summary>
        public static string GetJournalPath(string sourcePath, string directory = null)
        {
            string normalized = NormalizePath(sourcePath);
            string root = string.IsNullOrEmpty(directory) ? DefaultDirectoryName : directory;
            root = Path.GetFullPath(root);
            byte[] pathBytes = Encoding.UTF8.GetBytes(normalized);
            byte[] hash;
            using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(pathBytes);
            StringBuilder name = new StringBuilder(hash.Length * 2 + JournalExtension.Length);
            for (int i = 0; i < hash.Length; i++) name.Append(hash[i].ToString("x2"));
            name.Append(JournalExtension);
            return Path.Combine(root, name.ToString());
        }

        /// <summary>Computes the SHA-256 fingerprint of the current bytes on disk.</summary>
        public static byte[] ComputeFingerprint(string sourcePath)
        {
            string normalized = NormalizePath(sourcePath);
            using (FileStream stream = new FileStream(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(stream);
        }

        /// <summary>
        /// Writes a journal using the current disk bytes as the baseline fingerprint. Callers
        /// that already hold the exact baseline can use the byte[] overload.
        /// </summary>
        public static void Write(string sourcePath, IEnumerable<CsvCellChange> changes, string directory = null)
        {
            Write(sourcePath, ComputeFingerprint(sourcePath), changes, DateTime.UtcNow, directory);
        }

        public static void Write(string sourcePath, IEnumerable<CsvCellChange> changes,
            DateTime timestampUtc, string directory = null)
        {
            Write(sourcePath, ComputeFingerprint(sourcePath), changes, timestampUtc, directory);
        }

        public static void Write(string sourcePath, byte[] baselineFingerprint,
            IEnumerable<CsvCellChange> changes, DateTime timestampUtc, string directory = null)
        {
            string normalized = NormalizePath(sourcePath);
            ValidateFingerprint(baselineFingerprint);
            List<CsvCellChange> snapshot = CopyAndValidateChanges(changes);
            string path = GetJournalPath(normalized, directory);
            string root = Path.GetDirectoryName(path);
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);

            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
                    try
                    {
                        writer.Write(FormatMagic);
                        writer.Write(FormatVersion);
                        WriteString(writer, normalized, MaxPathBytes);
                        writer.Write(timestampUtc.ToUniversalTime().Ticks);
                        writer.Write(baselineFingerprint);
                        writer.Write(snapshot.Count);
                        for (int i = 0; i < snapshot.Count; i++)
                        {
                            CsvCellChange change = snapshot[i];
                            writer.Write(change.RecordIndex);
                            writer.Write(change.ColumnIndex);
                            WriteString(writer, change.OriginalValue, MaxValueBytes);
                            WriteString(writer, change.CurrentValue, MaxValueBytes);
                        }
                        writer.Flush();
                        stream.Flush(true);
                    }
                    finally
                    {
                        writer.Dispose();
                    }
                }

                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static bool TryRead(string sourcePath, out Entry entry, string directory = null)
        {
            entry = null;
            string path;
            try { path = GetJournalPath(sourcePath, directory); }
            catch (Exception) { return false; }
            if (!File.Exists(path)) return false;

            try
            {
                FileInfo info = new FileInfo(path);
                if (info.Length < 4 || info.Length > MaxJournalBytes) return false;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadInt32() != FormatMagic || reader.ReadUInt16() != FormatVersion) return false;
                    string storedPath = ReadString(reader, MaxPathBytes);
                    string requestedPath = NormalizePath(sourcePath);
                    if (!CsvDocument.PathsEqualForConflict(storedPath, requestedPath)) return false;
                    DateTime timestamp = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                    byte[] fingerprint = ReadBytes(reader, FingerprintLength);
                    int count = reader.ReadInt32();
                    if (count < 0 || count > MaxChangeCount) return false;
                    List<CsvCellChange> changes = new List<CsvCellChange>(Math.Min(count, 1024));
                    HashSet<Coordinate> coordinates = new HashSet<Coordinate>();
                    for (int i = 0; i < count; i++)
                    {
                        int recordIndex = reader.ReadInt32();
                        int columnIndex = reader.ReadInt32();
                        if (recordIndex < 0 || columnIndex < 0) return false;
                        if (!coordinates.Add(new Coordinate(recordIndex, columnIndex))) return false;
                        string original = ReadString(reader, MaxValueBytes);
                        string current = ReadString(reader, MaxValueBytes);
                        changes.Add(new CsvCellChange(recordIndex, columnIndex, original, current));
                    }
                    if (stream.Position != stream.Length) return false;
                    entry = new Entry(storedPath, fingerprint, timestamp, changes);
                    return true;
                }
            }
            catch (Exception exception)
            {
                if (exception is IOException || exception is UnauthorizedAccessException ||
                    exception is EndOfStreamException || exception is ArgumentException ||
                    exception is FormatException || exception is DecoderFallbackException ||
                    exception is OverflowException || exception is CryptographicException)
                    return false;
                return false;
            }
        }

        public static Compatibility Validate(string sourcePath, Entry entry, string directory = null)
        {
            if (entry == null) return Compatibility.Invalid;
            try
            {
                string normalized = NormalizePath(sourcePath);
                if (!CsvDocument.PathsEqualForConflict(normalized, entry.SourcePath))
                    return Compatibility.SourcePathMismatch;
                if (!File.Exists(normalized)) return Compatibility.SourceMissing;
                byte[] current = ComputeFingerprint(normalized);
                return ByteArraysEqual(current, entry.BaselineFingerprint)
                    ? Compatibility.Compatible : Compatibility.BaselineFingerprintMismatch;
            }
            catch (Exception)
            {
                return Compatibility.Invalid;
            }
        }

        /// <summary>
        /// Applies all journal entries as one document operation after validating both the
        /// disk baseline and every in-memory original value. A failed preflight performs no
        /// mutation and returns false.
        /// </summary>
        public static bool TryApply(CsvDocument document, Entry entry, string directory = null)
        {
            if (document == null || entry == null) return false;
            if (Validate(document.SourcePath, entry, directory) != Compatibility.Compatible) return false;

            List<CsvCellAssignment> assignments = new List<CsvCellAssignment>(entry.Changes.Count);
            HashSet<Coordinate> coordinates = new HashSet<Coordinate>();
            for (int i = 0; i < entry.Changes.Count; i++)
            {
                CsvCellChange change = entry.Changes[i];
                if (change.RecordIndex < 0 || change.ColumnIndex < 0 ||
                    !coordinates.Add(new Coordinate(change.RecordIndex, change.ColumnIndex)) ||
                    document.GetCell(change.RecordIndex, change.ColumnIndex) != change.OriginalValue)
                    return false;
                assignments.Add(new CsvCellAssignment(change.RecordIndex, change.ColumnIndex, change.CurrentValue));
            }

            // Coordinates and original values have all been checked before this call. The core
            // therefore records the entire recovery as one undoable operation.
            return document.SetCells(assignments);
        }

        public static void Delete(string sourcePath, string directory = null)
        {
            string path = GetJournalPath(sourcePath, directory);
            if (File.Exists(path)) File.Delete(path);
        }

        private static string NormalizePath(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("A source path is required.", "sourcePath");
            return Path.GetFullPath(sourcePath);
        }

        private static void ValidateFingerprint(byte[] fingerprint)
        {
            if (fingerprint == null || fingerprint.Length != FingerprintLength)
                throw new ArgumentException("A SHA-256 fingerprint is required.", "baselineFingerprint");
        }

        private static List<CsvCellChange> CopyAndValidateChanges(IEnumerable<CsvCellChange> changes)
        {
            if (changes == null) throw new ArgumentNullException("changes");
            List<CsvCellChange> result = new List<CsvCellChange>();
            HashSet<Coordinate> coordinates = new HashSet<Coordinate>();
            foreach (CsvCellChange change in changes)
            {
                if (result.Count >= MaxChangeCount) throw new ArgumentException("Too many recovery changes.", "changes");
                if (change.RecordIndex < 0 || change.ColumnIndex < 0 ||
                    !coordinates.Add(new Coordinate(change.RecordIndex, change.ColumnIndex)))
                    throw new ArgumentException("Recovery changes must contain unique non-negative coordinates.", "changes");
                ValidateString(change.OriginalValue, MaxValueBytes, "originalValue");
                ValidateString(change.CurrentValue, MaxValueBytes, "currentValue");
                result.Add(new CsvCellChange(change.RecordIndex, change.ColumnIndex,
                    change.OriginalValue, change.CurrentValue));
            }
            return result;
        }

        private static void ValidateString(string value, int maxBytes, string parameterName)
        {
            if (Encoding.UTF8.GetByteCount(value ?? string.Empty) > maxBytes)
                throw new ArgumentException("Recovery value is too large.", parameterName);
        }

        private static void WriteString(BinaryWriter writer, string value, int maxBytes)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > maxBytes) throw new InvalidDataException("Recovery string is too large.");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader, int maxBytes)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maxBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid recovery string length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private static byte[] ReadBytes(BinaryReader reader, int length)
        {
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid recovery fingerprint length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return bytes;
        }

        private static bool ByteArraysEqual(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            int difference = 0;
            for (int i = 0; i < first.Length; i++) difference |= first[i] ^ second[i];
            return difference == 0;
        }

        private struct Coordinate : IEquatable<Coordinate>
        {
            private readonly int recordIndex;
            private readonly int columnIndex;

            public Coordinate(int recordIndex, int columnIndex)
            {
                this.recordIndex = recordIndex;
                this.columnIndex = columnIndex;
            }

            public bool Equals(Coordinate other)
            {
                return recordIndex == other.recordIndex && columnIndex == other.columnIndex;
            }

            public override bool Equals(object obj)
            {
                return obj is Coordinate && Equals((Coordinate)obj);
            }

            public override int GetHashCode()
            {
                unchecked { return (recordIndex * 397) ^ columnIndex; }
            }
        }
    }
}
