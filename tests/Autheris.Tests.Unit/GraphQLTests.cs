using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Filtering;
using Autheris.GraphQL.Types;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class GraphQLTests
{
    private readonly SqlFilterProvider _filterProvider = new();

    private static TableMetadata CreateSampleMetadata()
    {
        var table = new Table
        {
            SchemaName = "dbo",
            TableName = "invoices",
            DisplayName = "Customer Invoices"
        };

        var columns = new[]
        {
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" },
            new TableColumn { ColumnName = "customer", DataType = "varchar" },
            new TableColumn { ColumnName = "status", DataType = "varchar" }
        };

        return new TableMetadata
        {
            Table = table,
            Identifier = new TableIdentifier("finance", "dbo", "invoices"),
            Columns = columns
        };
    }

    [Fact]
    public void SqlFilterProvider_TranslatesSimpleEquality_SqlServerDialect()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("amount", new ObjectValueNode(
                new ObjectFieldNode("gte", new FloatValueNode(1000.50))
            ))
        );

        var (sql, parameters) = _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, SqlFilterProvider.UnrestrictedAccess(meta));

        sql.ShouldBe("[amount] >= @p1");
        parameters.Count.ShouldBe(1);
        parameters["p1"].ShouldBe(1000.50m);
    }

    [Fact]
    public void SqlFilterProvider_TranslatesSimpleEquality_PostgreSqlDialect()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("status", new ObjectValueNode(
                new ObjectFieldNode("eq", new StringValueNode("PAID"))
            ))
        );

        var (sql, parameters) = _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.PostgreSql, SqlFilterProvider.UnrestrictedAccess(meta));

        sql.ShouldBe("\"status\" = $1");
        parameters.Count.ShouldBe(1);
        parameters["1"].ShouldBe("PAID");
    }

    [Fact]
    public void SqlFilterProvider_TranslatesSimpleEquality_OracleDialect()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("status", new ObjectValueNode(
                new ObjectFieldNode("eq", new StringValueNode("PAID"))
            ))
        );

        var (sql, parameters) = _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.Oracle, SqlFilterProvider.UnrestrictedAccess(meta));

        sql.ShouldBe("\"status\" = :p1");
        parameters.Count.ShouldBe(1);
        parameters["p1"].ShouldBe("PAID");
    }

    [Fact]
    public void SqlFilterProvider_TranslatesLogicalAndOr()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("or", new ListValueNode(
                new ObjectValueNode(
                    new ObjectFieldNode("status", new ObjectValueNode(
                        new ObjectFieldNode("eq", new StringValueNode("PAID"))
                    ))
                ),
                new ObjectValueNode(
                    new ObjectFieldNode("amount", new ObjectValueNode(
                        new ObjectFieldNode("gt", new IntValueNode(5000))
                    ))
                )
            ))
        );

        var (sql, parameters) = _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, SqlFilterProvider.UnrestrictedAccess(meta));

        sql.ShouldContain("OR");
        sql.ShouldContain("[status] = @p1");
        sql.ShouldContain("[amount] > @p2");
        parameters["p1"].ShouldBe("PAID");
        parameters["p2"].ShouldBe(5000L);
    }

    [Fact]
    public void SqlFilterProvider_RejectsUnknownColumn_PreventsSqlInjection()
    {
        var meta = CreateSampleMetadata();
        var maliciousFilter = new ObjectValueNode(
            new ObjectFieldNode("amount; DROP TABLE invoices; --", new ObjectValueNode(
                new ObjectFieldNode("eq", new StringValueNode("1"))
            ))
        );

        Should.Throw<InvalidOperationException>(() =>
        {
            _filterProvider.TranslateObjectValue(maliciousFilter, meta, DatabaseDialect.SqlServer, SqlFilterProvider.UnrestrictedAccess(meta));
        });
    }

    [Fact]
    public async Task SchemaSnapshot_BuildsValidGraphQLSchema()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>();

        var sp = services.BuildServiceProvider();
        var executor = await sp.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        var schema = executor.Schema;

        var sdl = schema.ToString();
        sdl.ShouldNotBeNull();
        sdl.ShouldContain("type Query");
        sdl.ShouldContain("type Mutation");
        sdl.ShouldContain("table(");
        sdl.ShouldContain("TableRecordPayload");
        sdl.ShouldContain("requestTableAccess");
        sdl.ShouldContain("approveConsentRequest");
        sdl.ShouldContain("revokeConsent");
    }

    [Fact]
    public void SqlFilterProvider_TranslatesInOperator_WithStandardList()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("status", new ObjectValueNode(
                new ObjectFieldNode("in", new ListValueNode(
                    new StringValueNode("PAID"),
                    new StringValueNode("PENDING")
                ))
            ))
        );

        var (sql, parameters) = _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, SqlFilterProvider.UnrestrictedAccess(meta));

        sql.ShouldBe("[status] IN (@p1, @p2)");
        parameters.Count.ShouldBe(2);
        parameters["p1"].ShouldBe("PAID");
        parameters["p2"].ShouldBe("PENDING");
    }

    [Fact]
    public void SqlFilterProvider_TranslatesInOperator_WhenListExceedsChunkSize_SplitsIntoOrConnectedInClauses()
    {
        // Sonderfall: RDBMS-Limit (z.B. SQLite 999 oder Oracle 1000). Wenn IN-Liste das Chunk-Limit überschreitet,
        // wird die Bedingung in OR-verknüpfte IN-Klauseln aufgeteilt: ((col IN (...)) OR (col IN (...)))
        var meta = CreateSampleMetadata();
        var items = Enumerable.Range(1, 5).Select(i => new IntValueNode(i)).ToList<IValueNode>();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("id", new ObjectValueNode(
                new ObjectFieldNode("in", new ListValueNode(items))
            ))
        );

        var providerWithSmallChunk = new SqlFilterProvider(maxInClauseSize: 2);
        var (sql, parameters) = providerWithSmallChunk.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, SqlFilterProvider.UnrestrictedAccess(meta));

        sql.ShouldBe("(([id] IN (@p1, @p2)) OR ([id] IN (@p3, @p4)) OR ([id] IN (@p5)))");
        parameters.Count.ShouldBe(5);
        parameters["p1"].ShouldBe(1L);
        parameters["p5"].ShouldBe(5L);
    }

    [Fact]
    public void SqlFilterProvider_TranslatesEndsWith()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("customer", new ObjectValueNode(
                new ObjectFieldNode("endswith", new StringValueNode("Corp"))
            ))
        );

        var (sql, parameters) = _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, SqlFilterProvider.UnrestrictedAccess(meta));

        sql.ShouldBe("[customer] LIKE @p1 ESCAPE '\\'");
        parameters.Count.ShouldBe(1);
        parameters["p1"].ShouldBe("%Corp");
    }

    [Fact]
    public void SqlFilterProvider_EscapesWildcardsInLikeClauses()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("customer", new ObjectValueNode(
                new ObjectFieldNode("contains", new StringValueNode("100%_promo[1]"))
            ))
        );

        var (sql, parameters) = _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, SqlFilterProvider.UnrestrictedAccess(meta));

        sql.ShouldBe("[customer] LIKE @p1 ESCAPE '\\'");
        parameters.Count.ShouldBe(1);
        parameters["p1"].ShouldBe("%100\\%\\_promo\\[1]%");
    }

    [Fact]
    public async Task DynamicTableType_WhenColumnIsBigint_MapsToLongType()
    {
        var table = new Table
        {
            SchemaName = "dbo",
            TableName = "transactions",
            DisplayName = "Transactions"
        };
        var columns = new[]
        {
            new TableColumn { ColumnName = "tx_id", DataType = "bigint" },
            new TableColumn { ColumnName = "quantity", DataType = "int" }
        };
        var meta = new TableMetadata
        {
            Table = table,
            Identifier = new TableIdentifier("finance", "dbo", "transactions"),
            Columns = columns
        };

        var masking = NSubstitute.Substitute.For<IColumnMaskingProvider>();

        var schema = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType(d => d.Name("Query").Field("test").Resolve(_ => "ok"))
            .AddType(new Autheris.GraphQL.DynamicTypes.DynamicTableType(meta, masking))
            .BuildSchemaAsync();

        var dynamicType = schema.Types.GetType<IObjectTypeDefinition>("finance_transactions");
        dynamicType.ShouldNotBeNull();
        ((INameProvider)dynamicType.Fields["tx_id"].Type).Name.ShouldBe("Long");
        ((INameProvider)dynamicType.Fields["quantity"].Type).Name.ShouldBe("Int");
    }

    [Fact]
    public void SqlFilterProvider_WhenColumnAccessIsMaskOrDeny_ThrowsSecurityException()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("amount", new ObjectValueNode(
                new ObjectFieldNode("gte", new IntValueNode(100))
            ))
        );

        var colAccessMask = new Dictionary<string, ColumnAccessLevel>
        {
            ["amount"] = ColumnAccessLevel.Mask
        };

        Should.Throw<System.Security.SecurityException>(() =>
        {
            _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, colAccessMask);
        }).Message.ShouldContain("Zero-Trust-Verletzung");

        var colAccessDeny = new Dictionary<string, ColumnAccessLevel>
        {
            ["amount"] = ColumnAccessLevel.Deny
        };

        Should.Throw<System.Security.SecurityException>(() =>
        {
            _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, colAccessDeny);
        }).Message.ShouldContain("Zero-Trust-Verletzung");
    }

    [Fact]
    public void SqlFilterProvider_WhenColumnAccessIsClear_AllowsFiltering()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("amount", new ObjectValueNode(
                new ObjectFieldNode("gte", new IntValueNode(100))
            ))
        );

        var colAccessClear = new Dictionary<string, ColumnAccessLevel>
        {
            ["amount"] = ColumnAccessLevel.Clear
        };

        var (sql, parameters) = _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, colAccessClear);
        sql.ShouldBe("[amount] >= @p1");
        parameters["p1"].ShouldBe(100L);
    }

    [Fact]
    public void SqlFilterProvider_WhenColumnAccessIsNull_ThrowsArgumentNullExceptionFailClosed()
    {
        var meta = CreateSampleMetadata();
        var filterNode = new ObjectValueNode(
            new ObjectFieldNode("amount", new ObjectValueNode(
                new ObjectFieldNode("gte", new IntValueNode(100))
            ))
        );

        Should.Throw<ArgumentNullException>(() =>
        {
            _filterProvider.TranslateObjectValue(filterNode, meta, DatabaseDialect.SqlServer, null!);
        });
    }
}


