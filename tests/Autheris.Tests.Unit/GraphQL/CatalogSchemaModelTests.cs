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

    // BUG #157: tables without queryable columns are omitted instead of producing field-less GraphQL types.

    private static readonly TableColumn[] NoColumns = [];
    private static readonly TableColumn[] OnlyDashedColumns =
        [new TableColumn { ColumnName = "secret-col", DataType = "int" }, new TableColumn { ColumnName = "other-col", DataType = "int" }];

    private static async Task<CatalogSchemaModel> BuildTablesAsync(
        IReadOnlyList<TableMetadata> tables,
        IReadOnlyList<TableRelation>? relations = null,
        Microsoft.Extensions.Logging.ILogger? logger = null)
        => await BuildWithRelationsAsync(tables, relations ?? [], logger);

    [Fact]
    public async Task BuildAsync_TableWithoutColumns_IsOmittedWithNoQueryableColumns()
    {
        var empty = CreateTable(new TableIdentifier("sales", "dbo", "empty"), columns: NoColumns);
        var valid = CreateTable(new TableIdentifier("sales", "dbo", "valid"));

        var model = await BuildTablesAsync([empty, valid]);

        Assert.Equal(valid.Identifier, Assert.Single(model.Tables).Identifier);
        var omitted = Assert.Single(model.OmittedTables);
        Assert.Equal(empty.Identifier, omitted.Table);
        Assert.Equal(CatalogTableOmissionReason.NoQueryableColumns, omitted.Reason);
    }

    [Fact]
    public async Task BuildAsync_TableWhoseColumnsAllFailPlainIdentifierRule_IsOmittedWithNoQueryableColumns()
    {
        var dashed = CreateTable(new TableIdentifier("sales", "dbo", "dashed"), columns: OnlyDashedColumns);

        var model = await BuildTablesAsync([dashed]);

        Assert.Empty(model.Tables);
        Assert.Equal(CatalogTableOmissionReason.NoQueryableColumns, Assert.Single(model.OmittedTables).Reason);
    }

    [Fact]
    public async Task BuildAsync_OmittedColumnlessTable_DoesNotReserveTypeNames()
    {
        // "sales.dbo.orders" sorts before "sales_dbo.orders.x"? Both sanitise to sales_dbo_orders*; use identical base names.
        var emptyFirst = CreateTable(new TableIdentifier("sales", "dbo", "orders_item"), columns: NoColumns);
        var validLater = CreateTable(new TableIdentifier("sales_dbo", "orders", "item"));

        var model = await BuildTablesAsync([emptyFirst, validLater]);

        var kept = Assert.Single(model.Tables);
        Assert.Equal(validLater.Identifier, kept.Identifier);
        Assert.Equal("sales_dbo_orders_item", kept.TypeName);
        Assert.Equal(CatalogTableOmissionReason.NoQueryableColumns, Assert.Single(model.OmittedTables).Reason);
    }

    [Fact]
    public async Task BuildAsync_RelationToOrFromOmittedTable_IsNotExposed()
    {
        var parent = RelTable(PartnerId, "pg1");
        var emptyChild = CreateTable(ContactId, sourceName: "pg1", columns: NoColumns);

        var model = await BuildTablesAsync([parent, emptyChild], [Relation("contacts", PartnerId, ContactId)]);

        Assert.Empty(Assert.Single(model.Tables).Relations);

        var emptyParent = CreateTable(PartnerId, sourceName: "pg1", columns: NoColumns);
        var child = RelTable(ContactId, "pg1");
        var model2 = await BuildTablesAsync([emptyParent, child], [Relation("contacts", PartnerId, ContactId)]);

        Assert.Empty(Assert.Single(model2.Tables).Relations);
    }

    [Fact]
    public async Task BuildAsync_CollidingTable_IsRecordedAsNameCollision()
    {
        var first = CreateTable(new TableIdentifier("sales", "dbo", "orders_item"));
        var second = CreateTable(new TableIdentifier("sales_dbo", "orders", "item"));

        var model = await BuildTablesAsync([first, second]);

        var omitted = Assert.Single(model.OmittedTables);
        Assert.Equal(second.Identifier, omitted.Table);
        Assert.Equal(CatalogTableOmissionReason.NameCollision, omitted.Reason);
    }

    [Fact]
    public async Task BuildAsync_TreeUnsupportedDialectTable_IsRecordedAsUnsupportedDialect()
    {
        var pg = CreateTable(new TableIdentifier("sales", "public", "orders"));
        var oracle = CreateTable(new TableIdentifier("erp", "app", "invoices"), dialect: "Oracle");
        var unknown = CreateTable(new TableIdentifier("erp", "app", "mystery"), dialect: "NoSuchDialect");

        var model = await BuildTablesAsync([pg, oracle, unknown]);

        Assert.Equal(pg.Identifier, Assert.Single(model.Tables).Identifier);
        Assert.Equal(2, model.OmittedTables.Count);
        Assert.All(model.OmittedTables, o => Assert.Equal(CatalogTableOmissionReason.UnsupportedDialect, o.Reason));
    }

    [Fact]
    public async Task BuildAsync_ColumnlessTable_LogsOneWarningWithQualifiedNameOnly()
    {
        var logger = new CapturingLogger();
        var empty = CreateTable(new TableIdentifier("sales", "dbo", "empty"), columns: NoColumns);

        await BuildTablesAsync([empty], logger: logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains(empty.Identifier.ToQualifiedName(), warning.Message);
    }

    [Fact]
    public async Task BuildAsync_NullLogger_ColumnlessTable_DoesNotThrow()
    {
        var empty = CreateTable(new TableIdentifier("sales", "dbo", "empty"), columns: NoColumns);

        var model = await BuildTablesAsync([empty], logger: null);

        Assert.Empty(model.Tables);
    }

    [Fact]
    public async Task Model_NeverContainsTableWithZeroColumns()
    {
        var tables = new[]
        {
            CreateTable(new TableIdentifier("a", "dbo", "valid")),
            CreateTable(new TableIdentifier("a", "dbo", "empty"), columns: NoColumns),
            CreateTable(new TableIdentifier("a", "dbo", "filtered"), columns: OnlyDashedColumns),
            CreateTable(new TableIdentifier("a", "dbo", "valid_filter")),
            CreateTable(new TableIdentifier("a_dbo", "valid", "filter")),
        };

        var model = await BuildTablesAsync(tables);

        Assert.NotEmpty(model.Tables);
        Assert.All(model.Tables, t => Assert.NotEmpty(t.Columns));
        Assert.Equal(tables.Length, model.Tables.Count + model.OmittedTables.Count);
    }

    [Fact]
    public async Task Fingerprint_ChangesWhenColumnlessTableAppears()
    {
        var valid = CreateTable(new TableIdentifier("sales", "dbo", "valid"));
        var before = await BuildTablesAsync([valid]);
        var after = await BuildTablesAsync([valid, CreateTable(new TableIdentifier("sales", "dbo", "empty"), columns: NoColumns)]);

        Assert.NotEqual(before.Fingerprint(), after.Fingerprint());
    }

    [Fact]
    public async Task Fingerprint_ChangesWhenOmittedTableGainsColumns()
    {
        var emptyId = new TableIdentifier("sales", "dbo", "later");
        var before = await BuildTablesAsync([CreateTable(emptyId, columns: NoColumns)]);
        var after = await BuildTablesAsync([CreateTable(emptyId)]);

        Assert.NotEqual(before.Fingerprint(), after.Fingerprint());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Model_OmittedTable_HasNoTypesFieldsRelationsOrReservedNames()
    {
        // SEC-BUG157-01 / T-BUG157-S01
        var empty = CreateTable(PartnerId, sourceName: "pg1", columns: NoColumns);
        var child = RelTable(ContactId, "pg1");
        var reuser = CreateTable(new TableIdentifier("crm_dbo", "partners", "x"), sourceName: "pg1");
        var reuserSameName = CreateTable(new TableIdentifier("crm", "dbo", "partners_x"), sourceName: "pg1", columns: NoColumns);

        var model = await BuildTablesAsync(
            [empty, child, reuser, reuserSameName],
            [Relation("contacts", PartnerId, ContactId)]);

        Assert.DoesNotContain(model.Tables, t => t.Identifier == empty.Identifier);
        Assert.DoesNotContain(model.Tables, t => t.Identifier == reuserSameName.Identifier);
        Assert.False(model.TablesByIdentifier.ContainsKey(empty.Identifier));
        Assert.DoesNotContain("crm_dbo_partners", model.TablesByTypeName.Keys);
        Assert.DoesNotContain("crm_dbo_partners", model.TablesByQueryFieldName.Keys);
        Assert.All(model.Tables, t => Assert.DoesNotContain(t.Relations, r => r.TargetTableIdentifier == empty.Identifier));
        // The later table keeps the name that the omitted table would otherwise have reserved
        Assert.Equal("crm_dbo_partners_x", model.TablesByIdentifier[reuser.Identifier].TypeName);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Model_AllColumnsFilteredByRGql13_ReasonIsNoQueryableColumnsAndLeaksNoColumnName()
    {
        // SEC-BUG157-05 / T-BUG157-S02: the pre-existing R-GQL-13 information line is out of scope; the new records are checked.
        var logger = new CapturingLogger();
        var dashed = CreateTable(new TableIdentifier("sales", "dbo", "dashed"), columns: OnlyDashedColumns);

        var model = await BuildTablesAsync([dashed], logger: logger);

        var omitted = Assert.Single(model.OmittedTables);
        Assert.Equal(CatalogTableOmissionReason.NoQueryableColumns, omitted.Reason);
        var warning = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.DoesNotContain("secret-col", warning.Message);
        Assert.DoesNotContain("other-col", warning.Message);
        Assert.DoesNotContain("secret", omitted.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Model_InactiveAndNonSqlTables_NeverAppearInOmittedTables()
    {
        // SEC-BUG157-06 / T-BUG157-S08
        var inactive = CreateTable(new TableIdentifier("a", "dbo", "inactive"), isActive: false, columns: NoColumns);
        var inactiveOracle = CreateTable(new TableIdentifier("a", "dbo", "inactive_oracle"), isActive: false, dialect: "Oracle");
        var nonSql = CreateTable(new TableIdentifier("a", "dbo", "http"), dsType: DataSourceType.HttpDeclarative, columns: NoColumns);
        var nonSqlUnknown = CreateTable(new TableIdentifier("a", "dbo", "http2"), dsType: DataSourceType.HttpDeclarative, dialect: "Nope");
        var valid = CreateTable(new TableIdentifier("a", "dbo", "valid"));

        var model = await BuildTablesAsync([inactive, inactiveOracle, nonSql, nonSqlUnknown, valid]);

        Assert.Empty(model.OmittedTables);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Warning_ColumnlessTable_ContainsOnlyQualifiedName()
    {
        // SEC-BUG157-05 / T-BUG157-S09: asserts on the new warning record only.
        var logger = new CapturingLogger();
        var empty = CreateTable(new TableIdentifier("sales", "dbo", "empty"), columns: OnlyDashedColumns);

        await BuildTablesAsync([empty], logger: logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Equal(
            $"GraphQL catalog schema: table '{empty.Identifier.ToQualifiedName()}' has no queryable columns and is omitted from the GraphQL schema.",
            warning.Message);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Fingerprint_StableAcrossRepeatedBuilds_NoRebuildLoop()
    {
        // SEC-BUG157-09 / T-BUG157-S10: check build (logger null) and real build agree with omissions present.
        var tables = new[]
        {
            CreateTable(new TableIdentifier("a", "dbo", "valid")),
            CreateTable(new TableIdentifier("a", "dbo", "empty"), columns: NoColumns),
            CreateTable(new TableIdentifier("a", "dbo", "oracle"), dialect: "Oracle"),
        };

        var check = await BuildTablesAsync(tables, logger: null);
        var real = await BuildTablesAsync(tables, logger: new CapturingLogger());

        Assert.Equal(check.Fingerprint(), real.Fingerprint());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Fingerprint_OmissionEntries_AreUnambiguouslyEncoded()
    {
        // SEC-BUG157-09: two different omission sets never produce the same fingerprint.
        var a = await BuildTablesAsync([CreateTable(new TableIdentifier("a", "b", "c"), columns: NoColumns)]);
        var b = await BuildTablesAsync([CreateTable(new TableIdentifier("a", "b", "c"), dialect: "Oracle")]);

        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
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
