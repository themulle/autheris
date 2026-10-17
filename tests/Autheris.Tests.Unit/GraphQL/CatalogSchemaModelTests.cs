using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using NSubstitute;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class CatalogSchemaModelTests
{
    [Theory]
    [InlineData("valid_name", "valid_name")]
    [InlineData("123_table", "_123_table")]
    [InlineData("table-name.with!special#chars", "table_name_with_special_chars")]
    [InlineData("", "_")]
    [InlineData("   ", "_")]
    [InlineData("___", "_")]
    public void SanitizeGraphQlName_SanitizesCorrectly(string input, string expected)
    {
        var result = CatalogSchemaModel.SanitizeGraphQlName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task BuildAsync_FiltersInactiveNonSqlAndInvalidDialects()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var activeSql = CreateTable(new TableIdentifier("sales", "dbo", "customers"), isActive: true, dsType: DataSourceType.Sql, dialect: "PostgreSql");
        var inactiveSql = CreateTable(new TableIdentifier("sales", "dbo", "inactive_table"), isActive: false, dsType: DataSourceType.Sql, dialect: "PostgreSql");
        var nonSql = CreateTable(new TableIdentifier("sales", "dbo", "mongo_table"), isActive: true, dsType: DataSourceType.HttpDeclarative, dialect: "PostgreSql");
        var invalidDialect = CreateTable(new TableIdentifier("sales", "dbo", "bad_dialect"), isActive: true, dsType: DataSourceType.Sql, dialect: "UnknownDialect");

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([activeSql, inactiveSql, nonSql, invalidDialect]);

        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        Assert.Single(model.Tables);
        Assert.Equal(activeSql.Identifier, model.Tables[0].Identifier);
    }

    [Fact]
    public async Task BuildAsync_CollidingTableNames_OmitsDuplicateAndPreservesCanonicalTable()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        // Two tables that sanitize to the same base name "sales_dbo_orders_item"
        var table1 = CreateTable(new TableIdentifier("sales", "dbo", "orders_item"), dialect: "SqlServer");
        var table2 = CreateTable(new TableIdentifier("sales_dbo", "orders", "item"), dialect: "SqlServer");

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([table1, table2]);

        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        // G-2: Stable naming - the canonical table is preserved, the colliding duplicate is omitted
        var canonicalTable = Assert.Single(model.Tables);
        Assert.Equal(table1.Identifier, canonicalTable.Identifier);
        Assert.Equal("sales_dbo_orders_item", canonicalTable.TypeName);
    }

    [Fact]
    public async Task BuildAsync_TableCollidingWithFilterNameOfEarlierTable_IsOmittedAndPreservesCanonicalTable()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var table1 = CreateTable(new TableIdentifier("db", "public", "orders"), dialect: "SqlServer");
        var table2 = CreateTable(new TableIdentifier("db", "public", "orders_filter"), dialect: "SqlServer");

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([table1, table2]);

        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        // G-2: orders is preserved with canonical name and not renamed to orders_2; orders_filter is omitted
        var canonicalTable = Assert.Single(model.Tables);
        Assert.Equal(table1.Identifier, canonicalTable.Identifier);
        Assert.Equal("db_public_orders", canonicalTable.TypeName);
        Assert.Equal("db_public_orders_filter", canonicalTable.FilterTypeName);
    }

    [Fact]
    public async Task BuildAsync_TableCollidingWithOrderByNameOfEarlierTable_IsOmittedAndPreservesCanonicalTable()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var table1 = CreateTable(new TableIdentifier("db", "public", "orders"), dialect: "SqlServer");
        var table2 = CreateTable(new TableIdentifier("db", "public", "orders_order_by"), dialect: "SqlServer");

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([table1, table2]);

        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        // G-2: orders is preserved with canonical name and not renamed; orders_order_by is omitted
        var canonicalTable = Assert.Single(model.Tables);
        Assert.Equal(table1.Identifier, canonicalTable.Identifier);
        Assert.Equal("db_public_orders", canonicalTable.TypeName);
        Assert.Equal("db_public_orders_order_by", canonicalTable.OrderByTypeName);
    }

    [Fact]
    public async Task BuildAsync_MapsDataTypesToCatalogFieldTypes()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var table = CreateTable(new TableIdentifier("inventory", "dbo", "items"), dialect: "PostgreSql", columns:
        [
            new TableColumn { ColumnName = "id", DataType = "bigint" },
            new TableColumn { ColumnName = "quantity", DataType = "int" },
            new TableColumn { ColumnName = "price", DataType = "decimal(18,2)" },
            new TableColumn { ColumnName = "weight", DataType = "float" },
            new TableColumn { ColumnName = "is_available", DataType = "boolean" },
            new TableColumn { ColumnName = "created_at", DataType = "timestamptz" },
            new TableColumn { ColumnName = "description", DataType = "text" }
        ]);

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([table]);

        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);
        var tableType = Assert.Single(model.Tables);

        var colMap = tableType.Columns.ToDictionary(c => c.ColumnName, c => c.FieldType);
        Assert.Equal(CatalogFieldType.Long, colMap["id"]);
        Assert.Equal(CatalogFieldType.Int, colMap["quantity"]);
        Assert.Equal(CatalogFieldType.Decimal, colMap["price"]);
        Assert.Equal(CatalogFieldType.Float, colMap["weight"]);
        Assert.Equal(CatalogFieldType.Boolean, colMap["is_available"]);
        Assert.Equal(CatalogFieldType.DateTime, colMap["created_at"]);
        Assert.Equal(CatalogFieldType.String, colMap["description"]);
    }

    [Fact]
    public async Task BuildAsync_ColumnWithHmacMaskingRule_IsTypedAsStringInSchema()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var table = CreateTable(
            new TableIdentifier("billing", "dbo", "payments"),
            dialect: "PostgreSql",
            columns:
            [
                new TableColumn { ColumnName = "id", DataType = "bigint" },
                new TableColumn { ColumnName = "user_id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal(18,2)" },
                new TableColumn { ColumnName = "is_verified", DataType = "boolean" },
                new TableColumn { ColumnName = "created_at", DataType = "timestamptz" }
            ],
            columnMaskingRules: new Dictionary<string, MaskingRule>
            {
                ["user_id"] = new MaskingRule { RuleType = "HMAC" },
                ["amount"] = new MaskingRule { RuleType = "HMAC_SHA256" },
                ["is_verified"] = new MaskingRule { RuleType = "HASH" }
            });

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([table]);

        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);
        var tableType = Assert.Single(model.Tables);

        var colMap = tableType.Columns.ToDictionary(c => c.ColumnName, c => c.FieldType);
        Assert.Equal(CatalogFieldType.Long, colMap["id"]); // unmasked
        Assert.Equal(CatalogFieldType.String, colMap["user_id"]); // HMAC -> String
        Assert.Equal(CatalogFieldType.String, colMap["amount"]); // HMAC_SHA256 -> String
        Assert.Equal(CatalogFieldType.String, colMap["is_verified"]); // HASH -> String
        Assert.Equal(CatalogFieldType.DateTime, colMap["created_at"]); // unmasked
    }

    [Fact]
    public async Task BuildAsync_CreatesBidirectionalRelationsWithCorrectJoinKeysAndListFlags()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var parentId = new TableIdentifier("sales", "dbo", "customers");
        var childId = new TableIdentifier("sales", "dbo", "orders");

        var parentTable = CreateTable(parentId, dialect: "Sqlite", columns: [new TableColumn { ColumnName = "id", DataType = "int" }]);
        var childTable = CreateTable(childId, dialect: "Sqlite", columns:
        [
            new TableColumn { ColumnName = "order_id", DataType = "int" },
            new TableColumn { ColumnName = "customer_id", DataType = "int" }
        ]);

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([parentTable, childTable]);

        var relation = new TableRelation
        {
            RelationName = "customer_orders",
            ParentTableIdentifier = parentId,
            ChildTableIdentifier = childId,
            Cardinality = RelationCardinality.OneToMany,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["customer_id"]
        };

        relRepo.GetRelationsForTableAsync(parentId, Arg.Any<CancellationToken>())
            .Returns([relation]);
        relRepo.GetRelationsForTableAsync(childId, Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        var parentModel = Assert.Single(model.Tables, t => t.Identifier == parentId);
        var childModel = Assert.Single(model.Tables, t => t.Identifier == childId);

        // Parent forward relation (1:N => isList = true)
        var forwardRel = Assert.Single(parentModel.Relations);
        Assert.Equal("customer_orders", forwardRel.RelationName);
        Assert.True(forwardRel.IsList);
        Assert.Equal(childId, forwardRel.TargetTableIdentifier);
        Assert.Equal(["id"], forwardRel.ParentColumns);
        Assert.Equal(["customer_id"], forwardRel.ChildColumns);

        // Child reverse relation (N:1 => isList = false)
        var reverseRel = Assert.Single(childModel.Relations);
        Assert.Equal("customer_orders", reverseRel.RelationName);
        Assert.False(reverseRel.IsList);
        Assert.Equal(parentId, reverseRel.TargetTableIdentifier);
        Assert.Equal(["customer_id"], reverseRel.ParentColumns);
        Assert.Equal(["id"], reverseRel.ChildColumns);
    }

    [Fact]
    public async Task BuildAsync_AppendsRelSuffixOnRelationColumnNameCollision()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var parentId = new TableIdentifier("crm", "dbo", "users");
        var childId = new TableIdentifier("crm", "dbo", "profiles");

        // Parent has a column named "profile" and a relation named "profile"
        var parentTable = CreateTable(parentId, dialect: "SqlServer", columns:
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "profile", DataType = "nvarchar(100)" }
        ]);
        var childTable = CreateTable(childId, dialect: "SqlServer", columns:
        [
            new TableColumn { ColumnName = "user_id", DataType = "int" }
        ]);

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([parentTable, childTable]);

        var relation = new TableRelation
        {
            RelationName = "profile",
            ParentTableIdentifier = parentId,
            ChildTableIdentifier = childId,
            Cardinality = RelationCardinality.ManyToOne,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["user_id"]
        };

        relRepo.GetRelationsForTableAsync(parentId, Arg.Any<CancellationToken>())
            .Returns([relation]);
        relRepo.GetRelationsForTableAsync(childId, Arg.Any<CancellationToken>())
            .Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);
        var parentModel = Assert.Single(model.Tables, t => t.Identifier == parentId);

        var relField = Assert.Single(parentModel.Relations);
        Assert.Equal("profile_rel", relField.FieldName);
    }

    [Fact]
    public async Task BuildAsync_TablesOfDialectsWithoutTreeCompiler_AreOmitted()
    {
        // R-GQL-13: Oracle and Databricks tables were in the schema, but every query failed with NotSupported.
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();
        var pg = CreateTable(new TableIdentifier("sales", "public", "orders"), dialect: "PostgreSql");
        var oracle = CreateTable(new TableIdentifier("erp", "app", "invoices"), dialect: "Oracle");
        var databricks = CreateTable(new TableIdentifier("lake", "gold", "facts"), dialect: "Databricks");
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([pg, oracle, databricks]);
        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        Assert.Equal(pg.Identifier, Assert.Single(model.Tables).Identifier);
    }

    [Fact]
    public async Task BuildAsync_ColumnsWithoutPlainNames_AreOmitted()
    {
        // R-GQL-13: "a-b" was offered as a_b but the tree compiler rejects such column names.
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();
        var table = CreateTable(new TableIdentifier("sales", "public", "orders"), columns:
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "unit-price", DataType = "decimal" }
        ]);
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([table]);
        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo);

        Assert.Equal(["id"], Assert.Single(model.Tables).Columns.Select(c => c.ColumnName));
    }

    // BUG #75: relations between tables the tree service cannot run as one statement are not exposed.

    private static readonly TableIdentifier PartnerId = new("crm", "dbo", "partners");
    private static readonly TableIdentifier ContactId = new("sales", "dbo", "contacts");

    private static TableRelation Relation(string name, TableIdentifier parent, TableIdentifier child, bool allowCrossDataSource = false) => new()
    {
        RelationName = name,
        ParentTableIdentifier = parent,
        ChildTableIdentifier = child,
        Cardinality = RelationCardinality.OneToMany,
        JoinKeysParent = ["id"],
        JoinKeysChild = ["partner_id"],
        AllowCrossDataSource = allowCrossDataSource
    };

    private static TableMetadata RelTable(TableIdentifier id, string sourceName, string dialect = "PostgreSql") =>
        CreateTable(id, dialect: dialect, sourceName: sourceName, columns:
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "partner_id", DataType = "int" }
        ]);

    private static async Task<CatalogSchemaModel> BuildWithRelationsAsync(
        IReadOnlyList<TableMetadata> tables,
        IReadOnlyList<TableRelation> relations,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(tables);
        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => relations.Where(r => r.ParentTableIdentifier == ci.ArgAt<TableIdentifier>(0)).ToList());
        return await CatalogSchemaModel.BuildAsync(metaRepo, relRepo, logger);
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static TableMetadata CompositeTable(TableIdentifier id, params string[] cols) =>
        CreateTable(id, dialect: "PostgreSql", sourceName: "pg1", columns: cols.Select(c => new TableColumn { ColumnName = c, DataType = "int" }).ToArray());

    private static TableRelation CompositeRelation(string name, IReadOnlyList<string> parentKeys, IReadOnlyList<string> childKeys) => new()
    {
        RelationName = name,
        ParentTableIdentifier = PartnerId,
        ChildTableIdentifier = ContactId,
        Cardinality = RelationCardinality.OneToMany,
        JoinKeysParent = parentKeys,
        JoinKeysChild = childKeys
    };

    [Fact]
    public async Task CatalogSchema_KeyCountMismatch_OmitsRelationField()
    {
        var model = await BuildWithRelationsAsync(
            [CompositeTable(PartnerId, "id", "region"), CompositeTable(ContactId, "pid", "region")],
            [CompositeRelation("contacts", ["id", "region"], ["pid"])]);

        Assert.All(model.Tables, t => Assert.Empty(t.Relations));
    }

    [Fact]
    public async Task CatalogSchema_CompositeRelation_KeepsAllPairsInBothDirections()
    {
        var model = await BuildWithRelationsAsync(
            [CompositeTable(PartnerId, "id", "region"), CompositeTable(ContactId, "pid", "reg")],
            [CompositeRelation("contacts", ["id", "region"], ["pid", "reg"])]);

        var fwd = Assert.Single(Assert.Single(model.Tables, t => t.Identifier == PartnerId).Relations);
        Assert.Equal(["id", "region"], fwd.ParentColumns);
        Assert.Equal(["pid", "reg"], fwd.ChildColumns);
        var rev = Assert.Single(Assert.Single(model.Tables, t => t.Identifier == ContactId).Relations);
        Assert.Equal(["pid", "reg"], rev.ParentColumns);
        Assert.Equal(["id", "region"], rev.ChildColumns);
    }

    [Fact]
    public async Task CatalogSchema_Fingerprint_ChangesWithSecondKeyPair()
    {
        TableMetadata[] tables = [CompositeTable(PartnerId, "id", "region", "zone"), CompositeTable(ContactId, "pid", "reg", "zn")];
        var one = await BuildWithRelationsAsync(tables, [CompositeRelation("contacts", ["id"], ["pid"])]);
        var two = await BuildWithRelationsAsync(tables, [CompositeRelation("contacts", ["id", "region"], ["pid", "reg"])]);
        var twoOther = await BuildWithRelationsAsync(tables, [CompositeRelation("contacts", ["id", "zone"], ["pid", "zn"])]);

        Assert.NotEqual(one.Fingerprint(), two.Fingerprint());
        Assert.NotEqual(two.Fingerprint(), twoOther.Fingerprint());
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogSchema_DroppedKeyColumn_OmitsRelation(bool dropOnParent)
    {
        // SEC-REL151-08 / T-REL151-12: the second key column no longer exists in the table metadata.
        var parent = dropOnParent ? CompositeTable(PartnerId, "id") : CompositeTable(PartnerId, "id", "region");
        var child = dropOnParent ? CompositeTable(ContactId, "pid", "reg") : CompositeTable(ContactId, "pid");

        var model = await BuildWithRelationsAsync(
            [parent, child],
            [CompositeRelation("contacts", ["id", "region"], ["pid", "reg"])]);

        Assert.All(model.Tables, t => Assert.Empty(t.Relations));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CatalogSchema_ManyInvalidRelations_LogsBoundedLinesWithIdsOnly()
    {
        // SEC-REL151-09: one summary plus at most 10 detail lines, no catalog names.
        var logger = new CapturingLogger();
        var relations = Enumerable.Range(0, 50)
            .Select(i => CompositeRelation($"secret_rel_{i}", ["id", "region"], ["pid"]))
            .ToList();

        await BuildWithRelationsAsync(
            [CompositeTable(PartnerId, "id", "region"), CompositeTable(ContactId, "pid", "reg")],
            relations, logger);

        Assert.True(logger.Entries.Count <= 11, $"Expected at most 11 log lines but got {logger.Entries.Count}.");
        Assert.All(logger.Entries, e => Assert.DoesNotContain("secret_rel_", e.Message));
        Assert.All(logger.Entries, e => Assert.DoesNotContain("region", e.Message));
    }

    [Fact]
    public async Task BuildAsync_CrossSourceForwardRelation_IsOmitted()
    {
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg2")],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.Empty(Assert.Single(model.Tables, t => t.Identifier == PartnerId).Relations);
    }

    [Fact]
    public async Task BuildAsync_CrossSourceReverseRelation_IsOmitted()
    {
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg2")],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.Empty(Assert.Single(model.Tables, t => t.Identifier == ContactId).Relations);
    }

    [Fact]
    public async Task BuildAsync_CrossSourceRelationWithAllowFlag_IsStillOmitted()
    {
        // Interim rule: the flag has no effect on the schema until the federated path exists.
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg2")],
            [Relation("contacts", PartnerId, ContactId, allowCrossDataSource: true)]);

        Assert.All(model.Tables, t => Assert.Empty(t.Relations));
    }

    [Fact]
    public async Task BuildAsync_SameSourceDifferentDomain_RelationIsKept()
    {
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg1")],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.Single(Assert.Single(model.Tables, t => t.Identifier == PartnerId).Relations);
        Assert.Single(Assert.Single(model.Tables, t => t.Identifier == ContactId).Relations);
    }

    [Fact]
    public async Task BuildAsync_SourceNameComparison_IsCaseInsensitive()
    {
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "PG1"), RelTable(ContactId, "pg1")],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.Single(Assert.Single(model.Tables, t => t.Identifier == PartnerId).Relations);
    }

    [Fact]
    public async Task BuildAsync_EmptySourceNameOnBothSides_IsSameSource()
    {
        // Documents the decision: empty equals empty (the runtime then fails closed with "has no database connection").
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, ""), RelTable(ContactId, "")],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.Single(Assert.Single(model.Tables, t => t.Identifier == PartnerId).Relations);
    }

    [Fact]
    public async Task BuildAsync_EmptyVersusNonEmptySourceName_IsOmitted()
    {
        // An empty name is never a wildcard for another source.
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, ""), RelTable(ContactId, "pg1")],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.All(model.Tables, t => Assert.Empty(t.Relations));
    }

    [Fact]
    public async Task BuildAsync_SameSourceNameDifferentDialect_IsOmitted()
    {
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1", dialect: "PostgreSql"), RelTable(ContactId, "pg1", dialect: "SqlServer")],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.All(model.Tables, t => Assert.Empty(t.Relations));
    }

    [Fact]
    public async Task BuildAsync_OmittedCrossSourceRelation_DoesNotConsumeFieldNames()
    {
        var otherId = new TableIdentifier("crm", "dbo", "others");
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg2"), RelTable(otherId, "pg1")],
            [Relation("profile", PartnerId, ContactId), Relation("profile", PartnerId, otherId)]);

        var field = Assert.Single(Assert.Single(model.Tables, t => t.Identifier == PartnerId).Relations);
        Assert.Equal("profile", field.FieldName);
        Assert.Equal(otherId, field.TargetTableIdentifier);
    }

    [Fact]
    public async Task BuildAsync_MixedRelations_OnlyCrossSourceOmitted()
    {
        var sameId = new TableIdentifier("crm", "dbo", "others");
        var model = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg2"), RelTable(sameId, "pg1")],
            [Relation("contacts", PartnerId, ContactId), Relation("others", PartnerId, sameId)]);

        var forward = Assert.Single(Assert.Single(model.Tables, t => t.Identifier == PartnerId).Relations);
        Assert.Equal(sameId, forward.TargetTableIdentifier);
        Assert.Empty(Assert.Single(model.Tables, t => t.Identifier == ContactId).Relations);
        Assert.Single(Assert.Single(model.Tables, t => t.Identifier == sameId).Relations);
    }

    [Fact]
    public async Task Fingerprint_AddingOmittedCrossSourceRelation_DoesNotChangeIt_ButSameSourceRelationDoes()
    {
        var tables = new[] { RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg2") };
        var none = await BuildWithRelationsAsync(tables, []);
        var crossSource = await BuildWithRelationsAsync(tables, [Relation("contacts", PartnerId, ContactId)]);
        var sameSource = await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg1")],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.Equal(none.Fingerprint(), crossSource.Fingerprint());
        Assert.NotEqual(none.Fingerprint(), sameSource.Fingerprint());
    }

    [Fact]
    public async Task Fingerprint_ChangesWhenTableMovesToOtherSource()
    {
        var relations = new[] { Relation("contacts", PartnerId, ContactId) };
        var before = await BuildWithRelationsAsync([RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg1")], relations);
        var after = await BuildWithRelationsAsync([RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg2")], relations);

        Assert.NotEqual(before.Fingerprint(), after.Fingerprint());
    }

    [Fact]
    public async Task BuildAsync_OmittedRelations_LogsCountOnceWithoutNames()
    {
        var logger = new CapturingLogger();

        await BuildWithRelationsAsync(
            [RelTable(PartnerId, "secret_source_a"), RelTable(ContactId, "secret_source_b")],
            [Relation("secret_relation", PartnerId, ContactId)],
            logger);

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("relation(s)", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.Contains("1 relation(s)", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_relation", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_source", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("partners", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("contacts", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_NoOmittedRelations_LogsNothingAboutRelations()
    {
        var logger = new CapturingLogger();

        await BuildWithRelationsAsync(
            [RelTable(PartnerId, "pg1"), RelTable(ContactId, "pg1")],
            [Relation("contacts", PartnerId, ContactId)],
            logger);

        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("relation(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void GraphQlAssembly_DoesNotReadAllowCrossDataSource()
    {
        // C-3: the flag has no effect on the schema until the federation gate exists.
        var root = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(root);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Autheris.GraphQL")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var offenders = Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Autheris.GraphQL"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("AllowCrossDataSource", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    private static TableMetadata CreateTable(
        TableIdentifier id,
        bool isActive = true,
        DataSourceType dsType = DataSourceType.Sql,
        string dialect = "PostgreSql",
        IReadOnlyList<TableColumn>? columns = null,
        IReadOnlyDictionary<string, MaskingRule>? columnMaskingRules = null,
        string sourceName = "")
    {
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                IsActive = isActive,
                SourceName = sourceName,
                SourceType = dialect,
                DataSourceType = dsType
            },
            Columns = columns ?? [new TableColumn { ColumnName = "id", DataType = "int" }],
            ColumnMaskingRules = columnMaskingRules ?? new Dictionary<string, MaskingRule>()
        };
    }
}
