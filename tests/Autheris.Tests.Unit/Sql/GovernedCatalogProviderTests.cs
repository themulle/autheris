using System.Collections.Immutable;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.Sql;

/// <summary>
/// WP-SIMPL-44 / SEC-SIMPL-142: the catalog is built from declared metadata only, fail closed.
/// No connection factory or source metadata reader is used.
/// </summary>
public sealed class GovernedCatalogProviderTests
{
    private const string Source = "ds";
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TableColumn Col(string name, string type = "int", string? collation = null) =>
        new() { ColumnName = name, DataType = type, Collation = collation };

    private static IGovernanceRepository Governance(string provider, params TableColumn[] columns)
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = provider, SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = columns
        });
        return repo;
    }

    private static (GovernedCatalogProvider Provider, Clock Clock, IGovernanceRepository Governance) Build(
        string provider, TableColumn[] columns, int ttl = 300, IGovernanceRepository? governance = null, string? tenantColumn = null)
    {
        var gateway = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = { [Source] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "x" } }
            }
        };
        var clock = new Clock();
        var repo = governance ?? Substitute.For<IGovernanceRepository>();
        if (governance is null)
        {
            repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
            {
                Table = new Table { SourceType = provider, SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
                Identifier = Orders,
                Columns = columns,
                TenantColumnName = tenantColumn
            });
        }

        var sut = new GovernedCatalogProvider(repo, Options.Create(gateway),
            new GovernedCatalogOptions { CacheTtlSeconds = ttl }, clock);
        return (sut, clock, repo);
    }

    private static TableCatalogEntry? Resolve(ITableCatalog catalog) =>
        catalog.Resolve(new SqlQualifiedName(new[] { new SqlIdentifier("dbo"), new SqlIdentifier("orders") }));

    [Fact]
    [Trait("Category", "Security")]
    public async Task Admission_UsesDeclaredColumnsOnly_NoConnectionOpened()
    {
        // SEC-SIMPL-142: ISqlConnectionFactory is absent from constructor, declared metadata only
        var columns = new[]
        {
            Col("id", "int"),
            Col("tenant_id", "nvarchar(64)", "SQL_Latin1_General_CP1_CI_AS"),
            Col("amount", "decimal(18,2)")
        };
        var (sut, _, _) = Build("SqlServer", columns, tenantColumn: "tenant_id");

        var catalog = await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        var entry = Resolve(catalog);

        entry.ShouldNotBeNull();
        entry.Columns.Select(c => (c.Name, c.DataType, c.Collation)).ShouldBe(new[]
        {
            ("id", "int", (string?)null),
            ("tenant_id", "nvarchar(64)", "SQL_Latin1_General_CP1_CI_AS"),
            ("amount", "decimal(18,2)", null)
        });
        entry.TenantColumn.ShouldBe("tenant_id");
        entry.Version.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task DeclaredTypeAndCollation_FlowIntoCatalogColumn()
    {
        var columns = new[] { Col("name", "nvarchar(128)", "Latin1_General_BIN2") };
        var (sut, _, _) = Build("SqlServer", columns);

        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));
        entry.ShouldNotBeNull();
        var col = entry.Columns.ShouldHaveSingleItem();
        col.Name.ShouldBe("name");
        col.DataType.ShouldBe("nvarchar(128)");
        col.Collation.ShouldBe("Latin1_General_BIN2");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FingerprintChanges_WhenDeclaredTypeOrCollationChanges()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = new[] { Col("amount", "decimal(10,2)") }
        });

        var (sut, _, _) = Build("SqlServer", Array.Empty<TableColumn>(), governance: repo);
        var first = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));
        first.ShouldNotBeNull();
        first.Columns[0].DataType.ShouldBe("decimal(10,2)");

        // Update metadata with changed type: fingerprint must change so cache misses and reloads
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = new[] { Col("amount", "decimal(18,4)") }
        });

        var second = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));
        second.ShouldNotBeNull();
        second.Columns[0].DataType.ShouldBe("decimal(18,4)");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UndeclaredTenantColumn_Denied()
    {
        var columns = new[] { Col("id", "int") };
        var (sut, _, _) = Build("SqlServer", columns, tenantColumn: "tenant_id");

        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));
        entry.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NullCollationStringKey_NotBindable()
    {
        // SEC-SIMPL-142: string keys with unknown collation are not bindable
        KeySetKeyTypes.IsBindablePair(
            TargetSqlDialect.SqlServer, "varchar(20)", "varchar(20)",
            null, "Latin1_General_BIN2").ShouldBeFalse();

        KeySetKeyTypes.IsBindablePair(
            TargetSqlDialect.SqlServer, "varchar(20)", "varchar(20)",
            null, null).ShouldBeFalse();

        KeySetKeyTypes.IsBindablePair(
            TargetSqlDialect.SqlServer, "nvarchar(20)", "nvarchar(20)",
            null, "UTF8_BINARY").ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Databricks_admission_follows_tier_only()
    {
        var columns = new[] { Col("id", "int"), Col("tenantid", "string", "UTF8_BINARY") };
        var (sut, _, _) = Build("Databricks", columns, tenantColumn: "tenantid");

        var catalog = await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        var entry = Resolve(catalog);
        entry.ShouldNotBeNull();
        entry.TenantColumn.ShouldBe("tenantid");
    }

    [Fact]
    public async Task ZeroColumns_DeniesTable()
    {
        var (sut, _, _) = Build("SqlServer", Array.Empty<TableColumn>());
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task LoadFailure_FailsClosed()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns<Task<TableMetadata?>>(_ => throw new InvalidOperationException("boom"));

        var (sut, _, _) = Build("SqlServer", Array.Empty<TableColumn>(), governance: repo);
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task UnknownDataSource_FailsClosed()
    {
        var columns = new[] { Col("id", "int") };
        var (sut, _, _) = Build("SqlServer", columns);

        Resolve(await sut.GetCatalogAsync("other", new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ColumnTypeChange_ChangesVersion()
    {
        var col1 = new[] { Col("id", "int"), Col("amount", "decimal(18,2)") };
        var col2 = new[] { Col("id", "int"), Col("amount", "decimal(18,3)") };
        CatalogColumnVersion.Compute(col1).ShouldNotBe(CatalogColumnVersion.Compute(col2));
    }

    [Fact]
    public async Task CollationChange_ChangesVersion()
    {
        var col1 = new[] { Col("id", "int"), Col("tenant_id", "nvarchar(64)", "SQL_Latin1_General_CP1_CI_AS") };
        var col2 = new[] { Col("id", "int"), Col("tenant_id", "nvarchar(64)", "Latin1_General_BIN2") };
        CatalogColumnVersion.Compute(col1).ShouldNotBe(CatalogColumnVersion.Compute(col2));
    }

    [Fact]
    public async Task CacheWithinTtl_DoesNotReload_ExpiryReloads()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = new[] { Col("id", "int") }
        });

        var (sut, clock, _) = Build("SqlServer", Array.Empty<TableColumn>(), ttl: 60, governance: repo);
        await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(59);
        await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        // Table was loaded once + fingerprint check once
        await repo.Received(2).GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>());

        clock.Now += TimeSpan.FromSeconds(2);
        await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        // Expired: reloaded
        await repo.Received(3).GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantColumnGovernedAfterCachedLoad_TakesEffectWithinTheTtl()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = new[] { Col("id", "int"), Col("amount", "decimal(18,2)") }
        });

        var (sut, _, _) = Build("SqlServer", Array.Empty<TableColumn>(), governance: repo);
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None))!.TenantColumn.ShouldBeNull();

        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = new[] { Col("id", "int"), Col("amount", "decimal(18,2)"), Col("tenant_id", "nvarchar(64)") },
            TenantColumnName = "tenant_id"
        });

        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));
        entry.ShouldNotBeNull();
        entry.TenantColumn.ShouldBe("tenant_id");
    }

    [Fact]
    public async Task GovernedColumnRemovedAfterCachedLoad_IsNoLongerExposedWithinTheTtl()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = new[] { Col("id", "int"), Col("amount", "decimal(18,2)") }
        });

        var (sut, _, _) = Build("SqlServer", Array.Empty<TableColumn>(), governance: repo);
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None))!.Columns.Select(c => c.Name).ShouldContain("amount");

        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = new[] { Col("id", "int") }
        });

        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));
        entry.ShouldNotBeNull();
        entry.Columns.Select(c => c.Name).ShouldBe(new[] { "id" });
    }

    [Fact]
    public async Task GovernanceRecordRemovedAfterCachedLoad_DeniesTable()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = new[] { Col("id", "int") }
        });

        var (sut, _, _) = Build("SqlServer", Array.Empty<TableColumn>(), governance: repo);
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldNotBeNull();

        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns((TableMetadata?)null);
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("INTEGER")]
    [InlineData("NUMERIC")]
    [InlineData("DECIMAL(10,2)")]
    [InlineData("REAL")]
    [InlineData("BLOB")]
    [InlineData("")]
    [InlineData("BOOLEAN")]
    public async Task Sqlite_TenantColumnNonTextAffinity_DeniesTable(string declaredType)
    {
        var columns = new[] { Col("id", "INTEGER"), Col("tenant_id", declaredType) };
        var (sut, _, _) = Build("Sqlite", columns, tenantColumn: "tenant_id");

        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("VARCHAR(64)")]
    [InlineData("nvarchar(10)")]
    [InlineData("CLOB")]
    public async Task Sqlite_TenantColumnTextAffinity_IsAdmitted_WithNullCollation(string declaredType)
    {
        var columns = new[] { Col("id", "INTEGER"), Col("tenant_id", declaredType) };
        var (sut, _, _) = Build("Sqlite", columns, tenantColumn: "tenant_id");

        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));
        entry.ShouldNotBeNull();
        entry.Columns.ShouldAllBe(c => c.Collation == null);
    }

    [Theory]
    [InlineData("INT", SqliteAffinity.IntegerAffinity)]
    [InlineData("BIGINT", SqliteAffinity.IntegerAffinity)]
    [InlineData("CHARACTER(20)", SqliteAffinity.TextAffinity)]
    [InlineData("TEXT", SqliteAffinity.TextAffinity)]
    [InlineData("CLOB", SqliteAffinity.TextAffinity)]
    [InlineData("BLOB", SqliteAffinity.BlobAffinity)]
    [InlineData("", SqliteAffinity.BlobAffinity)]
    [InlineData("DOUBLE PRECISION", SqliteAffinity.RealAffinity)]
    [InlineData("FLOAT", SqliteAffinity.RealAffinity)]
    [InlineData("NUMERIC", SqliteAffinity.NumericAffinity)]
    [InlineData("BOOLEAN", SqliteAffinity.NumericAffinity)]
    [InlineData("DATETIME", SqliteAffinity.NumericAffinity)]
    [InlineData("POINT", SqliteAffinity.IntegerAffinity)]
    public void SqliteAffinity_FollowsTheDocumentedRules(string declared, SqliteAffinity expected) =>
        SqliteTypeAffinity.Of(declared).ShouldBe(expected);

    [Fact]
    public async Task CatalogCollation_NeverAppearsInEmittedSql()
    {
        const string collation = "SQL_Latin1_General_CP1_CI_AS";
        var columns = new[] { Col("id", "int"), Col("tenant_id", "nvarchar(64)", collation) };
        var (sut, _, _) = Build("SqlServer", columns);
        var catalog = await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        var policies = new NoPolicies();

        var compiled = new FastSqlEngine().Compile("SELECT id FROM dbo.orders".AsMemory(), new CompileRequest
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = policies,
                Masks = policies,
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        }, CancellationToken.None);

        compiled.Sql.ShouldNotContain(collation, Case.Insensitive);
        compiled.Sql.ShouldNotContain("COLLATE", Case.Insensitive);
        compiled.Parameters.ShouldAllBe(p => !string.Equals(p.Value as string, collation, StringComparison.Ordinal));
    }

    [Fact]
    public void SEC_SIMPL_141_OracleCompiledSql_DoesNotTripGuard()
    {
        var orders = new TableIdentity("AUTH_APP", "ORDERS");
        var catalog = new InMemoryTableCatalog(new[]
        {
            new TableCatalogEntry(orders, ImmutableArray.Create(
                new CatalogColumn("ID", "NUMBER(10)"), new CatalogColumn("TENANT_ID", "VARCHAR2(64)"), new CatalogColumn("REGION", "VARCHAR2(20)"),
                new CatalogColumn("STATUS", "VARCHAR2(20)"), new CatalogColumn("AMOUNT", "NUMBER(18,2)"), new CatalogColumn("EMAIL", "VARCHAR2(200)")),
                "TENANT_ID", 1)
        }, "AUTH_APP");

        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.Oracle,
            AllowRecursiveCte = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new NoPolicies(),
                Masks = new NoPolicies(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };

        var compiled = new FastSqlEngine().Compile("SELECT id, email FROM AUTH_APP.orders".AsMemory(), request, CancellationToken.None);
        var violated = SourceMetadataGuard.TryFindViolation(compiled.Sql, DatabaseDialect.Oracle, out var rule);
        violated.ShouldBeFalse($"SQL was: '{compiled.Sql}', Rule was: '{rule}'");
    }

    private sealed class NoPolicies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }
}
