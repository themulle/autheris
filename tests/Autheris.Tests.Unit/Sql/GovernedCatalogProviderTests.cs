using System.Data.Common;
using System.Runtime.CompilerServices;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.Sql;

/// <summary>WP-X0c: the catalog is built from database metadata, fail closed (CR-ADG-47, SEC-X1-09).</summary>
public sealed class GovernedCatalogProviderTests
{
    private const string Source = "ds";
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private sealed class FakeReader(TargetSqlDialect dialect) : IColumnMetadataReader
    {
        public TargetSqlDialect Dialect { get; } = dialect;
        public List<DbColumnMetadata> Columns { get; set; } = new();
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }

        public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            foreach (var c in Columns.ToArray()) yield return c;
            await Task.CompletedTask;
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TableColumn Gov(string name) => new() { ColumnName = name };

    private static IGovernanceRepository Governance(string provider, params string[] columns)
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = provider, SourceName = Source, SchemaName = "dbo", TableName = "orders" },
            Identifier = Orders,
            Columns = columns.Select(Gov).ToArray()
        });
        return repo;
    }

    private static (GovernedCatalogProvider Provider, FakeReader Reader, Clock Clock) Build(
        string provider, TargetSqlDialect dialect, string[] govColumns, IEnumerable<DbColumnMetadata> dbColumns, int ttl = 300,
        IGovernanceRepository? governance = null)
    {
        var reader = new FakeReader(dialect) { Columns = dbColumns.ToList() };
        var gateway = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = { [Source] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "x" } }
            }
        };
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<DbConnection>(new SqliteConnection()));
        var clock = new Clock();
        var sut = new GovernedCatalogProvider(governance ?? Governance(provider, govColumns), Options.Create(gateway), factory,
            new[] { reader }, new GovernedCatalogOptions { CacheTtlSeconds = ttl }, clock);
        return (sut, reader, clock);
    }

    private static TableCatalogEntry? Resolve(ITableCatalog catalog) =>
        catalog.Resolve(new SqlQualifiedName(new[] { new SqlIdentifier("dbo"), new SqlIdentifier("orders") }));

    private static readonly DbColumnMetadata[] SqlServerColumns =
    {
        new("id", "int", null),
        new("tenant_id", "nvarchar(64)", "SQL_Latin1_General_CP1_CI_AS"),
        new("amount", "decimal(18,2)", null)
    };

    [Fact]
    public async Task ExposedColumns_AreGovernanceIntersectedWithDatabase_WithDatabaseTypes()
    {
        var (sut, _, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "ID", "Tenant_Id" }, SqlServerColumns);

        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));

        entry.ShouldNotBeNull();
        entry.Columns.Select(c => (c.Name, c.DataType, c.Collation)).ShouldBe(new[]
        {
            ("id", "int", (string?)null), ("tenant_id", "nvarchar(64)", "SQL_Latin1_General_CP1_CI_AS")
        });
        entry.TenantColumn.ShouldBe("tenant_id");
        entry.Version.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Databricks_TableIsLoadedThroughTheMapperAndTheDatabricksReader()
    {
        // WP-X0j (written after the wiring; a guard, not a Red test): the Databricks dialect is mapped, so the table is no longer denied by default.
        var (sut, reader, _) = Build("Databricks", TargetSqlDialect.Databricks, new[] { "id", "tenantid" },
            new DbColumnMetadata[] { new("id", "int", null), new("tenantid", "string", "UTF8_BINARY") });

        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));

        reader.Calls.ShouldBe(1);
        entry.ShouldNotBeNull();
        entry.Columns.Select(c => (c.Name, c.DataType, c.Collation)).ShouldBe(new[] { ("id", "int", (string?)null), ("tenantid", "string", "UTF8_BINARY") });
    }

    [Fact]
    public async Task GovernanceColumnMissingInDatabase_DeniesTable()
    {
        var (sut, _, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id", "tenant_id", "ghost" }, SqlServerColumns);

        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ZeroColumns_DeniesTable()
    {
        var (sut, _, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, Array.Empty<DbColumnMetadata>());

        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task LoadFailure_FailsClosed()
    {
        var (sut, reader, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, SqlServerColumns);
        reader.Failure = new InvalidOperationException("boom");

        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task UnknownDataSource_FailsClosed()
    {
        var (sut, _, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, SqlServerColumns);

        Resolve(await sut.GetCatalogAsync("other", new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ColumnTypeChange_ChangesVersion()
    {
        var (sut, reader, clock) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id", "amount" }, SqlServerColumns);
        long before = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None))!.Version;

        reader.Columns = SqlServerColumns.Select(c => c.Name == "amount" ? c with { NativeType = "decimal(18,3)" } : c).ToList();
        clock.Now += TimeSpan.FromSeconds(301);
        long after = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None))!.Version;

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task CollationChange_ChangesVersion()
    {
        var (sut, reader, clock) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id", "tenant_id" }, SqlServerColumns);
        long before = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None))!.Version;

        reader.Columns = SqlServerColumns.Select(c => c.Name == "tenant_id" ? c with { Collation = "Latin1_General_BIN2" } : c).ToList();
        clock.Now += TimeSpan.FromSeconds(301);
        long after = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None))!.Version;

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task CacheWithinTtl_DoesNotReload_ExpiryReloads()
    {
        var (sut, reader, clock) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, SqlServerColumns, ttl: 60);
        await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(59);
        await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        reader.Calls.ShouldBe(1);

        clock.Now += TimeSpan.FromSeconds(2);
        await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        reader.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task TenantColumnGovernedAfterCachedLoad_TakesEffectWithinTheTtl()
    {
        var repo = Governance("SqlServer", "id", "amount");
        var (sut, reader, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id", "amount" }, SqlServerColumns, governance: repo);
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None))!.TenantColumn.ShouldBeNull();

        SetGovernedColumns(repo, "SqlServer", "id", "amount", "tenant_id");
        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));

        entry.ShouldNotBeNull();
        entry.TenantColumn.ShouldBe("tenant_id");
        reader.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task GovernedColumnRemovedAfterCachedLoad_IsNoLongerExposedWithinTheTtl()
    {
        var repo = Governance("SqlServer", "id", "amount");
        var (sut, _, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id", "amount" }, SqlServerColumns, governance: repo);
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None))!.Columns.Select(c => c.Name).ShouldContain("amount");

        SetGovernedColumns(repo, "SqlServer", "id");
        var entry = Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None));

        entry.ShouldNotBeNull();
        entry.Columns.Select(c => c.Name).ShouldBe(new[] { "id" });
    }

    [Fact]
    public async Task GovernanceRecordRemovedAfterCachedLoad_DeniesTable()
    {
        var repo = Governance("SqlServer", "id");
        var (sut, _, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, SqlServerColumns, governance: repo);
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldNotBeNull();

        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns((TableMetadata?)null);

        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task UnchangedGovernance_StillServedFromCache()
    {
        var (sut, reader, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, SqlServerColumns);
        await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);
        await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None);

        reader.Calls.ShouldBe(1);
    }

    private static void SetGovernedColumns(IGovernanceRepository repo, string provider, params string[] columns) =>
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = provider, SourceName = Source, SchemaName = "dbo", TableName = "orders" },
            Identifier = Orders,
            Columns = columns.Select(Gov).ToArray()
        });

    [Fact]
    public async Task FailedLoad_IsNotCached_NextCallReloads()
    {
        var (sut, reader, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, SqlServerColumns);
        reader.Failure = new InvalidOperationException("boom");
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();

        reader.Failure = null;
        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    [InlineData(int.MaxValue)]
    public void TtlAboveMaximum_RejectedAtStartup(int ttl)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, SqlServerColumns, ttl));
    }

    [Fact]
    public void TtlAtMaximum_IsAccepted() =>
        Should.NotThrow(() => Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id" }, SqlServerColumns, 300));

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
        var (sut, _, _) = Build("Sqlite", TargetSqlDialect.Sqlite, new[] { "id", "tenant_id" },
            new[] { new DbColumnMetadata("id", "INTEGER", null), new DbColumnMetadata("tenant_id", declaredType, null) });

        Resolve(await sut.GetCatalogAsync(Source, new[] { Orders }, CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("VARCHAR(64)")]
    [InlineData("nvarchar(10)")]
    [InlineData("CLOB")]
    public async Task Sqlite_TenantColumnTextAffinity_IsAdmitted_WithNullCollation(string declaredType)
    {
        var (sut, _, _) = Build("Sqlite", TargetSqlDialect.Sqlite, new[] { "id", "tenant_id" },
            new[] { new DbColumnMetadata("id", "INTEGER", null), new DbColumnMetadata("tenant_id", declaredType, null) });

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
    [InlineData("POINT", SqliteAffinity.IntegerAffinity)]   // contains "INT"
    public void SqliteAffinity_FollowsTheDocumentedRules(string declared, SqliteAffinity expected) =>
        SqliteTypeAffinity.Of(declared).ShouldBe(expected);

    [Fact]
    public async Task CatalogCollation_NeverAppearsInEmittedSql()
    {
        const string collation = "SQL_Latin1_General_CP1_CI_AS";
        var (sut, _, _) = Build("SqlServer", TargetSqlDialect.SqlServer, new[] { "id", "tenant_id" }, SqlServerColumns);
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

    private sealed class NoPolicies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }
}
