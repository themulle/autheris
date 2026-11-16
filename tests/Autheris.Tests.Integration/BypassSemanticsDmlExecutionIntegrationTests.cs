namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>WebSQL DML execution, tenant isolation, rollback and affected-row limit verification against real PostgreSQL.</summary>
[Trait("Category", "Security")]
public sealed class BypassSemanticsDmlExecutionIntegrationTests
{
    private const string Tenant = "tenant_a";
    private const string WriterRole = "WebSqlWriter";

    private sealed class DmlPgFixture(PostgreSqlTestDatabase db, GovernedSqlExecutionService service, List<AuditLogEntry> audit) : IAsyncDisposable
    {
        public string ConnectionString => db.ConnectionString;
        public GovernedSqlExecutionService Service => service;
        public List<AuditLogEntry> AuditEntries => audit;

        public static async Task<DmlPgFixture?> CreateAsync(long maxAffectedRows = 1000)
        {
            var db = await PostgreSqlTestDatabase.CreateAsync();
            if (!db.IsAvailable)
            {
                await db.DisposeAsync();
                return null;
            }

            await using (var conn = new NpgsqlConnection(db.ConnectionString))
            {
                await conn.OpenAsync();
                await using var setup = conn.CreateCommand();
                setup.CommandText =
                    "CREATE TABLE public.orders (id INTEGER, amount DOUBLE PRECISION, tenant_id TEXT);" +
                    "INSERT INTO public.orders VALUES (1, 10, 'tenant_a'), (2, 20, 'tenant_a'), (3, 30, 'tenant_a'), (4, 40, 'tenant_a'), (5, 50, 'tenant_a');" +
                    "INSERT INTO public.orders VALUES (6, 60, 'tenant_b'), (7, 70, 'tenant_b');";
                await setup.ExecuteNonQueryAsync();
            }

            var gatewayOptions = new GatewayOptions
            {
                WebSql = new WebSqlOptions
                {
                    Enabled = true,
                    AllowDml = true,
                    MaxAffectedRows = maxAffectedRows,
                    DefaultMaxRows = 100,
                    MaxAllowedRows = 500
                },
                DataSources = new SqlDataSourceOptions
                {
                    Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["default"] = new DataSourceConnectionOptions
                        {
                            Provider = "PostgreSql",
                            ConnectionString = db.ConnectionString,
                            WriteConnectionString = db.ConnectionString,
                            DefaultSchema = "public"
                        }
                    }
                }
            };

            var orders = new TableMetadata
            {
                Identifier = new TableIdentifier("default", "public", "orders"),
                Table = new Table { TableName = "orders", SchemaName = "public", SourceName = string.Empty, SourceType = "PostgreSql", IsActive = true },
                Columns =
                [
                    new TableColumn { ColumnName = "id", DataType = "int" },
                    new TableColumn { ColumnName = "amount", DataType = "decimal" },
                    new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
                ]
            };

            var writeConsent = new Consent
            {
                TableIdentifier = orders.Identifier,
                TenantId = new TenantId(Tenant),
                GranteeType = GranteeType.User,
                GranteeSid = new Sid("S-1-5-21-DML-WRITER"),
                Effect = ConsentEffect.Allow,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                WriteRules =
                [
                    new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "amount", "tenant_id"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["id", "amount", "tenant_id"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows)
                ]
            };

            var options = Options.Create(gatewayOptions);
            var tableRepo = new SingleTableRepo(orders);
            var consentRepo = new SingleConsentRepo([writeConsent]);
            var consentResolution = new AllowAllConsentResolution();
            var auditRepo = new OutcomeAuditRepo();
            var catalog = new OrdersCatalogProvider(orders);
            var policyFactory = new GovernancePolicyFactory(catalog, tableRepo, options);
            var typedPolicies = new TypedPolicySources(
                new RowPolicyFactory(new PolicyPredicateRenderer()),
                new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(options)));

            var pdp = new AccessDecisionPoint(
                options,
                consentResolution,
                consentRepo,
                NullConsentCacheService.Instance,
                typedPolicies: typedPolicies,
                writeGrantEvaluator: new WriteGrantEvaluator(consentRepo, allowDeleteGrants: true));

            var rewriteDeps = new GovernedSqlRewriteDependencies(
                consentResolution, tableRepo, null, consentRepo, FastSqlEngine.Default, policyFactory, null,
                NullConsentCacheService.Instance, NullMandatoryRowFilterResolver.Instance, null, null, null, typedPolicies, null, pdp);
            var execDeps = new GovernedSqlExecutionDependencies(
                ConnectionFactory: new SqlConnectionFactory(new DevelopmentEnvironment()),
                TransportGate: AllowingTransportGate.Instance,
                AuditDigester: new Autheris.Application.Sql.Services.CompiledSqlAuditDigester(options, null, new DevelopmentEnvironment()));

            var service = new GovernedSqlExecutionService(
                options,
                new AuditWriter(auditRepo, NullLogger<AuditWriter>.Instance),
                PassThroughExecutionGuard.Instance,
                rewriteDeps,
                execDeps,
                logger: NullLogger<GovernedSqlExecutionService>.Instance);

            return new DmlPgFixture(db, service, auditRepo.Entries);
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        public ValueTask DisposeAsync() => db.DisposeAsync();
    }

    private static ClaimsPrincipal CreateWriter() =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-DML-WRITER"),
                new Claim("tenant_id", Tenant),
                new Claim(ClaimTypes.Role, WriterRole),
                new Claim("scp", "Sql.Write"),
                GroupResolutionClaims.CreateMarker(),
                GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
            ],
            "Test"));

    private static Task ExecuteAsync(GovernedSqlExecutionService service, string sql) =>
        service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest(sql, Transport: SqlTransports.WebSql),
            CreateWriter(),
            new TenantId(Tenant),
            (_, _) => Task.CompletedTask);

    [RequiresContainerRuntimeFact]
    public async Task DML_DeleteWithRealWhere_IsExecuted_AndAuditedWithoutSqlText()
    {
        await using var fixture = await DmlPgFixture.CreateAsync();
        if (fixture is null) return;

        await ExecuteAsync(fixture.Service, "DELETE FROM orders WHERE amount = 10");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(6);

        var entry = fixture.AuditEntries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("WEBSQL_DML_EXECUTED");
        entry.Decision.ShouldBe("ALLOW");
        entry.TargetTable.ShouldBe("orders");
        entry.DetailsJson.ShouldContain("\"affectedRows\":1");
        entry.DetailsJson.ShouldContain("\"statementType\":\"Delete\"");
        entry.DetailsJson.ShouldContain("sql.compiled_digest");
        entry.DetailsJson.ShouldContain("compiler_version");
        entry.DetailsJson.ShouldNotContain("DELETE FROM");
        entry.DetailsJson.ShouldNotContain("amount = 10");
    }

    [RequiresContainerRuntimeFact]
    public async Task DML_UpdateIsTenantScoped_AndDoesNotTouchForeignRows()
    {
        await using var fixture = await DmlPgFixture.CreateAsync();
        if (fixture is null) return;

        await ExecuteAsync(fixture.Service, "UPDATE orders SET amount = 0 WHERE id > 0");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0 AND tenant_id = 'tenant_a'")).ShouldBe(5);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0 AND tenant_id = 'tenant_b'")).ShouldBe(0);
        fixture.AuditEntries.ShouldHaveSingleItem().DetailsJson.ShouldContain("\"affectedRows\":5");
    }

    [RequiresContainerRuntimeFact]
    public async Task DML_MaxAffectedRowsExceeded_RollsBack_AndIsAudited()
    {
        await using var fixture = await DmlPgFixture.CreateAsync(maxAffectedRows: 2);
        if (fixture is null) return;

        var ex = await Should.ThrowAsync<DmlAffectedRowsLimitException>(() => ExecuteAsync(fixture.Service, "UPDATE orders SET amount = 0 WHERE id > 0"));

        ex.Code.ShouldBe("DML_AFFECTED_ROWS_LIMIT");
        ex.Message.ShouldNotContain("5");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0")).ShouldBe(0);

        var entry = fixture.AuditEntries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("WEBSQL_DML_REJECTED");
        entry.Decision.ShouldBe("DENY");
        entry.DetailsJson.ShouldContain("DML_AFFECTED_ROWS_LIMIT");
    }

    [RequiresContainerRuntimeFact]
    public async Task DML_MaxAffectedRowsWithinLimit_IsCommitted()
    {
        await using var fixture = await DmlPgFixture.CreateAsync(maxAffectedRows: 2);
        if (fixture is null) return;

        await ExecuteAsync(fixture.Service, "DELETE FROM orders WHERE id <= 2");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(5);
        fixture.AuditEntries.ShouldHaveSingleItem().EventType.ShouldBe("WEBSQL_DML_EXECUTED");
    }

    [RequiresContainerRuntimeFact]
    public async Task DML_MaxAffectedRowsZero_IsUnlimited()
    {
        await using var fixture = await DmlPgFixture.CreateAsync(maxAffectedRows: 0);
        if (fixture is null) return;

        await ExecuteAsync(fixture.Service, "UPDATE orders SET amount = 0 WHERE id > 0");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0")).ShouldBe(5);
    }

    private sealed class SingleTableRepo(TableMetadata table) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier id, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(id.Equals(table.Identifier) ? table : null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([table]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier t, IReadOnlyList<string> pk, IReadOnlyList<TableIndexDefinition> idx,
            IReadOnlyList<KeyColumnDirection>? dir = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class SingleConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> s, TableIdentifier t, DateTimeOffset at, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> s, TableIdentifier t, DateTimeOffset at, TenantId? tid, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> s, IEnumerable<string>? r = null, DateTimeOffset? at = null, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> s, IEnumerable<string>? r, DateTimeOffset? at, TenantId? tid, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid id, Sid by, string reason, ConsentRevocationAuthority auth, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid id, DateTimeOffset at, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid id, Guid reqId, Sid by, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => Task.FromResult(new ConsentPage(consents, null));
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revoke, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority auth, IReadOnlyList<AuditLogEntry>? audit = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AllowAllConsentResolution : IConsentResolutionService
    {
        public TableAccessDecision ResolveAccess(
            Sid userSid, IReadOnlySet<Sid> subjectGroupSids, IReadOnlySet<string> userRoles, TableIdentifier table,
            IReadOnlyList<Consent> activeConsents, DatabaseDialect dialect = DatabaseDialect.SqlServer,
            IReadOnlySet<Sid>? allUserSids = null, IReadOnlySet<string>? callerScopes = null) =>
            TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
    }

    private sealed class OutcomeAuditRepo : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (entry.EventType != AuditEventTypes.WebSqlDmlIntent)
            {
                Entries.Add(entry);
            }

            return Task.CompletedTask;
        }
        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction tx, CancellationToken ct = default) => RecordAuditEventAsync(entry, ct);
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tid = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? t = null, Sid? a = null, DateTimeOffset? s = null, int l = 1000, TenantId? tid = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>(Entries);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class OrdersCatalogProvider(TableMetadata table) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
        {
            var cols = table.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray();
            return new(new InMemoryTableCatalog([new TableCatalogEntry(new TableIdentity("public", "orders"), cols, "tenant_id", 1)], "public"));
        }
    }

    private sealed class AllowingTransportGate : IDataSourceTransportGate
    {
        public static readonly AllowingTransportGate Instance = new();
        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.WebSql;
        public IReadOnlyList<string> CheckedSources => [];
        public Task EnsureAsync(string? s, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureAllAsync(IEnumerable<string?> s, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureCatalogSourcesAsync(IEnumerable<string?> s, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureCatalogSourceAsync(string? s, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsAllowed(string? s) => true;
    }
}
