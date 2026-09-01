using System;
using System.Collections.Generic;
using System.IO;
using CsvTool.Schema;
using NUnit.Framework;
using UnityEngine;

namespace CsvTool.Editor.Configuration.Tests
{
    public sealed class CsvWorkspaceAssetTests
    {
        [Test]
        public void DtoConvertsToSchemaAndBack()
        {
            CsvWorkspaceAsset asset = ScriptableObject.CreateInstance<CsvWorkspaceAsset>();
            try
            {
                asset.name = "Reusable Data";
                asset.rootDirectory = "Assets/Config";
                CsvWorkspaceTableConfig table = new CsvWorkspaceTableConfig
                {
                    name = "units",
                    relativePath = "units.csv",
                    identityColumn = "id",
                    displayColumn = "name",
                    frozenColumnCount = 2
                };
                CsvWorkspaceColumnConfig column = new CsvWorkspaceColumnConfig
                {
                    name = "abilities",
                    displayName = "Abilities",
                    helpText = "Ability identifiers",
                    valueKind = CsvValueKind.Reference,
                    readOnly = true,
                    required = true,
                    hasMinimum = true,
                    minimum = 1,
                    hasMaximum = true,
                    maximum = 10,
                    group = 3,
                    order = 7,
                    tokenSyntax = new CsvWorkspaceTokenSyntaxConfig
                    {
                        mode = CsvTokenExtractionMode.Delimited,
                        separator = ";"
                    }
                };
                column.enumOptions.Add("A");
                column.references.Add(new CsvWorkspaceReferenceConfig
                {
                    targetTable = "abilities",
                    targetKeyColumn = "name",
                    targetDisplayColumn = "label",
                    required = true
                });
                table.columns.Add(column);
                asset.tables.Add(table);

                CsvWorkspaceSchema schema = asset.ToSchema();
                Assert.AreEqual("Reusable Data", schema.Name);
                Assert.AreEqual("Assets/Config", schema.RootDirectory);
                Assert.AreEqual(1, schema.Tables.Count);
                CsvTableSchema convertedTable = schema.Tables[0];
                Assert.AreEqual("units.csv", convertedTable.RelativePath);
                Assert.AreEqual("id", convertedTable.IdentityColumn);
                Assert.AreEqual(1, convertedTable.Columns.Count);
                CsvColumnSchema convertedColumn = convertedTable.Columns[0];
                Assert.AreEqual("Ability identifiers", convertedColumn.Description);
                Assert.AreEqual(1, convertedColumn.References.Count);
                Assert.AreEqual("abilities", convertedColumn.References[0].TargetTable);
                Assert.AreEqual(1d, convertedColumn.Minimum.Value);

                CsvWorkspaceAsset roundTrip = CsvWorkspaceAsset.FromSchema(schema);
                try
                {
                    Assert.AreEqual("Assets/Config", roundTrip.rootDirectory);
                    Assert.AreEqual("units", roundTrip.tables[0].name);
                    Assert.AreEqual("Ability identifiers", roundTrip.tables[0].columns[0].helpText);
                    Assert.AreEqual("abilities", roundTrip.tables[0].columns[0].references[0].targetTable);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(roundTrip);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ProjectRelativeAndAbsoluteRootsResolveSafely()
        {
            string projectRoot = Path.Combine(Path.GetTempPath(), "CsvToolProject");
            string expectedRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets", "Data"));
            string absoluteRoot;
            string error;
            Assert.IsTrue(CsvWorkspacePathResolver.TryResolveRootDirectory("Assets/Data", projectRoot, out absoluteRoot, out error), error);
            Assert.AreEqual(expectedRoot, absoluteRoot);
            Assert.IsFalse(CsvWorkspacePathResolver.TryResolveRootDirectory("../../outside", projectRoot, out absoluteRoot, out error));

            string absolutePath;
            Assert.IsTrue(CsvWorkspacePathResolver.TryResolveTablePath("Assets/Data", "nested/values.csv", projectRoot, out absolutePath, out error), error);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(expectedRoot, "nested", "values.csv")), absolutePath);
            Assert.IsFalse(CsvWorkspacePathResolver.TryResolveTablePath("Assets/Data", "../Secrets.csv", projectRoot, out absolutePath, out error));
            Assert.IsFalse(CsvWorkspacePathResolver.TryResolveTablePath(expectedRoot, Path.Combine(projectRoot, "outside.csv"), projectRoot, out absolutePath, out error));

            string absoluteConfiguredRoot = Path.Combine(Path.GetTempPath(), "CsvToolAbsoluteData");
            Assert.IsTrue(CsvWorkspacePathResolver.TryResolveTablePath(absoluteConfiguredRoot, "values.csv", projectRoot, out absolutePath, out error), error);
            Assert.AreEqual(Path.GetFullPath(Path.Combine(absoluteConfiguredRoot, "values.csv")), absolutePath);
        }

        [Test]
        public void ValidationBridgeReportsSchemaStructureIssues()
        {
            CsvWorkspaceAsset asset = ScriptableObject.CreateInstance<CsvWorkspaceAsset>();
            try
            {
                asset.tables.Add(new CsvWorkspaceTableConfig { name = "table", relativePath = "one.csv" });
                asset.tables.Add(new CsvWorkspaceTableConfig { name = "TABLE", relativePath = "two.csv" });
                IReadOnlyList<CsvSchemaIssue> issues = asset.Validate();
                Assert.IsTrue(ContainsCode(issues, "TABLE_NAME_DUPLICATE"));
                Assert.IsFalse(asset.IsValid());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private static bool ContainsCode(IReadOnlyList<CsvSchemaIssue> issues, string code)
        {
            for (int i = 0; i < issues.Count; i++)
                if (issues[i].Code == code) return true;
            return false;
        }
    }
}
