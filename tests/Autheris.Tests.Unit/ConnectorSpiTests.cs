using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.Adapters;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Connectors;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class ConnectorSpiTests
{
    private static TableMetadata CreateSampleMetadata(string domain = "finance", string schema = "dbo", string table = "Invoices", string sourceName = "test-sql", string sourceType = "PostgreSQL")
    {
        var identifier = new TableIdentifier(domain, schema, table);
        return new TableMetadata
        {
            Table = new Table
            {
                SourceName = sourceName,
                SchemaName = schema,
                TableName = table,
                DisplayName = "Customer Invoices",
                DataSourceType = DataSourceType.Sql,
                SourceType = sourceType
            },
            Columns =
            [
                new TableColumn { ColumnName = "Id", DataType = "int" },
                new TableColumn { ColumnName = "Amount", DataType = "decimal" },
                new TableColumn { ColumnName = "Iban", DataType = "varchar", IsSensitive = true },
                new TableColumn { ColumnName = "SecretNote", DataType = "varchar", IsSensitive = true }
            ]
        };
    }

    private static SqlConnector NewSqlConnector(
        string connectorId,
        ITableMetadataRepository metadataRepository,
        IGovernedSqlExecutionService? governed = null,
        IOptions<GatewayOptions>? options = null)
    {
        var governedService = governed ?? NSubstitute.Substitute.For<IGovernedSqlExecutionService>();
        var services = new ServiceCollection();
        services.AddScoped<IDataSourceExecutor>(_ => new SqlTableReadExecutor(new GovernedTableReadPlanner(), () => governedService));
        return new SqlConnector(
            connectorId,
            metadataRepository,
            NSubstitute.Substitute.For<IColumnMaskingProvider>(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            options);
    }

    private static ClaimsPrincipal CreatePrincipal(string sid = "S-1-5-21-TEST", string role = "Analyst")
    {
        var identity = new ClaimsIdentity("TestAuth");
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, sid));
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public void ConnectorCapabilities_FlagsAndDefaults_AreCorrect()
    {
        var sqlCaps = ConnectorCapabilities.DefaultSql;
        sqlCaps.HasFeature(ConnectorFeatures.FilterPushdown).ShouldBeTrue();
        sqlCaps.HasFeature(ConnectorFeatures.ProjectionPushdown).ShouldBeTrue();
        sqlCaps.HasFeature(ConnectorFeatures.LimitPushdown).ShouldBeTrue();
        sqlCaps.HasFeature(ConnectorFeatures.StreamingExecution).ShouldBeTrue();
        sqlCaps.SupportsTransactions.ShouldBeTrue();
        sqlCaps.MaxBatchSize.ShouldBe(5000);

        var httpCaps = ConnectorCapabilities.DefaultHttp;
        httpCaps.HasFeature(ConnectorFeatures.FilterPushdown).ShouldBeTrue();
        httpCaps.HasFeature(ConnectorFeatures.ProjectionPushdown).ShouldBeFalse();
        httpCaps.SupportsTransactions.ShouldBeFalse();
    }

    [Fact]
    public void InMemoryConnectorRegistry_RegisterAndRetrieve_Succeeds()
    {
        var registry = new InMemoryConnectorRegistry();
        var mockConnector = new LegacyDataSourceExecutorAdapter(new FakeSqlExecutor(), "catalog-a");

        registry.RegisterConnector("catalog-a", mockConnector);

        registry.GetConnector("catalog-a").ShouldBeSameAs(mockConnector);
        registry.GetAllConnectors().Count.ShouldBe(1);
    }

    [Fact]
    public void InMemoryConnectorRegistry_MaliciousCatalogName_ThrowsArgumentException()
    {
        var registry = new InMemoryConnectorRegistry();
        var mockConnector = new LegacyDataSourceExecutorAdapter(new FakeSqlExecutor());

        Should.Throw<ArgumentException>(() =>
            registry.RegisterConnector("../../etc/passwd", mockConnector));

        Should.Throw<ArgumentException>(() =>
            registry.RegisterConnector("catalog with spaces!", mockConnector));
    }

    [Fact]
    public void InMemoryConnectorRegistry_TryGetConnectorForTable_ResolvesByDomainOrFallback()
    {
        var registry = new InMemoryConnectorRegistry();
        var financeConnector = new LegacyDataSourceExecutorAdapter(new FakeSqlExecutor(), "finance");
        var defaultSqlConnector = new LegacyDataSourceExecutorAdapter(new FakeSqlExecutor(), "default-sql");

        registry.RegisterConnector("finance", financeConnector);
        registry.RegisterConnector("default-sql", defaultSqlConnector);

        // 1. Direct domain match
        var financeTable = new TableIdentifier("finance", "dbo", "Invoices");
        registry.TryGetConnectorForTable(financeTable, out var resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(financeConnector);

        // 2. Fallback to default-sql for other domains
        var hrTable = new TableIdentifier("hr", "dbo", "Employees");
        registry.TryGetConnectorForTable(hrTable, out var fallbackResolved).ShouldBeTrue();
        fallbackResolved.ShouldBeSameAs(defaultSqlConnector);
    }

    [Fact]
    public async Task SqlConnector_FailClosed_ThrowsSecurityExceptionWhenAccessDenied()
    {
        var meta = CreateSampleMetadata();
        var principal = CreatePrincipal();
        var decision = TableAccessDecision.Denied(meta.Identifier, "Policy forbids access");

        var sqlConnector = NewSqlConnector("test-sql", new FakeMetadataRepository([meta]));

        var session = new ConnectorSessionContext(
            Principal: principal,
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["Id", "Amount"],
            Arguments: new Dictionary<string, object?>());

        session.Items["TableMetadata"] = meta;
        var split = ConnectorSplit.Default();

        var ex = await Should.ThrowAsync<SecurityException>(async () =>
        {
            await sqlConnector.RecordSource.ReadBatchAsync(split, session);
        });

        ex.Message.ShouldContain("Zero-Trust violation");
    }

    [Fact]
    public async Task SqlConnector_SideChannelInferenceProtection_ThrowsOnMaskedColumnFilter()
    {
        var meta = CreateSampleMetadata();
        var principal = CreatePrincipal();
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>
        {
            ["Id"] = ColumnAccessLevel.Clear,
            ["Amount"] = ColumnAccessLevel.Clear,
            ["Iban"] = ColumnAccessLevel.Mask
        });

        var sqlConnector = NewSqlConnector("test-sql", new FakeMetadataRepository([meta]));

        // Attacker attempts to infer masked IBAN via WHERE argument
        var session = new ConnectorSessionContext(
            Principal: principal,
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["Id", "Amount"],
            Arguments: new Dictionary<string, object?>
            {
                ["Iban"] = "DE1234567890"
            });

        session.Items["TableMetadata"] = meta;
        var split = ConnectorSplit.Default();

        var ex = await Should.ThrowAsync<SecurityException>(async () =>
        {
            await sqlConnector.RecordSource.ReadBatchAsync(split, session);
        });

        ex.Message.ShouldContain("Zero-Trust violation");
        ex.Message.ShouldContain("Iban");
    }

    [Fact]
    public async Task SqlConnector_PushdownFilterSql_ValidatesAgainstInjection()
    {
        var meta = CreateSampleMetadata();
        var principal = CreatePrincipal();
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var sqlConnector = NewSqlConnector("test-sql", new FakeMetadataRepository([meta]));

        // Poisoned RLS predicate containing stacked query
        var session = new ConnectorSessionContext(
            Principal: principal,
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["Id"],
            Arguments: new Dictionary<string, object?>(),
            PushdownFilterSql: "1=1; DROP TABLE Invoices; --");

        session.Items["TableMetadata"] = meta;
        var split = ConnectorSplit.Default();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await sqlConnector.RecordSource.ReadBatchAsync(split, session);
        });
    }

    [Fact]
    public async Task SqlConnector_ReadsThroughTheGovernedPipeline_WithThePlannedStatement()
    {
        var meta = CreateSampleMetadata(sourceName: "lwetem_prod", sourceType: "SqlServer");
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var governed = NSubstitute.Substitute.For<IGovernedSqlExecutionService>();
        governed.ExecuteQueryBufferedAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(new GovernedSqlResult(
                "", ["Id"], [new Dictionary<string, object?> { ["Id"] = 7 }], 1, 0));

        var sqlConnector = NewSqlConnector("default-sql", new FakeMetadataRepository([meta]), governed);

        var session = new ConnectorSessionContext(
            Principal: CreatePrincipal(),
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["Id"],
            Arguments: new Dictionary<string, object?>());
        session.Items["TableMetadata"] = meta;

        var batch = await sqlConnector.RecordSource.ReadBatchAsync(ConnectorSplit.Default(), session);

        batch.Count.ShouldBe(1);
        batch[0]["Id"].ShouldBe(7);
        // The statement is the planner's Trino text for the catalog table; the connection is the governed pipeline's concern.
        await governed.Received(1).ExecuteQueryBufferedAsync(
            Arg.Is<GovernedSqlQueryRequest>(r => r.Sql.Contains("FROM finance.dbo.Invoices", StringComparison.Ordinal) && r.Sql.Contains("\"Id\"", StringComparison.Ordinal)),
            Arg.Any<ClaimsPrincipal>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SQL202_SqlConnector_RespectsSessionLimitBeyond5000()
    {
        var memConnStr = $"Data Source=sql202_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var masterConn = new SqliteConnection(memConnStr);
        masterConn.Open();

        using (var cmd = masterConn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE orders (id INT, tenant_id TEXT);";
            cmd.ExecuteNonQuery();

            using var tx = masterConn.BeginTransaction();
            using var insertCmd = masterConn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = "INSERT INTO orders VALUES ($id, 'tenant-1');";
            var p = insertCmd.CreateParameter();
            p.ParameterName = "$id";
            insertCmd.Parameters.Add(p);

            for (int i = 1; i <= 6000; i++)
            {
                p.Value = i;
                insertCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        var table = new TableIdentifier("sales", "main", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Table = new Table { SchemaName = "main", TableName = "orders", SourceName = "sqlite_ds", SourceType = "Sqlite" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "tenant_id", DataType = "text" }]
        };

        var options = Options.Create(new GatewayOptions
        {
            DuckDbOlap = new DuckDbOlapOptions { MaxStagedRowsPerTable = 25000 }
        });

        var env = new Autheris.Tests.Unit.Sql.TestCompileEnv(TrinoSqlEngine.TargetSqlDialect.Sqlite)
            .Table("orders", "tenant_id", "id:INT", "tenant_id");
        var governed = new Autheris.Tests.Unit.TestDoubles.SqliteGovernedSqlService(masterConn, env);
        var sqlConnector = NewSqlConnector("default-sql", new FakeMetadataRepository([meta]), governed, options);

        var decision = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var session = new ConnectorSessionContext(
            Principal: CreatePrincipal(),
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["id"],
            Arguments: new Dictionary<string, object?>(),
            Limit: 6000);
        session.Items["TableMetadata"] = meta;

        var batch = await sqlConnector.RecordSource.ReadBatchAsync(ConnectorSplit.Default(), session);

        // Before fix: batch.Count was 5000 because of Math.Clamp(..., 1, 5000).
        // After fix: batch.Count is 6000!
        batch.Count.ShouldBe(6000);
        governed.Requests.Single().Sql.ShouldContain("LIMIT 6000");
    }

    private sealed class FakeSqlExecutor : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            DataSourceExecutionContext context,
            CancellationToken ct = default)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
            [
                new Dictionary<string, object?>
                {
                    ["Id"] = 101,
                    ["Amount"] = 250.0m
                }
            ];
            return Task.FromResult(rows);
        }
    }

    private sealed class FakeMetadataRepository : ITableMetadataRepository
    {
        private readonly List<TableMetadata> _tables;

        public FakeMetadataRepository(IEnumerable<TableMetadata> tables)
        {
            _tables = [.. tables];
        }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            return Task.FromResult(_tables.Find(t => t.Identifier.Equals(table)));
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<TableMetadata>>(_tables);
        }

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            _tables.RemoveAll(t => t.Identifier.Equals(metadata.Identifier));
            _tables.Add(metadata);
            return Task.FromResult(metadata);
        }
    }
}
