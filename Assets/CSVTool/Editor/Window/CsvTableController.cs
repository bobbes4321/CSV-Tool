using System;
using System.Collections.Generic;
using System.IO;
using CsvTool.Core;
using CsvTool.Schema;

namespace CsvTool.Editor
{
    /// <summary>
    /// Editor-facing state for one discovered CSV. The controller deliberately
    /// owns no Unity objects, so it can also be reused by a future importer,
    /// validation window, or reference index.
    /// </summary>
    public sealed class CsvTableController
    {
        private readonly List<int> visibleRecords = new List<int>();
        private readonly List<CsvSearchMatch> searchMatches = new List<CsvSearchMatch>();
        private DateTime observedWriteTimeUtc;
        private long observedLength;
        private string searchQuery = string.Empty;
        private int headerRecordIndex = -1;

        public CsvTableController(string name, string absolutePath, CsvTableSchema schema,
            bool caseSensitiveNames = false)
        {
            Name = name ?? string.Empty;
            AbsolutePath = Path.GetFullPath(absolutePath ?? string.Empty);
            Schema = schema;
            CaseSensitiveNames = caseSensitiveNames;
        }

        public string Name { get; private set; }
        public string AbsolutePath { get; private set; }
        public CsvTableSchema Schema { get; private set; }
        public bool CaseSensitiveNames { get; private set; }
        public CsvResolvedTableSchema ResolvedSchema { get; private set; }
        public CsvDocument Document { get; private set; }
        public IReadOnlyList<int> VisibleRecordIndices { get { return visibleRecords; } }
        public IReadOnlyList<CsvSearchMatch> SearchMatches { get { return searchMatches; } }
        public IReadOnlyList<CsvCellChange> Changes
        {
            get { return Document == null ? EmptyChanges : Document.Changes; }
        }
        public int ChangeCount { get { return Document == null ? 0 : Document.Changes.Count; } }
        public string SearchQuery { get { return searchQuery; } }
        public bool IsLoaded { get { return Document != null; } }
        public bool IsDirty { get { return Document != null && Document.IsDirty; } }
        public bool CanUndo { get { return Document != null && Document.History.CanUndo; } }
        public bool CanRedo { get { return Document != null && Document.History.CanRedo; } }
        public bool HasExternalChange { get; private set; }
        public int HeaderRecordIndex { get { return headerRecordIndex; } }
        public int DataRowCount { get { return visibleRecords.Count; } }
        public int SearchMatchCount { get { return searchMatches.Count; } }

        /// <summary>
        /// Monotonically increases when a structural edit changes the physical
        /// document shape or header location. Editor views can use this signal
        /// to invalidate physical selection and layout caches as one operation.
        /// </summary>
        public int StructuralRevision { get; private set; }

        public bool IsRecordVisible(int recordIndex)
        {
            for (int i = 0; i < visibleRecords.Count; i++)
                if (visibleRecords[i] == recordIndex) return true;
            return false;
        }

        public IReadOnlyList<string> Headers
        {
            get
            {
                if (Document == null || headerRecordIndex < 0) return EmptyHeaders;
                return Document.Records[headerRecordIndex].Values;
            }
        }

        private static readonly IReadOnlyList<string> EmptyHeaders = new string[0];
        private static readonly IReadOnlyList<CsvCellChange> EmptyChanges = new CsvCellChange[0];

        public void Open()
        {
            Document = CsvDocument.LoadFromFile(AbsolutePath, new CsvParseOptions
            {
                HasHeader = true,
                TrimClassificationWhitespace = true,
                StrictQuotes = false
            });
            FindHeader();
            ResolveSchema();
            ObserveFile();
            HasExternalChange = false;
            RebuildSearch();
        }

        public void Reload()
        {
            Open();
        }

        /// <summary>Updates external-file state without reading the whole file each repaint.</summary>
        public void PollExternalChange()
        {
            if (string.IsNullOrEmpty(AbsolutePath)) return;
            try
            {
                DateTime writeTime = File.GetLastWriteTimeUtc(AbsolutePath);
                long length = File.Exists(AbsolutePath) ? new FileInfo(AbsolutePath).Length : -1L;
                HasExternalChange = writeTime != observedWriteTimeUtc || length != observedLength;
            }
            catch (IOException)
            {
                HasExternalChange = true;
            }
            catch (UnauthorizedAccessException)
            {
                HasExternalChange = true;
            }
        }

        public void SetSearchQuery(string query)
        {
            query = query ?? string.Empty;
            if (string.Equals(searchQuery, query, StringComparison.Ordinal)) return;
            searchQuery = query;
            RebuildSearch();
        }

        public string GetHeader(int columnIndex)
        {
            if (ResolvedSchema != null)
            {
                for (int i = 0; i < ResolvedSchema.Columns.Count; i++)
                {
                    CsvResolvedColumn column = ResolvedSchema.Columns[i];
                    if (column.IsResolved && column.PhysicalIndex == columnIndex && column.Schema != null &&
                        !string.IsNullOrWhiteSpace(column.Schema.DisplayName)) return column.Schema.DisplayName;
                }
                if (columnIndex >= 0 && columnIndex < ResolvedSchema.Labels.Count)
                    return ResolvedSchema.Labels[columnIndex];
            }
            string value = columnIndex >= 0 && columnIndex < Headers.Count ? Headers[columnIndex] : string.Empty;
            return string.IsNullOrEmpty(value) ? "Column " + (columnIndex + 1) : value;
        }

        public string GetCell(int recordIndex, int columnIndex)
        {
            return Document == null ? string.Empty : Document.GetCell(recordIndex, columnIndex);
        }

        /// <summary>
        /// Changes a cell addressed by its physical record and column indices.
        /// Physical indices are intentionally used here: filtering and hidden
        /// structural rows must never make an edit target a different record.
        /// </summary>
        public bool SetCell(int recordIndex, int columnIndex, string value)
        {
            return SetCells(new[] { new CsvCellAssignment(recordIndex, columnIndex, value) });
        }

        /// <summary>Returns the non-mutating permission error for one physical cell, if any.</summary>
        public string GetEditError(int recordIndex, int columnIndex)
        {
            try
            {
                ValidateEdit(recordIndex, columnIndex);
                return string.Empty;
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException ||
                exception is InvalidOperationException)
            {
                return exception.Message;
            }
        }

        /// <summary>Looks up configured metadata by its already-resolved physical column.</summary>
        public CsvColumnSchema GetConfiguredColumn(int physicalColumnIndex)
        {
            if (ResolvedSchema == null) return null;
            for (int i = 0; i < ResolvedSchema.Columns.Count; i++)
            {
                CsvResolvedColumn column = ResolvedSchema.Columns[i];
                if (column != null && column.IsResolved && column.PhysicalIndex == physicalColumnIndex)
                    return column.Schema;
            }
            return null;
        }

        /// <summary>
        /// Resolves a configuration selector to exactly one physical column. Raw headers and
        /// configured selectors participate; display labels never do, and ambiguity is an error.
        /// </summary>
        public bool TryResolvePhysicalColumn(string selector, out int physicalColumnIndex)
        {
            physicalColumnIndex = -1;
            if (string.IsNullOrWhiteSpace(selector) || ResolvedSchema == null) return false;
            List<int> candidates = new List<int>();
            StringComparison comparison = CaseSensitiveNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            CsvResolvedColumn identity = ResolvedSchema.Identity;
            if (identity != null && identity.IsResolved && Schema != null && Schema.IdentityColumnIndex >= 0 &&
                string.Equals(selector, Schema.IdentityColumn ?? string.Empty, comparison))
            {
                physicalColumnIndex = identity.PhysicalIndex;
                return true;
            }
            for (int i = 0; i < ResolvedSchema.Columns.Count; i++)
            {
                CsvResolvedColumn column = ResolvedSchema.Columns[i];
                if (column != null && column.IsResolved && column.Schema != null && column.Schema.Index >= 0 &&
                    string.Equals(selector, column.Schema.Name ?? string.Empty, comparison) &&
                    !candidates.Contains(column.PhysicalIndex)) candidates.Add(column.PhysicalIndex);
            }
            if (candidates.Count == 1)
            {
                physicalColumnIndex = candidates[0];
                return true;
            }
            if (candidates.Count > 1) return false;
            for (int i = 0; i < Headers.Count; i++)
                if (string.Equals(selector, Headers[i] ?? string.Empty, comparison)) candidates.Add(i);
            for (int i = 0; i < ResolvedSchema.Columns.Count; i++)
            {
                CsvResolvedColumn column = ResolvedSchema.Columns[i];
                if (column != null && column.IsResolved && column.Schema != null &&
                    string.Equals(selector, column.Schema.Name ?? string.Empty, comparison) &&
                    !candidates.Contains(column.PhysicalIndex)) candidates.Add(column.PhysicalIndex);
            }
            if (identity != null && identity.IsResolved && Schema != null &&
                string.Equals(selector, Schema.IdentityColumn ?? string.Empty, comparison) &&
                !candidates.Contains(identity.PhysicalIndex)) candidates.Add(identity.PhysicalIndex);
            if (candidates.Count != 1) return false;
            physicalColumnIndex = candidates[0];
            return true;
        }

        /// <summary>Inserts a data row before a physical document record, or appends at Records.Count.</summary>
        public int InsertDataRow(int physicalInsertIndex)
        {
            EnsureLoaded();
            if (physicalInsertIndex < 0 || physicalInsertIndex > Document.Records.Count)
                throw new ArgumentOutOfRangeException("physicalInsertIndex");
            List<string> values = new List<string>();
            for (int i = 0; i < Document.ColumnCount; i++) values.Add(string.Empty);
            Document.InsertRecord(physicalInsertIndex, values);
            RebuildStructuralState(true);
            return physicalInsertIndex;
        }

        /// <summary>Inserts a table column. Index-addressed schema is intentionally blocked because changing it silently would retarget metadata.</summary>
        public void InsertColumn(int physicalColumnIndex, string header)
        {
            EnsureLoaded();
            if (HasIndexBasedSchemaAtOrAfter(physicalColumnIndex))
                throw new InvalidOperationException("This table uses index-based schema metadata at or after this column. Update the workspace configuration explicitly before inserting a column.");
            List<int> records = new List<int>();
            for (int i = 0; i < Document.Records.Count; i++)
            {
                CsvRecordKind kind = Document.Records[i].Kind;
                if (kind == CsvRecordKind.Header || kind == CsvRecordKind.Data) records.Add(i);
            }
            Document.InsertColumn(physicalColumnIndex, records, header ?? string.Empty);
            RebuildStructuralState(true);
        }

        /// <summary>
        /// Applies a collection of physical cell assignments as one undoable
        /// operation. All coordinates and edit permissions are preflighted
        /// before the document is mutated, so a rejected batch is atomic.
        /// </summary>
        public bool SetCells(IEnumerable<CsvCellAssignment> assignments)
        {
            EnsureLoaded();
            if (assignments == null) throw new ArgumentNullException("assignments");

            List<CsvCellAssignment> materialized = new List<CsvCellAssignment>();
            foreach (CsvCellAssignment assignment in assignments)
            {
                ValidateEdit(assignment.RecordIndex, assignment.ColumnIndex);
                materialized.Add(assignment);
            }

            bool changed = Document.SetCells(materialized);
            if (changed) RebuildSearch();
            return changed;
        }

        /// <summary>Undoes the most recent cell edit and refreshes filtered views.</summary>
        public bool Undo()
        {
            EnsureLoaded();
            int previousRecordCount = Document.Records.Count;
            int previousColumnCount = Document.ColumnCount;
            int previousHeaderRecordIndex = headerRecordIndex;
            bool changed = Document.Undo();
            if (changed && HasStructuralShapeChanged(previousRecordCount, previousColumnCount, previousHeaderRecordIndex))
                RebuildStructuralState(true);
            else if (changed) RebuildSearch();
            return changed;
        }

        /// <summary>Redoes the next cell edit and refreshes filtered views.</summary>
        public bool Redo()
        {
            EnsureLoaded();
            int previousRecordCount = Document.Records.Count;
            int previousColumnCount = Document.ColumnCount;
            int previousHeaderRecordIndex = headerRecordIndex;
            bool changed = Document.Redo();
            if (changed && HasStructuralShapeChanged(previousRecordCount, previousColumnCount, previousHeaderRecordIndex))
                RebuildStructuralState(true);
            else if (changed) RebuildSearch();
            return changed;
        }

        /// <summary>Restores one changed physical cell to its loaded value.</summary>
        public bool RevertCell(int recordIndex, int columnIndex)
        {
            EnsureLoaded();
            bool changed = Document.RevertCell(recordIndex, columnIndex);
            if (changed) RebuildSearch();
            return changed;
        }

        /// <summary>Restores all changed cells as one undoable operation.</summary>
        public bool RevertAll()
        {
            EnsureLoaded();
            bool changed = Document.RevertAll();
            if (changed) RebuildSearch();
            return changed;
        }

        public bool RevertAllChanges()
        {
            return RevertAll();
        }

        /// <summary>
        /// Saves the current document with the core's external-change guard.
        /// A conflict is rethrown for the caller to handle and is also retained
        /// in controller state so the UI can show the warning immediately.
        /// </summary>
        public void Save()
        {
            Save(new[] { this });
        }

        /// <summary>Saves with the core's conflict-aware atomic write.</summary>
        public void Save(IEnumerable<CsvTableController> validationTables)
        {
            EnsureLoaded();
            try
            {
                Document.SaveToFile();
            }
            catch (CsvExternalChangeException)
            {
                HasExternalChange = true;
                throw;
            }

            // SaveToFile marks records clean and updates the document's source
            // snapshot. Refresh our inexpensive metadata snapshot as well so a
            // subsequent poll does not report our own write as external.
            ObserveFile();
            HasExternalChange = false;
        }

        public int FindNextMatch(int currentRecordIndex, int currentColumnIndex, bool backwards)
        {
            if (searchMatches.Count == 0) return -1;
            int selected = -1;
            for (int i = 0; i < searchMatches.Count; i++)
            {
                if (searchMatches[i].RecordIndex == currentRecordIndex && searchMatches[i].ColumnIndex == currentColumnIndex)
                {
                    selected = i;
                    break;
                }
            }
            int next = selected < 0 ? (backwards ? searchMatches.Count - 1 : 0) : selected + (backwards ? -1 : 1);
            if (next < 0) next = searchMatches.Count - 1;
            if (next >= searchMatches.Count) next = 0;
            return next < 0 ? -1 : searchMatches[next].RecordIndex;
        }

        public void ApplySchema(CsvTableSchema schema)
        {
            Schema = schema;
            if (Document != null) ResolveSchema();
        }

        /// <summary>Rebuilds filtered/search projections after a trusted external document operation.</summary>
        public void RefreshView()
        {
            EnsureLoaded();
            int previousRecordCount = Document.Records.Count;
            int previousColumnCount = Document.ColumnCount;
            int previousHeaderRecordIndex = headerRecordIndex;
            bool structuralChange = HasStructuralShapeChanged(previousRecordCount, previousColumnCount, previousHeaderRecordIndex);
            RebuildStructuralState(structuralChange);
        }

        private void RebuildStructuralState(bool structuralChange)
        {
            FindHeader();
            ResolveSchema();
            RebuildSearch();

            if (structuralChange) StructuralRevision++;
        }

        private bool HasStructuralShapeChanged(int previousRecordCount, int previousColumnCount,
            int previousHeaderRecordIndex)
        {
            if (Document == null) return false;
            return previousRecordCount != Document.Records.Count ||
                previousColumnCount != Document.ColumnCount ||
                previousHeaderRecordIndex != FindHeaderRecordIndex();
        }

        private int FindHeaderRecordIndex()
        {
            if (Document == null) return -1;
            for (int i = 0; i < Document.Records.Count; i++)
                if (Document.Records[i].Kind == CsvRecordKind.Header) return i;
            return Document.Records.Count > 0 ? 0 : -1;
        }

        private void FindHeader()
        {
            headerRecordIndex = -1;
            if (Document == null) return;
            for (int i = 0; i < Document.Records.Count; i++)
            {
                if (Document.Records[i].Kind == CsvRecordKind.Header)
                {
                    headerRecordIndex = i;
                    break;
                }
            }
            if (headerRecordIndex < 0 && Document.Records.Count > 0) headerRecordIndex = 0;
        }

        private void ResolveSchema()
        {
            List<string> headers = new List<string>(Headers.Count);
            for (int i = 0; i < Headers.Count; i++) headers.Add(Headers[i]);
            ResolvedSchema = CsvSchemaResolver.Resolve(Schema ?? new CsvTableSchema(Name,
                Path.GetFileName(AbsolutePath)), headers, CaseSensitiveNames);
        }

        private void EnsureLoaded()
        {
            if (Document == null) throw new InvalidOperationException("The CSV table is not loaded.");
        }

        private void ValidateEdit(int recordIndex, int columnIndex)
        {
            if (recordIndex < 0 || recordIndex >= Document.Records.Count)
                throw new ArgumentOutOfRangeException("recordIndex", "The physical CSV record index is outside the loaded document.");
            if (columnIndex < 0)
                throw new ArgumentOutOfRangeException("columnIndex", "The physical CSV column index cannot be negative.");
        }

        private bool HasIndexBasedSchemaAtOrAfter(int columnIndex)
        {
            if (Schema == null) return false;
            if (Schema.IdentityColumnIndex >= columnIndex || Schema.DisplayColumnIndex >= columnIndex) return true;
            if (Schema.Columns == null) return false;
            for (int i = 0; i < Schema.Columns.Count; i++)
                if (Schema.Columns[i] != null && Schema.Columns[i].Index >= columnIndex) return true;
            return false;
        }

        private void RebuildSearch()
        {
            visibleRecords.Clear();
            searchMatches.Clear();
            if (Document == null) return;
            int columnCount = Document.ColumnCount;
            for (int recordIndex = 0; recordIndex < Document.Records.Count; recordIndex++)
            {
                if (recordIndex == headerRecordIndex) continue;
                CsvRecord record = Document.Records[recordIndex];
                bool rowMatches = string.IsNullOrEmpty(searchQuery);
                for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    string value = record.GetValue(columnIndex);
                    if (!string.IsNullOrEmpty(searchQuery) && value.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        rowMatches = true;
                        searchMatches.Add(new CsvSearchMatch(recordIndex, columnIndex));
                    }
                }
                if (rowMatches) visibleRecords.Add(recordIndex);
            }
        }

        private void ObserveFile()
        {
            try
            {
                observedWriteTimeUtc = File.GetLastWriteTimeUtc(AbsolutePath);
                observedLength = File.Exists(AbsolutePath) ? new FileInfo(AbsolutePath).Length : -1L;
            }
            catch (IOException)
            {
                observedWriteTimeUtc = DateTime.MinValue;
                observedLength = -1L;
            }
        }
    }

    public struct CsvSearchMatch
    {
        public CsvSearchMatch(int recordIndex, int columnIndex)
        {
            RecordIndex = recordIndex;
            ColumnIndex = columnIndex;
        }

        public readonly int RecordIndex;
        public readonly int ColumnIndex;
    }

    /// <summary>Discovery and lifetime controller for a configured CSV workspace.</summary>
    public sealed class CsvWorkspaceController
    {
        private readonly List<CsvTableController> tables = new List<CsvTableController>();
        private CsvWorkspaceSchema configuredSchema;
        private readonly bool includeUnconfiguredTables;

        public CsvWorkspaceController(string folder)
        {
            SetFolder(folder);
        }

        public CsvWorkspaceController(string folder, CsvWorkspaceSchema schema, bool includeUnconfiguredTables = false)
        {
            configuredSchema = schema;
            this.includeUnconfiguredTables = includeUnconfiguredTables;
            SetFolder(folder);
        }

        public string Folder { get; private set; }
        public bool IsConfigured { get { return configuredSchema != null; } }
        public IReadOnlyList<CsvTableController> Tables { get { return tables; } }

        public void SetFolder(string folder)
        {
            Folder = string.IsNullOrEmpty(folder) ? string.Empty : Path.GetFullPath(folder);
            Discover();
        }

        public void Discover()
        {
            tables.Clear();
            if (string.IsNullOrEmpty(Folder) || !Directory.Exists(Folder)) return;
            if (configuredSchema != null)
            {
                DiscoverConfiguredTables();
                if (includeUnconfiguredTables) DiscoverAdHocTables(true);
                return;
            }
            DiscoverAdHocTables(false);
        }

        private void DiscoverAdHocTables(bool skipExisting)
        {
            string[] paths;
            try { paths = Directory.GetFiles(Folder, "*.csv", SearchOption.AllDirectories); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Length; i++)
            {
                if (skipExisting && ContainsPath(paths[i])) continue;
                string relative = GetRelativePath(Folder, paths[i]);
                string name = Path.ChangeExtension(relative, null).Replace('\\', '/');
                tables.Add(new CsvTableController(name, paths[i], new CsvTableSchema(name, relative),
                    configuredSchema != null && configuredSchema.CaseSensitiveNames));
            }
        }

        private bool ContainsPath(string absolutePath)
        {
            for (int i = 0; i < tables.Count; i++)
                if (string.Equals(tables[i].AbsolutePath, absolutePath, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void DiscoverConfiguredTables()
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < configuredSchema.Tables.Count; i++)
            {
                CsvTableSchema schema = configuredSchema.Tables[i];
                if (schema == null || !schema.Enabled || string.IsNullOrWhiteSpace(schema.RelativePath)) continue;
                string absolutePath;
                try
                {
                    absolutePath = Path.GetFullPath(Path.Combine(Folder,
                        schema.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException)
                {
                    continue;
                }
                if (!IsWithinFolder(absolutePath, Folder) || !paths.Add(absolutePath)) continue;
                string name = string.IsNullOrWhiteSpace(schema.Name)
                    ? Path.ChangeExtension(schema.RelativePath, null).Replace('\\', '/')
                    : schema.Name;
                tables.Add(new CsvTableController(name, absolutePath, schema,
                    configuredSchema.CaseSensitiveNames));
            }
        }

        public CsvTableController Find(string name)
        {
            CsvTableController found = null;
            for (int i = 0; i < tables.Count; i++)
                if (string.Equals(tables[i].Name, name, configuredSchema != null && configuredSchema.CaseSensitiveNames
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                {
                    if (found != null) return null;
                    found = tables[i];
                }
            return found;
        }

        private static string GetRelativePath(string root, string path)
        {
            Uri rootUri = new Uri(AppendDirectorySeparator(root));
            Uri pathUri = new Uri(path);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path : path + Path.DirectorySeparatorChar;
        }

        private static bool IsWithinFolder(string path, string folder)
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
