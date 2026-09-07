using System;
using System.Collections.Generic;

namespace CsvTool.Schema
{
    /// <summary>
    /// Describes how a configured cell value is converted into one or more
    /// lookup tokens. The document/index layer performs the extraction.
    /// </summary>
    public enum CsvTokenExtractionMode
    {
        WholeCell = 0,
        Delimited = 1,
        FirstWhitespaceToken = 2,
        RegexCapture = 3
    }

    /// <summary>
    /// The interpretation requested for a column. Auto leaves the value as text
    /// until a consumer (for example the editor grid) applies inference.
    /// </summary>
    public enum CsvValueKind
    {
        Auto = 0,
        Text = 1,
        Integer = 2,
        Decimal = 3,
        Boolean = 4,
        Enum = 5,
        List = 6,
        Reference = 7,
        MultilineText = 8
    }

    /// <summary>
    /// Describes how a list/reference cell is split into addressable tokens.
    /// Separators are strings rather than chars because real-world CSV schemas
    /// commonly use values such as ";", "|", or " || ".
    /// </summary>
    public sealed class CsvTokenSyntax
    {
        /// <summary>Finite budget used by consumers that evaluate configured regular expressions.</summary>
        public static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

        public CsvTokenSyntax()
            : this(CsvTokenExtractionMode.WholeCell, string.Empty, true, true)
        {
        }

        public CsvTokenSyntax(string separator, bool trimWhitespace, bool ignoreEmptyTokens)
            : this(CsvTokenExtractionMode.Delimited, separator, trimWhitespace, ignoreEmptyTokens)
        {
        }

        public CsvTokenSyntax(CsvTokenExtractionMode mode)
            : this(mode, string.Empty, true, true)
        {
        }

        public CsvTokenSyntax(CsvTokenExtractionMode mode, string separator, bool trimWhitespace, bool ignoreEmptyTokens)
        {
            Mode = mode;
            Separator = separator ?? string.Empty;
            TrimWhitespace = trimWhitespace;
            IgnoreEmptyTokens = ignoreEmptyTokens;
            RegexCaptureGroup = 1;
        }

        public CsvTokenExtractionMode Mode { get; set; }

        public string Separator { get; set; }

        public bool TrimWhitespace { get; set; }

        public bool IgnoreEmptyTokens { get; set; }

        /// <summary>
        /// Optional regular expression used when Mode is RegexCapture. Group
        /// zero is the complete match; positive values select capture groups.
        /// </summary>
        public string RegexPattern { get; set; }

        public int RegexCaptureGroup { get; set; }

        public bool IsConfigured
        {
            get
            {
                switch (Mode)
                {
                    case CsvTokenExtractionMode.WholeCell:
                    case CsvTokenExtractionMode.FirstWhitespaceToken:
                        return true;
                    case CsvTokenExtractionMode.Delimited:
                        return !string.IsNullOrEmpty(Separator);
                    case CsvTokenExtractionMode.RegexCapture:
                        return !string.IsNullOrEmpty(RegexPattern) && RegexCaptureGroup >= 0;
                    default:
                        return false;
                }
            }
        }
    }

    /// <summary>
    /// An explicit link from one column to a key in another configured table.
    /// A column may have more than one reference when a value can resolve
    /// against several tables; consumers should report ambiguity in that case.
    /// </summary>
    public sealed class CsvReferenceSpec
    {
        public CsvReferenceSpec()
        {
        }

        public CsvReferenceSpec(string targetTable, string targetKeyColumn)
        {
            TargetTable = targetTable ?? string.Empty;
            TargetKeyColumn = targetKeyColumn ?? string.Empty;
        }

        public string TargetTable { get; set; }

        public string TargetKeyColumn { get; set; }

        /// <summary>
        /// Optional column shown in autocomplete and record view. The target
        /// key is still the value written to the CSV.
        /// </summary>
        public string TargetDisplayColumn { get; set; }

        /// <summary>
        /// If false, an unresolved value is allowed and should be shown as a
        /// warning rather than an error by validation consumers.
        /// </summary>
        public bool Required { get; set; }

        public bool IsConfigured
        {
            get
            {
                return !string.IsNullOrWhiteSpace(TargetTable)
                    && !string.IsNullOrWhiteSpace(TargetKeyColumn);
            }
        }
    }

    /// <summary>
    /// Explicit metadata for one header. Index is useful for blank or duplicate
    /// headers; when both Name and Index are provided, consumers should use the
    /// index as the unambiguous selector and use Name for display/diagnostics.
    /// </summary>
    public sealed class CsvColumnSchema
    {
        private readonly List<string> enumValues = new List<string>();
        private readonly List<CsvReferenceSpec> references = new List<CsvReferenceSpec>();

        public CsvColumnSchema()
        {
            Index = -1;
            ValueKind = CsvValueKind.Auto;
            TokenSyntax = new CsvTokenSyntax();
        }

        public CsvColumnSchema(string name)
            : this()
        {
            Name = name ?? string.Empty;
        }

        public CsvColumnSchema(int index)
            : this()
        {
            Index = index;
        }

        /// <summary>
        /// Header to match. Empty is valid only when Index identifies the
        /// column (needed for unnamed Google Sheets columns).
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Zero-based header index, or -1 to match by Name.
        /// </summary>
        public int Index { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public CsvValueKind ValueKind { get; set; }

        public bool Hidden { get; set; }

        public bool ReadOnly { get; set; }

        public bool Required { get; set; }

        public double? Minimum { get; set; }

        public double? Maximum { get; set; }

        public CsvTokenSyntax TokenSyntax { get; set; }

        /// <summary>
        /// Values offered by enum autocomplete. The list is intentionally
        /// explicit; this avoids making assumptions from a project's data.
        /// </summary>
        public IList<string> EnumValues
        {
            get { return enumValues; }
        }

        public IList<CsvReferenceSpec> References
        {
            get { return references; }
        }

        public bool HasSelector
        {
            get { return Index >= 0 || !string.IsNullOrWhiteSpace(Name); }
        }

        public bool HasReferenceMetadata
        {
            get { return references.Count > 0; }
        }

        public string EffectiveDisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DisplayName))
                {
                    return DisplayName;
                }

                return Name ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// Describes one CSV file in a workspace. RelativePath is resolved relative
    /// to CsvWorkspaceSchema.RootDirectory by the file-system integration layer;
    /// this class itself performs no IO.
    /// </summary>
    public sealed class CsvTableSchema
    {
        private readonly List<CsvColumnSchema> columns = new List<CsvColumnSchema>();

        public CsvTableSchema()
        {
            Enabled = true;
            IdentityColumn = string.Empty;
            DisplayColumn = string.Empty;
            IdentityColumnIndex = -1;
            DisplayColumnIndex = -1;
        }

        public CsvTableSchema(string name, string relativePath)
            : this()
        {
            Name = name ?? string.Empty;
            RelativePath = relativePath ?? string.Empty;
        }

        public string Name { get; set; }

        public string RelativePath { get; set; }

        public string Description { get; set; }

        public bool Enabled { get; set; }

        /// <summary>
        /// Header name used as the stable row identity. Leave empty when using
        /// IdentityColumnIndex or when the table is not record-oriented.
        /// </summary>
        public string IdentityColumn { get; set; }

        /// <summary>
        /// Zero-based identity column index, or -1 to use IdentityColumn. When
        /// both are supplied, this index is authoritative and the name is kept
        /// as a human-readable hint/diagnostic label.
        /// </summary>
        public int IdentityColumnIndex { get; set; }

        /// <summary>
        /// Header name used in the record view and reference pickers. If empty,
        /// consumers may fall back to the identity selector.
        /// </summary>
        public string DisplayColumn { get; set; }

        /// <summary>
        /// Zero-based display column index, or -1 to use DisplayColumn. When
        /// both are supplied, this index is authoritative. Index selectors make
        /// unnamed or duplicate headers addressable without guessing.
        /// </summary>
        public int DisplayColumnIndex { get; set; }

        public IList<CsvColumnSchema> Columns
        {
            get { return columns; }
        }

        public CsvColumnSchema FindColumn(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            for (int i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return columns[i];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Project-independent workspace definition. It is deliberately a regular
    /// C# object: Unity-specific ScriptableObject/editor inspectors can wrap or
    /// serialize it later without forcing the CSV core to reference UnityEngine.
    /// </summary>
    public sealed class CsvWorkspaceSchema
    {
        private readonly List<CsvTableSchema> tables = new List<CsvTableSchema>();

        public CsvWorkspaceSchema()
        {
            RootDirectory = string.Empty;
            CaseSensitiveNames = false;
        }

        public string Name { get; set; }

        /// <summary>
        /// Directory used to resolve table RelativePath values. This can be an
        /// absolute path or a project-relative path; path policy belongs to IO.
        /// </summary>
        public string RootDirectory { get; set; }

        public bool CaseSensitiveNames { get; set; }

        public IList<CsvTableSchema> Tables
        {
            get { return tables; }
        }

        public CsvTableSchema FindTable(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            StringComparison comparison = CaseSensitiveNames
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            for (int i = 0; i < tables.Count; i++)
            {
                if (string.Equals(tables[i].Name, name, comparison))
                {
                    return tables[i];
                }
            }

            return null;
        }
    }
}

