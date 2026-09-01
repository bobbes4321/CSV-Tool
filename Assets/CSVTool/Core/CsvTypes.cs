using System;
using System.Collections.Generic;
using System.Text;

namespace CsvTool.Core
{
    /// <summary>The broad kind of a logical CSV record. The parser only applies classifications configured in CsvParseOptions.</summary>
    public enum CsvRecordKind
    {
        Unknown,
        Header,
        Data,
        Comment,
        Section,
        Blank
    }

    public enum CsvDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed class CsvParseDiagnostic
    {
        public CsvParseDiagnostic(CsvDiagnosticSeverity severity, string message, int recordIndex)
        {
            Severity = severity;
            Message = message ?? string.Empty;
            RecordIndex = recordIndex;
        }

        public CsvDiagnosticSeverity Severity { get; private set; }
        public string Message { get; private set; }
        public int RecordIndex { get; private set; }

        public override string ToString()
        {
            return string.Format("{0} (record {1}): {2}", Severity, RecordIndex + 1, Message);
        }
    }

    /// <summary>
    /// Parsing policy. This deliberately contains no assumptions about a particular project's columns or IDs.
    /// Prefixes and detectors can be supplied by an editor/workspace configuration.
    /// </summary>
    public sealed class CsvParseOptions
    {
        public CsvParseOptions()
        {
            CommentPrefixes = new[] { "#", "//" };
            SectionPrefixes = Array.Empty<string>();
            HasHeader = true;
            TrimClassificationWhitespace = true;
            StrictQuotes = false;
        }

        public bool HasHeader { get; set; }
        public bool TrimClassificationWhitespace { get; set; }
        public bool StrictQuotes { get; set; }
        public IReadOnlyList<string> CommentPrefixes { get; set; }
        public IReadOnlyList<string> SectionPrefixes { get; set; }

        /// <summary>Optional project-specific classification without baking project rules into the core.</summary>
        public Func<IReadOnlyList<string>, bool> CommentDetector { get; set; }
        public Func<IReadOnlyList<string>, bool> SectionDetector { get; set; }
    }

    public sealed class CsvEncodingInfo
    {
        private CsvEncodingInfo(Encoding encoding, bool hasBom)
        {
            Encoding = encoding;
            HasBom = hasBom;
        }

        public Encoding Encoding { get; private set; }
        public bool HasBom { get; private set; }

        public static CsvEncodingInfo Detect(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");

            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return new CsvEncodingInfo(new UTF32Encoding(true, false, true), true);
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return new CsvEncodingInfo(new UTF32Encoding(false, false, true), true);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new CsvEncodingInfo(new UTF8Encoding(false, true), true);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new CsvEncodingInfo(new UnicodeEncoding(false, false, true), true);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new CsvEncodingInfo(new UnicodeEncoding(true, false, true), true);

            return new CsvEncodingInfo(new UTF8Encoding(false, true), false);
        }

        public string Decode(byte[] bytes)
        {
            int offset = HasBom ? GetPreambleLength() : 0;
            if (offset > bytes.Length) offset = 0;
            return Encoding.GetString(bytes, offset, bytes.Length - offset);
        }

        public byte[] Encode(string text)
        {
            byte[] content = Encoding.GetBytes(text ?? string.Empty);
            if (!HasBom) return content;

            byte[] preamble = GetPreamble();
            byte[] result = new byte[preamble.Length + content.Length];
            Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            Buffer.BlockCopy(content, 0, result, preamble.Length, content.Length);
            return result;
        }

        private byte[] GetPreamble()
        {
            if (Encoding is UTF8Encoding) return new byte[] { 0xEF, 0xBB, 0xBF };
            if (Encoding is UnicodeEncoding)
                return Encoding.CodePage == 1200 ? new byte[] { 0xFF, 0xFE } : new byte[] { 0xFE, 0xFF };
            if (Encoding is UTF32Encoding)
                return Encoding.CodePage == 12001 ? new byte[] { 0x00, 0x00, 0xFE, 0xFF } : new byte[] { 0xFF, 0xFE, 0x00, 0x00 };
            return Encoding.GetPreamble();
        }

        private int GetPreambleLength()
        {
            return GetPreamble().Length;
        }
    }

    public sealed class CsvExternalChangeException : InvalidOperationException
    {
        public CsvExternalChangeException(string path)
            : base("The CSV changed on disk since it was opened: " + path)
        {
            Path = path;
        }

        public string Path { get; private set; }
    }
}
