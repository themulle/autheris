namespace Autheris.Tests.Integration.Sql;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

internal sealed class PgListCapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(formatter(state, exception) + (exception is null ? string.Empty : " | " + exception));
}

/// <summary>
/// PostgreSQL-backed governed pipeline fixture for list parameter and bulk keyset planned-read integration tests.
/// Uses concrete in-memory collaborators (zero mocks).
/// </summary>
internal sealed class ClientListPgFixture : IAsyncDisposable
{
    public const string TenantA = "tenant_a";
    public const string TenantB = "tenant_b";
    public const string WriterRole = "WebSqlWriter";

    private readonly PostgreSqlTestDatabase _db;
    private readonly List<AuditLogEntry> _entries;

    private ClientListPgFixture(PostgreSqlTestDatabase db, GovernedSqlExecutionService service, List<AuditLogEntry> entries)
    {
        _db = db;
        _entries = entries;
        ConnectionString = db.ConnectionString;
        Service = service;
    }

    public string ConnectionString { get; }

    public GovernedSqlExecutionService Service { get; }

    public IReadOnlyList<AuditLogEntry> Audit => _entries;

    public static async Task<ClientListPgFixture?> CreateAsync(
        int? maxListValues = null,
        int? maxLists = null,
        PgListCapturingLogger<GovernedSqlExecutionService>? logger = null)
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
                "CREATE TABLE public.orders (id INTEGER, status TEXT, amount DOUBLE PRECISION, tenant_id TEXT);" +
                "INSERT INTO public.orders VALUES (1, 'new', 10, 'tenant_a'), (2, 'new', 20, 'tenant_a'), (3, 'paid', 30, 'tenant_a'), (4, 'paid', 40, 'tenant_a'), (5, 'void', 50, 'tenant_a');" +
                "INSERT INTO public.orders VALUES (6, 'new', 60, 'tenant_b'), (7, 'paid', 70, 'tenant_b');";
            await setup.ExecuteNonQueryAsync();
        }

        var gatewayOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                MaxListParameterValues = maxListValues ?? 1000,
                MaxListParameters = maxLists ?? 8,
                Enabled = true,
                AllowDml = true,
                DefaultMaxRows = 2000,
                MaxAllowedRows = 5000,
                MaxAffectedRows = 1000
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
            Table = new Table
            {
                TableName = "orders",
                SchemaName = "public",
                SourceName = string.Empty,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "status", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
            ]
        };

        var writeConsent = new Consent
        {
            TableIdentifier = orders.Identifier,
            TenantId = new TenantId(TenantA),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-LIST-USER"),
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            WriteRules =
            [
                new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "status", "amount", "tenant_id"], WriteRowScope.AllRows),
                new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["id", "status", "amount", "tenant_id"], WriteRowScope.AllRows),
                new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows)
            ]
        };

        var options = Options.Create(gatewayOptions);
        var tableRepo = new SingleTableRepo(orders);
        var consentRepo = new SingleConsentRepo([writeConsent]);
        var consentResolution = new AllowAllConsentResolution();
        var auditRepo = new RecordingAuditRepo();
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
            consentResolution,
            tableRepo,
            null,
            consentRepo,
            FastSqlEngine.Default,
            policyFactory,
            null,
            NullConsentCacheService.Instance,
            NullMandatoryRowFilterResolver.Instance,
            null,
            null,
            null,
            typedPolicies,
            null,
            pdp);
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
            logger: (ILogger<GovernedSqlExecutionService>?)logger ?? NullLogger<GovernedSqlExecutionService>.Instance);

        return new ClientListPgFixture(db, service, auditRepo.Entries);
    }

    public static ClaimsPrincipal User(string tenant = TenantA, bool writer = true) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LIST-USER"),
                new Claim("tenant_id", tenant),
                new Claim(ClaimTypes.Role, writer ? WriterRole : "Reader"),
                new Claim("scp", "Sql.Write"),
                GroupResolutionClaims.CreateMarker(),
                GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
            ],
            "Test"));

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static GovernedSqlQueryRequest Request(string sql, IReadOnlyDictionary<string, object?>? parameters, string? transport = SqlTransports.WebSql, bool planned = false) =>
        new(sql, parameters, Transport: transport, PlannedTableRead: planned);

    public async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    public async Task<GovernedSqlResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, string tenant = TenantA, string? transport = SqlTransports.WebSql, bool planned = false) =>
        await Service.ExecuteQueryBufferedAsync(Request(sql, parameters, transport, planned), User(tenant), new TenantId(tenant));

    public async Task<long[]> IdsAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, string tenant = TenantA) =>
        (await QueryAsync(sql, parameters, tenant)).Rows.Select(r => Convert.ToInt64(r["id"], CultureInfo.InvariantCulture)).ToArray();

    public Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, string tenant = TenantA, string? transport = SqlTransports.WebSql) =>
        Service.ExecuteGovernedQueryAsync(Request(sql, parameters, transport), User(tenant), new TenantId(tenant), (_, _) => Task.CompletedTask);

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private sealed class SingleTableRepo(TableMetadata table) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier id, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(id.Equals(table.Identifier) ? table : null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([table]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier t, IReadOnlyList<string> pk, IReadOnlyList<TableIndexDefinition> idx,
            IReadOnlyList<KeyColumnDirection>? dir = null, CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class SingleConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> s, TableIdentifier t, DateTimeOffset at, TenantId? tid, CancellationToken ct = default) => Task.FromResult(consents);
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

    private sealed class RecordingAuditRepo : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction tx, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
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
