using System;
using System.Collections.Generic;
using CsvTool.Schema;

namespace CsvTool.Editor
{
    /// <summary>
    /// Explicit, optional layout metadata for the record view. Projects can
    /// provide this small list when a table needs a curated order or groups;
    /// the CSV and table schema remain the source of truth for values.
    /// </summary>
    public sealed class CsvRecordViewDefinition
    {
        private readonly List<CsvRecordFieldDefinition> fields = new List<CsvRecordFieldDefinition>();

        public CsvRecordViewDefinition()
        {
            IncludeUnconfiguredColumns = true;
            UnconfiguredGroupName = "Other";
        }

        public bool IncludeUnconfiguredColumns { get; set; }
        public string UnconfiguredGroupName { get; set; }
        public IList<CsvRecordFieldDefinition> Fields { get { return fields; } }
    }

    /// <summary>One explicit column placement/override in a record view.</summary>
    public sealed class CsvRecordFieldDefinition
    {
        public CsvRecordFieldDefinition(string columnName, string groupName)
        {
            ColumnName = columnName ?? string.Empty;
            ColumnIndex = -1;
            GroupName = groupName ?? string.Empty;
            ValueKindOverride = null;
        }

        public CsvRecordFieldDefinition(int columnIndex, string groupName)
            : this(string.Empty, groupName)
        {
            ColumnIndex = columnIndex;
        }

        public string ColumnName { get; set; }
        public int ColumnIndex { get; set; }
        public string GroupName { get; set; }
        public string DisplayName { get; set; }
        public string HelpText { get; set; }
        public CsvValueKind? ValueKindOverride { get; set; }
        public bool? HiddenOverride { get; set; }
        public bool? ReadOnlyOverride { get; set; }
        public bool? RequiredOverride { get; set; }
    }

    /// <summary>Resolved field metadata used by both GUI and integration code.</summary>
    public sealed class CsvRecordField
    {
        internal CsvRecordField(int columnIndex, string header, string groupName)
        {
            ColumnIndex = columnIndex;
            Header = header ?? string.Empty;
            GroupName = string.IsNullOrEmpty(groupName) ? "General" : groupName;
            DisplayName = string.IsNullOrEmpty(Header) ? "Column " + (columnIndex + 1) : Header;
            ValueKind = CsvValueKind.Text;
            EnumValues = new List<string>();
        }

        public int ColumnIndex { get; private set; }
        public string Header { get; private set; }
        public string GroupName { get; internal set; }
        public string DisplayName { get; internal set; }
        public string HelpText { get; internal set; }
        public CsvValueKind ValueKind { get; internal set; }
        public bool Hidden { get; internal set; }
        public bool ReadOnly { get; internal set; }
        public bool Required { get; internal set; }
        public double? Minimum { get; internal set; }
        public double? Maximum { get; internal set; }
        public IList<string> EnumValues { get; private set; }
    }

    /// <summary>Resolved, ordered fields in one visible group.</summary>
    public sealed class CsvRecordFieldGroup
    {
        private readonly List<CsvRecordField> fields = new List<CsvRecordField>();

        internal CsvRecordFieldGroup(string name)
        {
            Name = string.IsNullOrEmpty(name) ? "General" : name;
        }

        public string Name { get; private set; }
        public IReadOnlyList<CsvRecordField> Fields { get { return fields; } }
        internal List<CsvRecordField> MutableFields { get { return fields; } }
    }

    /// <summary>
    /// Pure resolution helpers. Explicit field definitions are intentionally
    /// optional: a schema-only table still gets a useful form, while projects
    /// can declare exact order/groups when that is valuable.
    /// </summary>
    public static class CsvRecordFieldResolver
    {
        public static IReadOnlyList<CsvRecordFieldGroup> Resolve(IReadOnlyList<string> headers,
            CsvTableSchema schema, CsvRecordViewDefinition definition = null)
        {
            List<CsvRecordFieldGroup> groups = new List<CsvRecordFieldGroup>();
            if (headers == null) return groups;
            HashSet<int> used = new HashSet<int>();
            bool hasExplicitFields = definition != null && definition.Fields != null && definition.Fields.Count > 0;

            if (hasExplicitFields)
            {
                for (int i = 0; i < definition.Fields.Count; i++)
                {
                    CsvRecordFieldDefinition item = definition.Fields[i];
                    if (item == null) continue;
                    int index = ResolveIndex(headers, item.ColumnIndex, item.ColumnName);
                    if (index < 0 || used.Contains(index)) continue;
                    CsvRecordField field = CreateField(headers, index, schema, item);
                    used.Add(index);
                    AddToGroup(groups, field);
                }
            }
            else if (schema != null && schema.Columns != null && schema.Columns.Count > 0)
            {
                for (int i = 0; i < schema.Columns.Count; i++)
                {
                    CsvColumnSchema item = schema.Columns[i];
                    if (item == null) continue;
                    int index = ResolveIndex(headers, item.Index, item.Name);
                    if (index < 0 || used.Contains(index)) continue;
                    CsvRecordField field = CreateField(headers, index, schema, null);
                    used.Add(index);
                    AddToGroup(groups, field);
                }
            }

            bool includeUnconfigured = definition == null || definition.IncludeUnconfiguredColumns;
            if (includeUnconfigured)
            {
                string group = definition == null ? "Other" : definition.UnconfiguredGroupName;
                for (int i = 0; i < headers.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    CsvRecordField field = CreateField(headers, i, schema, null);
                    field.GroupName = string.IsNullOrEmpty(group) ? "Other" : group;
                    used.Add(i);
                    AddToGroup(groups, field);
                }
            }
            return groups;
        }

        public static int ResolveIndex(IReadOnlyList<string> headers, int index, string name)
        {
            if (headers == null) return -1;
            if (index >= 0) return index < headers.Count ? index : -1;
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static CsvRecordField CreateField(IReadOnlyList<string> headers, int index,
            CsvTableSchema schema, CsvRecordFieldDefinition definition)
        {
            string header = index >= 0 && index < headers.Count ? headers[index] : string.Empty;
            CsvColumnSchema metadata = FindSchemaColumn(schema, index, header);
            string group = definition == null ? "General" : definition.GroupName;
            CsvRecordField result = new CsvRecordField(index, header, group);

            result.DisplayName = metadata == null ? result.DisplayName : metadata.EffectiveDisplayName;
            result.HelpText = metadata == null ? string.Empty : (metadata.Description ?? string.Empty);
            result.ValueKind = metadata == null ? CsvValueKind.Text : metadata.ValueKind;
            result.Hidden = metadata != null && metadata.Hidden;
            result.ReadOnly = false;
            result.Required = metadata != null && metadata.Required;
            result.Minimum = metadata == null ? null : metadata.Minimum;
            result.Maximum = metadata == null ? null : metadata.Maximum;
            if (metadata != null && metadata.EnumValues != null)
                CopyEnumValues(result.EnumValues, metadata.EnumValues);

            if (definition != null)
            {
                if (!string.IsNullOrEmpty(definition.GroupName)) result.GroupName = definition.GroupName;
                if (!string.IsNullOrEmpty(definition.DisplayName)) result.DisplayName = definition.DisplayName;
                if (definition.HelpText != null) result.HelpText = definition.HelpText;
                if (definition.ValueKindOverride.HasValue) result.ValueKind = definition.ValueKindOverride.Value;
                if (definition.HiddenOverride.HasValue) result.Hidden = definition.HiddenOverride.Value;
                if (definition.RequiredOverride.HasValue) result.Required = definition.RequiredOverride.Value;
            }
            if (result.ValueKind == CsvValueKind.Auto) result.ValueKind = CsvValueKind.Text;
            return result;
        }

        private static CsvColumnSchema FindSchemaColumn(CsvTableSchema schema, int index, string header)
        {
            if (schema == null || schema.Columns == null) return null;
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                CsvColumnSchema item = schema.Columns[i];
                if (item == null) continue;
                if (item.Index >= 0 && item.Index == index) return item;
            }
            for (int i = 0; i < schema.Columns.Count; i++)
            {
                CsvColumnSchema item = schema.Columns[i];
                if (item != null && item.Index < 0 && !string.IsNullOrEmpty(item.Name) &&
                    string.Equals(item.Name, header, StringComparison.OrdinalIgnoreCase)) return item;
            }
            return null;
        }

        private static void CopyEnumValues(IList<string> destination, IList<string> source)
        {
            for (int i = 0; i < source.Count; i++)
                if (!string.IsNullOrEmpty(source[i])) destination.Add(source[i]);
        }

        private static void AddToGroup(IList<CsvRecordFieldGroup> groups, CsvRecordField field)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (string.Equals(groups[i].Name, field.GroupName, StringComparison.OrdinalIgnoreCase))
                {
                    groups[i].MutableFields.Add(field);
                    return;
                }
            }
            CsvRecordFieldGroup group = new CsvRecordFieldGroup(field.GroupName);
            group.MutableFields.Add(field);
            groups.Add(group);
        }
    }

    /// <summary>Culture-invariant parsers used by typed controls and tests.</summary>
    public static class CsvRecordValueParser
    {
        public static bool TryParseInteger(string value, out long result)
        {
            return long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out result);
        }

        public static bool TryParseDecimal(string value, out double result)
        {
            return double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result);
        }

        public static bool TryParseBoolean(string value, out bool result)
        {
            if (bool.TryParse(value, out result)) return true;
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "y", StringComparison.OrdinalIgnoreCase)) { result = true; return true; }
            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "n", StringComparison.OrdinalIgnoreCase)) { result = false; return true; }
            result = false;
            return false;
        }

        public static bool IsValidEnum(string value, IList<string> values, bool allowEmpty)
        {
            if (allowEmpty && string.IsNullOrEmpty(value)) return true;
            if (values == null) return false;
            for (int i = 0; i < values.Count; i++)
                if (string.Equals(value, values[i], StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool IsValid(CsvRecordField field, string value, out string error)
        {
            error = string.Empty;
            if (field == null) return true;
            if (field.Required && string.IsNullOrEmpty(value)) { error = "A value is required."; return false; }
            if (string.IsNullOrEmpty(value)) return true;
            long integer;
            double decimalValue;
            bool boolean;
            switch (field.ValueKind)
            {
                case CsvValueKind.Integer:
                    if (!TryParseInteger(value, out integer)) { error = "Enter a whole number."; return false; }
                    if (field.Minimum.HasValue && integer < field.Minimum.Value) { error = "Value is below the minimum."; return false; }
                    if (field.Maximum.HasValue && integer > field.Maximum.Value) { error = "Value is above the maximum."; return false; }
                    return true;
                case CsvValueKind.Decimal:
                    if (!TryParseDecimal(value, out decimalValue)) { error = "Enter a number using invariant decimal notation."; return false; }
                    if (field.Minimum.HasValue && decimalValue < field.Minimum.Value) { error = "Value is below the minimum."; return false; }
                    if (field.Maximum.HasValue && decimalValue > field.Maximum.Value) { error = "Value is above the maximum."; return false; }
                    return true;
                case CsvValueKind.Boolean:
                    if (!TryParseBoolean(value, out boolean)) { error = "Enter true/false, yes/no, or 1/0."; return false; }
                    return true;
                case CsvValueKind.Enum:
                    if (!IsValidEnum(value, field.EnumValues, false)) { error = "Choose one of the configured enum values."; return false; }
                    return true;
                default:
                    return true;
            }
        }
    }
}
