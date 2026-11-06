namespace Autheris.Tests.Integration.Olap;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.DataSources;
using Autheris.Application.Federation;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Options.TableCache;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Caching.TableCache;
using Autheris.Infrastructure.Connectors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

[Trait("Category", "Security")]
public sealed class DuckDbCoreSecurityTests
{
    private static readonly TableIdentifier OrdersTable = new("sales", "public", "orders");

    private static readonly TableMetadata OrdersMeta = new()
    {
        Identifier = OrdersTable,
        Table = new Table
        {
            TableName = "orders",
            SchemaName = "public",
            SourceName = "sales",
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
            new TableColumn { ColumnName = "note", DataType = "varchar" }
        ]
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task Olap_and_table_cache_are_tenant_scoped_and_parameterized()
    {
        var tempCacheDir = Path.Combine(Path.GetTempPath(), "autheris_duckdb_core_sec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCacheDir);

        try
        {
            var olapOptions = new DuckDbOlapOptions
            {
                Enabled = true,
                MaxMemory = "256MB",
                MaxStagedRowsPerTable = 100,
                QueryTimeoutSeconds = 10,
                MaxThreads = 1
            };
            using var engine = new DuckDbOlapEngine(olapOptions, NullLogger<DuckDbOlapEngine>.Instance);

            var connectorRows = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["id"] = 1, ["tenant_id"] = "tenant-a", ["note"] = "TENANT_A_SECRET" },
                new Dictionary<string, object?> { ["id"] = 2, ["tenant_id"] = "tenant-a", ["note"] = "a' OR 1=1 --" },
                new Dictionary<string, object?> { ["id"] = 3, ["tenant_id"] = "tenant-b", ["note"] = "TENANT_B_ROW" }
            };

            var registry = new InMemoryConnectorRegistry();
            registry.RegisterConnector("sales", new InMemoryTestConnector("sales", connectorRows));

            var gatewayOptions = Options.Create(new GatewayOptions
            {
                WebSql = new WebSqlOptions
                {
                    Enabled = true,
                    AllowedDataSources = ["sales"],
                    TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["tenant-a"] = ["sales"]
                    }
                },
                DuckDbOlap = olapOptions
            });

            var auditWriter = new RecordingAuditWriter();
            var stagingService = new OlapStagingService(
                engine,
                new SingleTableMetadataRepository(OrdersMeta),
                registry,
                new TenantScopedAccessResolver(),
                new ColumnMaskingProvider(),
                gatewayOptions,
                auditWriter,
                NullLogger<OlapStagingService>.Instance,
                new FederationGate(gatewayOptions),
                FastSqlEngine.Default,
                new OlapRequestLimiter(gatewayOptions),
                AllowOlapTransportGate.Instance);

            // 1. Tenant A stages and queries its own rows through OlapStagingService + DuckDbOlapEngine.
            var userA = CreatePrincipal("tenant-a", "S-1-5-21-A");
            var callerA = CreateCaller("tenant-a", "S-1-5-21-A");
            var outcomeA = await stagingService.ExecuteAsync(
                userA,
                callerA,
                new OlapQueryCommand("SELECT id, note FROM orders ORDER BY id", ["sales.public.orders"], null),
                isDevelopment: false,
                traceId: "trace-tenant-a",
                CancellationToken.None);

            outcomeA.StatusCode.ShouldBe(StatusCodes.Status200OK);
            outcomeA.Result.ShouldNotBeNull();
            outcomeA.Result.Rows.Count.ShouldBe(2);
            outcomeA.Result.Rows[0][1]?.ToString().ShouldBe("TENANT_A_SECRET");

            // 2. Tenant B sees only its own row (cannot read Tenant A's staged session), and Tenant C is denied (403).
            var userB = CreatePrincipal("tenant-b", "S-1-5-21-B");
            var callerB = CreateCaller("tenant-b", "S-1-5-21-B");
            var outcomeB = await stagingService.ExecuteAsync(
                userB,
                callerB,
                new OlapQueryCommand("SELECT id, note FROM orders", ["sales.public.orders"], null),
                isDevelopment: false,
                traceId: "trace-tenant-b",
                CancellationToken.None);

            outcomeB.StatusCode.ShouldBe(StatusCodes.Status200OK);
            outcomeB.Result.ShouldNotBeNull();
            outcomeB.Result.Rows.Count.ShouldBe(1);
            outcomeB.Result.Rows[0][1]?.ToString().ShouldBe("TENANT_B_ROW");

            var userC = CreatePrincipal("tenant-c", "S-1-5-21-C");
            var callerC = CreateCaller("tenant-c", "S-1-5-21-C");
            var outcomeC = await stagingService.ExecuteAsync(
                userC,
                callerC,
                new OlapQueryCommand("SELECT id, note FROM orders", ["sales.public.orders"], null),
                isDevelopment: false,
                traceId: "trace-tenant-c",
                CancellationToken.None);

            outcomeC.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
            outcomeC.Result.ShouldBeNull();

            // 3. Parameterized execution on DuckDbOlapEngine treats injection payloads strictly as bound data values.
            var stagedForParamCheck = new OlapTableSource(
                OrdersTable,
                connectorRows.GetRange(0, 2),
                OrdersMeta,
                StagingTableName: "s0");
            var boundQuery = new GeneratedOlapQuery(
                "SELECT \"id\" FROM \"s0\" WHERE \"note\" = $1",
                [new BoundParameter("$1", "1", 0, "a' OR 1=1 --", SqlParameterType.String, ParameterOrigin.QueryLiteral)]);

            var paramResult = await engine.ExecuteGeneratedAsync(boundQuery, [stagedForParamCheck]);
            paramResult.Rows.Count.ShouldBe(1);
            Convert.ToInt32(paramResult.Rows[0][0]).ShouldBe(2);

            // 4. DuckDbPersistentTableCacheStore stages encrypted rows via parameterized batch inserts and enforces tenant filtering on reader connections.
            var cacheOptions = new TableCacheOptions
            {
                Enabled = true,
                PersistentDirectory = tempCacheDir,
                DefaultMemoryLimitMb = 128,
                DefaultDiskLimitMb = 50,
                Encryption = new TableCacheEncryptionOptions
                {
                    Enabled = true,
                    KeyVaultKeyName = "test-kek"
                }
            };
            var masterKey = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
            using var cacheStore = new DuckDbPersistentTableCacheStore(Options.Create(cacheOptions), customEncryptionKey: masterKey);

            async IAsyncEnumerable<IReadOnlyList<object?>> CacheRows()
            {
                yield return new object?[] { "1", "tenant-a", "TENANT_A_SECRET" };
                yield return new object?[] { "2", "tenant-a", "a' OR 1=1 --" };
                await Task.CompletedTask;
            }

            var shadow = await cacheStore.StageShadowGenerationAsync(OrdersTable, 1L, ["id", "tenant_id", "note"], CacheRows());
            var cacheMeta = await cacheStore.SwapActiveGenerationAsync(OrdersTable, 1L, shadow, rowCount: 2, sizeInBytes: 512, expiresAt: null);

            var readerConn = await cacheStore.LeaseReaderConnectionAsync(OrdersTable);
            try
            {
                using var tenantBCmd = readerConn.CreateCommand();
                tenantBCmd.CommandText = $"SELECT COUNT(*) FROM \"{cacheMeta.ActiveViewName}\" WHERE \"tenant_id\" = $tenant AND \"note\" = $payload;";
                var pTenant = tenantBCmd.CreateParameter();
                pTenant.ParameterName = "tenant";
                pTenant.Value = "tenant-b";
                tenantBCmd.Parameters.Add(pTenant);

                var pPayload = tenantBCmd.CreateParameter();
                pPayload.ParameterName = "payload";
                pPayload.Value = "a' OR 1=1 --";
                tenantBCmd.Parameters.Add(pPayload);

                var tenantBCount = Convert.ToInt64(await tenantBCmd.ExecuteScalarAsync());
                tenantBCount.ShouldBe(0L);

                using var tenantACmd = readerConn.CreateCommand();
                tenantACmd.CommandText = $"SELECT \"id\" FROM \"{cacheMeta.ActiveViewName}\" WHERE \"tenant_id\" = $tenant AND \"note\" = $payload;";
                var pTenantA = tenantACmd.CreateParameter();
                pTenantA.ParameterName = "tenant";
                pTenantA.Value = "tenant-a";
                tenantACmd.Parameters.Add(pTenantA);

                var pPayloadA = tenantACmd.CreateParameter();
                pPayloadA.ParameterName = "payload";
                pPayloadA.Value = "a' OR 1=1 --";
                tenantACmd.Parameters.Add(pPayloadA);

                var matchedId = (await tenantACmd.ExecuteScalarAsync())?.ToString();
                matchedId.ShouldBe("2");
            }
            finally
            {
                cacheStore.ReturnReaderConnection(OrdersTable, readerConn);
            }
        }
        finally
        {
            if (Directory.Exists(tempCacheDir))
            {
                try { Directory.Delete(tempCacheDir, recursive: true); } catch { }
            }
        }
    }

    private static ClaimsPrincipal CreatePrincipal(string tenantId, string userSid) =>
        new(new ClaimsIdentity(
        [
            new Claim("tenant_id", tenantId),
            new Claim("user_sid", userSid)
        ], "Bearer"));

    private static SecurityPrincipalContext CreateCaller(string tenantId, string userSid) => new()
    {
        UserSid = new Sid(userSid),
        TenantId = new TenantId(tenantId),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer"
    };

    private sealed class SingleTableMetadataRepository(TableMetadata metadata) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(table.Equals(metadata.Identifier) ? metadata : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([metadata]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata updated, CancellationToken ct = default) =>
            Task.FromResult(updated);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class TenantScopedAccessResolver : ICrossDomainAccessResolver
    {
        public Task<TableAccessDecision> ResolveAccessAsync(
            ClaimsPrincipal principal,
            TableIdentifier table,
            TableMetadata metadata,
            TenantId? tenant,
            CancellationToken ct = default)
        {
            if (tenant is not { } tenantId || string.IsNullOrWhiteSpace(tenantId.Value))
            {
                return Task.FromResult(TableAccessDecision.Denied(table, "Missing tenant."));
            }

            if (string.Equals(tenantId.Value, "tenant-c", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(TableAccessDecision.Denied(table, "Tenant not authorized."));
            }

            var escapedTenant = tenantId.Value.Replace("'", "''");
            return Task.FromResult(TableAccessDecision.Allowed(
                table,
                new Dictionary<string, ColumnAccessLevel>(),
                rowFilterSql: $"tenant_id = '{escapedTenant}'",
                hasUnconstrainedColumnAllow: true));
        }
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowOlapTransportGate : IDataSourceTransportGate
    {
        public static AllowOlapTransportGate Instance { get; } = new();

        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.Rest;

        public IReadOnlyList<string> CheckedSources => [];

        public Task EnsureAsync(string? dataSourceName, CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default) => Task.CompletedTask;

        public Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default) => Task.CompletedTask;

        public bool IsAllowed(string? dataSourceName) => true;
    }

    private sealed class InMemoryTestConnector(
        string connectorId,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IAutherisConnector, IConnectorMetadata, IConnectorSplitManager, IConnectorRecordSource
    {
        public string ConnectorId => connectorId;
        public string ConnectorType => "PostgreSql";
        public ConnectorCapabilities Capabilities { get; } = new(ConnectorFeatures.None);
        public IConnectorMetadata Metadata => this;
        public IConnectorSplitManager SplitManager => this;
        public IConnectorRecordSource RecordSource => this;

        public Task<IReadOnlyList<string>> ListSchemasAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["public"]);

        public Task<IReadOnlyList<TableIdentifier>> ListTablesAsync(string? schema = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableIdentifier>>([OrdersTable]);

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(OrdersMeta);

        public Task<IReadOnlyList<ConnectorSplit>> GetSplitsAsync(TableMetadata table, ConnectorSessionContext session, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConnectorSplit>>([new ConnectorSplit("split-1", new Dictionary<string, object?>())]);

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadSplitAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                yield return row;
            }
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadBatchAsync(
            ConnectorSplit split,
            ConnectorSessionContext session,
            CancellationToken ct = default) =>
            Task.FromResult(rows);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
