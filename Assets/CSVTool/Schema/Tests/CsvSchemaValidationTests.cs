using System.Collections.Generic;
using NUnit.Framework;

namespace CsvTool.Schema.Tests
{
    public sealed class CsvSchemaValidationTests
    {
        [Test]
        public void IdentityAndDisplayHeadersDoNotRequireColumnOverrides()
        {
            CsvWorkspaceSchema workspace = Workspace();
            workspace.Tables.Add(new CsvTableSchema("units", "units.csv")
            {
                IdentityColumn = "name",
                DisplayColumn = "label"
            });

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertNoErrors(issues);
        }

        [Test]
        public void DuplicateTableNamesAndPathsAreRejected()
        {
            CsvWorkspaceSchema workspace = Workspace();
            workspace.Tables.Add(new CsvTableSchema("Units", "units.csv"));
            workspace.Tables.Add(new CsvTableSchema("units", "other.csv"));
            workspace.Tables.Add(new CsvTableSchema("Weapons", "UNITS.CSV"));

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertHasCode(issues, "TABLE_NAME_DUPLICATE");
            AssertHasCode(issues, "TABLE_PATH_DUPLICATE");
        }

        [Test]
        public void DuplicateColumnNamesAndIndicesAreRejected()
        {
            CsvWorkspaceSchema workspace = Workspace();
            CsvTableSchema table = new CsvTableSchema("units", "units.csv");
            table.Columns.Add(new CsvColumnSchema("name") { Index = 0 });
            table.Columns.Add(new CsvColumnSchema("NAME") { Index = 1 });
            table.Columns.Add(new CsvColumnSchema(4));
            table.Columns.Add(new CsvColumnSchema("other") { Index = 4 });
            workspace.Tables.Add(table);

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertHasCode(issues, "COLUMN_NAME_DUPLICATE");
            AssertHasCode(issues, "COLUMN_INDEX_DUPLICATE");
        }

        [Test]
        public void UnnamedColumnCanBeSelectedByIndex()
        {
            CsvWorkspaceSchema workspace = Workspace();
            CsvTableSchema table = new CsvTableSchema("technologies", "technologies.csv")
            {
                IdentityColumnIndex = 14,
                DisplayColumnIndex = 14
            };
            table.Columns.Add(new CsvColumnSchema(14));
            workspace.Tables.Add(table);

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertNoErrors(issues);
        }

        [Test]
        public void InvalidTableSelectorIndicesAreRejected()
        {
            CsvWorkspaceSchema workspace = Workspace();
            workspace.Tables.Add(new CsvTableSchema("broken", "broken.csv")
            {
                IdentityColumnIndex = -2,
                DisplayColumnIndex = -3
            });

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertHasCode(issues, "IDENTITY_COLUMN_INDEX_INVALID");
            AssertHasCode(issues, "DISPLAY_COLUMN_INDEX_INVALID");
        }

        [Test]
        public void ExplicitReferenceSupportsAllTokenExtractionModes()
        {
            CsvWorkspaceSchema workspace = Workspace();
            CsvTableSchema abilities = new CsvTableSchema("abilities", "abilities.csv")
            {
                IdentityColumn = "name",
                DisplayColumn = "name"
            };
            workspace.Tables.Add(abilities);

            CsvTableSchema units = new CsvTableSchema("units", "units.csv");
            units.Columns.Add(ReferenceColumn("whole", new CsvTokenSyntax(CsvTokenExtractionMode.WholeCell)));
            units.Columns.Add(ReferenceColumn("list", new CsvTokenSyntax(CsvTokenExtractionMode.Delimited, ";", true, true)));
            units.Columns.Add(ReferenceColumn("first", new CsvTokenSyntax(CsvTokenExtractionMode.FirstWhitespaceToken)));
            units.Columns.Add(ReferenceColumn("regex", new CsvTokenSyntax(CsvTokenExtractionMode.RegexCapture, string.Empty, true, true)
            {
                RegexPattern = "^([A-Za-z_]+)\\s+\\d+$",
                RegexCaptureGroup = 1
            }));
            workspace.Tables.Add(units);

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertNoErrors(issues);
        }

        [Test]
        public void MissingReferenceTargetIsRejected()
        {
            CsvWorkspaceSchema workspace = Workspace();
            CsvTableSchema units = new CsvTableSchema("units", "units.csv");
            units.Columns.Add(ReferenceColumn("abilities", new CsvTokenSyntax(CsvTokenExtractionMode.WholeCell), "missing", "name"));
            workspace.Tables.Add(units);

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertHasCode(issues, "REFERENCE_TABLE_MISSING");
        }

        [Test]
        public void ListReferenceRequiresDelimitedExtraction()
        {
            CsvWorkspaceSchema workspace = Workspace();
            workspace.Tables.Add(new CsvTableSchema("abilities", "abilities.csv") { IdentityColumn = "name" });
            CsvTableSchema units = new CsvTableSchema("units", "units.csv");
            CsvColumnSchema column = new CsvColumnSchema("abilities")
            {
                ValueKind = CsvValueKind.List,
                TokenSyntax = new CsvTokenSyntax(CsvTokenExtractionMode.WholeCell)
            };
            column.References.Add(new CsvReferenceSpec("abilities", "name"));
            units.Columns.Add(column);
            workspace.Tables.Add(units);

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertHasCode(issues, "LIST_EXTRACTION_MODE_INVALID");
        }

        [Test]
        public void EmptyDelimitedAndRegexSyntaxAreRejected()
        {
            CsvWorkspaceSchema workspace = Workspace();
            workspace.Tables.Add(new CsvTableSchema("abilities", "abilities.csv"));
            CsvTableSchema units = new CsvTableSchema("units", "units.csv");
            units.Columns.Add(ReferenceColumn("emptyDelimiter", new CsvTokenSyntax(CsvTokenExtractionMode.Delimited, string.Empty, true, true), "abilities", "name"));
            units.Columns.Add(ReferenceColumn("emptyRegex", new CsvTokenSyntax(CsvTokenExtractionMode.RegexCapture), "abilities", "name"));
            workspace.Tables.Add(units);

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertHasCode(issues, "TOKEN_SYNTAX_MISSING");
        }

        [Test]
        public void MalformedRegexAndUnavailableCaptureGroupAreRejectedBeforeNavigation()
        {
            CsvWorkspaceSchema workspace = Workspace();
            workspace.Tables.Add(new CsvTableSchema("abilities", "abilities.csv"));
            CsvTableSchema units = new CsvTableSchema("units", "units.csv");
            units.Columns.Add(ReferenceColumn("broken", new CsvTokenSyntax(CsvTokenExtractionMode.RegexCapture)
            {
                RegexPattern = "(",
                RegexCaptureGroup = 1
            }, "abilities", "id"));
            units.Columns.Add(ReferenceColumn("missingGroup", new CsvTokenSyntax(CsvTokenExtractionMode.RegexCapture)
            {
                RegexPattern = "(id)",
                RegexCaptureGroup = 2
            }, "abilities", "id"));
            workspace.Tables.Add(units);

            IReadOnlyList<CsvSchemaIssue> issues = CsvSchemaValidator.Validate(workspace);

            AssertHasCode(issues, "REGEX_PATTERN_INVALID");
            AssertHasCode(issues, "REGEX_CAPTURE_GROUP_MISSING");
        }

        [Test]
        public void UnitsAbilitiesStyleConfigurationIsValid()
        {
            CsvWorkspaceSchema workspace = Workspace();
            CsvTableSchema abilities = new CsvTableSchema("abilities", "abilities.csv")
            {
                IdentityColumn = "name",
                DisplayColumn = "name"
            };
            workspace.Tables.Add(abilities);

            CsvTableSchema units = new CsvTableSchema("units", "units.csv")
            {
                IdentityColumn = "name",
                DisplayColumn = "name"
            };
            CsvColumnSchema references = new CsvColumnSchema("abilities")
            {
                ValueKind = CsvValueKind.List,
                TokenSyntax = new CsvTokenSyntax(";", true, true)
            };
            references.References.Add(new CsvReferenceSpec("abilities", "name") { Required = false });
            units.Columns.Add(references);
            workspace.Tables.Add(units);

            Assert.IsTrue(CsvSchemaValidator.IsValid(workspace));
        }

        private static CsvWorkspaceSchema Workspace()
        {
            return new CsvWorkspaceSchema
            {
                Name = "Test workspace",
                RootDirectory = "Assets/Data"
            };
        }

        private static CsvColumnSchema ReferenceColumn(string name, CsvTokenSyntax syntax)
        {
            return ReferenceColumn(name, syntax, "abilities", "name");
        }

        private static CsvColumnSchema ReferenceColumn(string name, CsvTokenSyntax syntax, string table, string key)
        {
            CsvColumnSchema column = new CsvColumnSchema(name)
            {
                ValueKind = CsvValueKind.Reference,
                TokenSyntax = syntax
            };
            column.References.Add(new CsvReferenceSpec(table, key));
            return column;
        }

        private static void AssertNoErrors(IReadOnlyList<CsvSchemaIssue> issues)
        {
            for (int i = 0; i < issues.Count; i++)
            {
                Assert.AreNotEqual(CsvSchemaIssueSeverity.Error, issues[i].Severity, issues[i].ToString());
            }
        }

        private static void AssertHasCode(IReadOnlyList<CsvSchemaIssue> issues, string code)
        {
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i].Code == code)
                {
                    return;
                }
            }

            Assert.Fail("Expected schema issue code: " + code);
        }
    }
}
