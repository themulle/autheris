using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.Adapters;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Connectors;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
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

        var sqlConnector = new SqlConnector(
            connectorId: "test-sql",
            connectionFactory: null!,
            metadataRepository: new FakeMetadataRepository([meta]));

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

        ex.Message.ShouldContain("Zero-Trust-Verletzung");
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

        var sqlConnector = new SqlConnector(
            connectorId: "test-sql",
            connectionFactory: null!,
            metadataRepository: new FakeMetadataRepository([meta]));

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

        ex.Message.ShouldContain("Zero-Trust-Verletzung");
        ex.Message.ShouldContain("Iban");
    }

    [Fact]
    public async Task SqlConnector_PushdownFilterSql_ValidatesAgainstInjection()
    {
        var meta = CreateSampleMetadata();
        var principal = CreatePrincipal();
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var sqlConnector = new SqlConnector(
            connectorId: "test-sql",
            connectionFactory: null!,
            metadataRepository: new FakeMetadataRepository([meta]));

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
    public async Task SqlConnector_ResolvesConnectionByTableSourceName_NotByConnectorId()
    {
        // Connection is configured under the table's data source ("lwetem_prod"), the connector is registered as "default-sql".
        var meta = CreateSampleMetadata(sourceName: "lwetem_prod", sourceType: "SqlServer");
        var decision = TableAccessDecision.Allowed(meta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections =
                {
                    ["lwetem_prod"] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=db;Database=x;" }
                }
            }
        });

        var factory = new RecordingConnectionFactory();
        var sqlConnector = new SqlConnector(
            connectorId: "default-sql",
            connectionFactory: factory,
            metadataRepository: new FakeMetadataRepository([meta]),
            options: options);

        var session = new ConnectorSessionContext(
            Principal: CreatePrincipal(),
            Tenant: new TenantId("tenant-1"),
            AccessDecision: decision,
            ProjectedColumns: ["Id"],
            Arguments: new Dictionary<string, object?>());
        session.Items["TableMetadata"] = meta;

        // The real connection path must be taken (and fail here); the synthetic demo-data fallback must not be used.
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await sqlConnector.RecordSource.ReadBatchAsync(ConnectorSplit.Default(), session);
        });

        factory.ConnectionString.ShouldBe("Server=db;Database=x;");
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
            DataSources = new SqlDataSourceOptions
            {
                Connections =
                {
                    ["sqlite_ds"] = new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = memConnStr }
                }
            },
            DuckDbOlap = new DuckDbOlapOptions { MaxStagedRowsPerTable = 25000 }
        });

        var sqlConnector = new SqlConnector(
            connectorId: "default-sql",
            connectionFactory: new SqlConnectionFactory(),
            metadataRepository: new FakeMetadataRepository([meta]),
            options: options);

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
    }

    private sealed class RecordingConnectionFactory : ISqlConnectionFactory
    {
        public string? ConnectionString { get; private set; }

        public Task<System.Data.Common.DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            ConnectionString = options.ConnectionString;
            throw new InvalidOperationException("recorded");
        }
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
