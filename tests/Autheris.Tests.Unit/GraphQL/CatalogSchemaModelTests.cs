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

    private static TableMetadata CreateTable(
        TableIdentifier id,
        bool isActive = true,
        DataSourceType dsType = DataSourceType.Sql,
        string dialect = "PostgreSql",
        IReadOnlyList<TableColumn>? columns = null,
        IReadOnlyDictionary<string, MaskingRule>? columnMaskingRules = null)
    {
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                IsActive = isActive,
                SourceType = dialect,
                DataSourceType = dsType
            },
            Columns = columns ?? [new TableColumn { ColumnName = "id", DataType = "int" }],
            ColumnMaskingRules = columnMaskingRules ?? new Dictionary<string, MaskingRule>()
        };
    }
}
