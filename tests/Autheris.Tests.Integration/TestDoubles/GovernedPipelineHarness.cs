namespace Autheris.Tests.Integration.TestDoubles;

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


/// <summary>
/// Builds the real governed WebSQL pipeline (compile, policy decision point, governed executor) over one data source and one table so the
/// container suites can run governed reads and writes under a database login of their choice. Doubles are the minimum that keeps the
/// decision, the compiler and the executor real: a single-table repository, a fixed consent list and an audit recorder.
/// </summary>
internal static class GovernedPipelineHarness
{
    public const string Tenant = "tenant_a";
    public const string CallerSid = "S-1-5-21-DML-WRITER";

    public static ClaimsPrincipal Caller(string tenant = Tenant) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, CallerSid),
                new Claim("tenant_id", tenant),
                new Claim(ClaimTypes.Role, "WebSqlWriter"),
                new Claim("scp", "Sql.Write"),
                GroupResolutionClaims.CreateMarker(),
                GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
            ],
            "Test"));

    public static TableMetadata Orders(string provider, string schema, string table) => new()
    {
        Identifier = new TableIdentifier("default", schema, table),
        Table = new Table { TableName = table, SchemaName = schema, SourceName = string.Empty, SourceType = provider, IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    public static Consent WriteConsent(TableMetadata orders, params WriteRule[] rules) => new()
    {
        TableIdentifier = orders.Identifier,
        TenantId = new TenantId(Tenant),
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(CallerSid),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        WriteRules = [.. rules]
    };

    public static GovernedSqlExecutionService Create(
        string provider, string readConnection, string? writeConnection, string schema, TableMetadata orders, IReadOnlyList<Consent> consents,
        List<AuditLogEntry> audit, bool allowDml)
    {
        var gatewayOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = allowDml, MaxAffectedRows = 1000, DefaultMaxRows = 100, MaxAllowedRows = 500 },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions
                    {
                        Provider = provider,
                        ConnectionString = readConnection,
                        WriteConnectionString = writeConnection,
                        DefaultSchema = schema
                    }
                }
            }
        };

        var options = Options.Create(gatewayOptions);
        var tableRepo = new SingleTableRepo(orders);
        var consentRepo = new SingleConsentRepo(consents);
        var consentResolution = new AllowAllConsentResolution();
        var auditRepo = new OutcomeAuditRepo(audit);
        var catalog = new OrdersCatalogProvider(orders, schema);
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

        return new GovernedSqlExecutionService(
            options,
            new AuditWriter(auditRepo, NullLogger<AuditWriter>.Instance),
            PassThroughExecutionGuard.Instance,
            rewriteDeps,
            execDeps,
            logger: NullLogger<GovernedSqlExecutionService>.Instance);
    }

    public static Task ExecuteAsync(GovernedSqlExecutionService service, string sql) =>
        service.ExecuteGovernedQueryAsync(
            new GovernedSqlQueryRequest(sql, Transport: SqlTransports.WebSql), Caller(), new TenantId(Tenant), (_, _) => Task.CompletedTask);

    public static Task<GovernedSqlResult> QueryAsync(GovernedSqlExecutionService service, string sql) =>
        service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql, Transport: SqlTransports.WebSql), Caller(), new TenantId(Tenant));

    internal sealed class SingleTableRepo(TableMetadata table) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier id, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(id.Equals(table.Identifier) ? table : null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([table]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier t, IReadOnlyList<string> pk, IReadOnlyList<TableIndexDefinition> idx,
            IReadOnlyList<KeyColumnDirection>? dir = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    internal sealed class SingleConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
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

    internal sealed class AllowAllConsentResolution : IConsentResolutionService
    {
        public TableAccessDecision ResolveAccess(
            Sid userSid, IReadOnlySet<Sid> subjectGroupSids, IReadOnlySet<string> userRoles, TableIdentifier table,
            IReadOnlyList<Consent> activeConsents, DatabaseDialect dialect = DatabaseDialect.SqlServer,
            IReadOnlySet<Sid>? allUserSids = null, IReadOnlySet<string>? callerScopes = null) =>
            TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true);
    }

    internal sealed class OutcomeAuditRepo(List<AuditLogEntry> entries) : IAuditLogRepository
    {
        public List<AuditLogEntry> Entries { get; } = entries;
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

    internal sealed class OrdersCatalogProvider(TableMetadata table, string schema) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default)
        {
            var cols = table.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray();
            return new(new InMemoryTableCatalog([new TableCatalogEntry(new TableIdentity(schema, table.Table.TableName), cols, "tenant_id", 1)], schema));
        }
    }

    internal sealed class AllowingTransportGate : IDataSourceTransportGate
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
