namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// #668 through the governed pipeline: the dbt table of database <c>finance</c> is catalogued as <c>finance.dbo.invoices</c> with
/// <c>SourceName = crmdb</c> (the connection key); <c>WebSql:DataSourceMappings:finance = crmdb</c> makes the logical name reach it.
/// </summary>
public sealed class WebSqlMappedConnectionTests
{
    private const string Tenant = "tenant_finance";
    private const string Secret = "crmdb_secret_key";

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER-1"),
            new Claim("tenant_id", Tenant),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete)
        ], "Test"));

    private static TableMetadata Table(string domain, string table, string sourceName) => new()
    {
        Identifier = new TableIdentifier(domain, "dbo", table),
        Table = new Table { TableName = table, SchemaName = "dbo", SourceType = "PostgreSql", SourceName = sourceName },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>()
    };

    private sealed class FakeMetadataRepo(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class EmptyConsentRepo : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AllowIdConsentResolution : IConsentResolutionService
    {
        public TableAccessDecision ResolveAccess(Sid userSid, IReadOnlySet<Sid> subjectGroupSids, IReadOnlySet<string> userRoles, TableIdentifier table, IReadOnlyList<Consent> activeConsents, DatabaseDialect dialect = DatabaseDialect.SqlServer, IReadOnlySet<Sid>? allUserSids = null, IReadOnlySet<string>? callerScopes = null) =>
            TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, null, hasUnconstrainedColumnAllow: false);
    }

    private sealed class RecordingConnectionFactory : ISqlConnectionFactory
    {
        public List<string> OpenedConnectionStrings { get; } = [];
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            OpenedConnectionStrings.Add(options.ConnectionString);
            return Task.FromResult<DbConnection>(new FakeDbConnection());
        }
    }

    private sealed class NoopSessionInitializer : IDbSessionContextInitializer
    {
        public Task<DbTransaction?> InitializeSessionAsync(DbConnection connection, DatabaseDialect dialect, TenantId tenantId, string? userSid = null, string? purpose = null, bool requireTransaction = true, CancellationToken ct = default) =>
            Task.FromResult<DbTransaction?>(null);
        public Task<DbTransaction?> InitializeSessionAsync(DbConnection connection, string? provider, TenantId tenantId, string? userSid = null, string? purpose = null, bool requireTransaction = true, CancellationToken ct = default) =>
            Task.FromResult<DbTransaction?>(null);
        public Task InitializeSessionAsync(DbConnection connection, DbTransaction? tx, DatabaseDialect dialect, TenantId tenantId, string? userSid = null, string? purpose = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed record Rig(IGovernedSqlExecutionService Service, RecordingConnectionFactory Factory);

    private static Rig Build(Dictionary<string, string> mappings, string[] connections, params TableMetadata[] tables) =>
        Build(mappings, connections, "PostgreSql", tables);

    private static Rig Build(Dictionary<string, string> mappings, string[] connections, string connectionProvider, params TableMetadata[] tables)
    {
        var repo = new FakeMetadataRepo(tables);
        var consents = new EmptyConsentRepo();
        var resolution = new AllowIdConsentResolution();

        var options = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "default" } };
        foreach (var (key, value) in mappings)
        {
            options.WebSql.DataSourceMappings[key] = value;
        }

        foreach (var name in connections)
        {
            options.DataSources.Connections[name] = new DataSourceConnectionOptions { Provider = connectionProvider, ConnectionString = $"Host=localhost;Database={name}" };
        }

        var factory = new RecordingConnectionFactory();
        var service = global::Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            Options.Create(options), global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo, policyFactory: GovernedTestSupport.PolicyFactory(repo), typedPolicies: GovernedTestSupport.TypedPolicies(),
            connectionFactory: factory, clientIpResolver: null, environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance, consentRepository: consents, secretProvider: null,
            sessionInitializer: new NoopSessionInitializer());
        return new Rig(service, factory);
    }

    private static Task<GovernedSqlResult> Query(Rig rig, string sql, string dataSource) =>
        rig.Service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql, null, dataSource), User(), new TenantId(Tenant));

    [Fact]
    public async Task Query_MappedLogicalName_ExecutesOnTheMappedConnection()
    {
        // a catalog entry does not record its connection: its source is the logical name, the connection follows from the mapping
        var rig = Build(new() { ["finance"] = "crmdb" }, ["crmdb"], Table("finance", "invoices", "finance"));

        await Query(rig, "SELECT id FROM finance.dbo.invoices", "finance");

        rig.Factory.OpenedConnectionStrings.ShouldBe(["Host=localhost;Database=crmdb"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_EntryOfOtherLogicalName_NotAdmittedThroughMapping()
    {
        var rig = Build(new() { ["finance"] = Secret }, [Secret], Table("finance", "invoices", "sales"));

        var ex = await Should.ThrowAsync<WebSqlTableDeniedException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "finance"));

        ex.Message.ShouldNotContain(Secret);
        rig.Factory.OpenedConnectionStrings.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Denial_Response_ContainsNoConnectionKey()
    {
        // the mapped connection has another dialect than the catalog entry
        var rig = Build(new() { ["finance"] = Secret }, [Secret], "Sqlite", Table("finance", "invoices", "finance"));

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "finance"));

        ex.Message.ShouldNotContain(Secret);
        rig.Factory.OpenedConnectionStrings.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TwoLogicalNames_OneConnection_EachSeesOnlyOwnDomainTables()
    {
        var rig = Build(new() { ["finance"] = "crmdb", ["sales"] = "crmdb" }, ["crmdb"],
            Table("finance", "invoices", "finance"), Table("sales", "orders", "sales"));

        await Query(rig, "SELECT id FROM finance.dbo.invoices", "finance");
        await Query(rig, "SELECT id FROM sales.dbo.orders", "sales");

        await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM sales.dbo.orders", "finance"));
        await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "sales"));
        await Should.ThrowAsync<WebSqlTableDeniedException>(() => Query(rig, "SELECT id FROM dbo.orders", "finance"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [Trait("Category", "Security")]
    public async Task Query_MappingWithoutConnection_Denied(string target)
    {
        var rig = Build(new() { ["finance"] = target }, ["finance"], Table("finance", "invoices", "finance"));

        await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "finance"));
        rig.Factory.OpenedConnectionStrings.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_ChainedMapping_Denied()
    {
        var rig = Build(new() { ["finance"] = "crmdb", ["crmdb"] = "other" }, ["crmdb", "other"], Table("finance", "invoices", "finance"));

        await Should.ThrowAsync<WebSqlPolicyException>(() => Query(rig, "SELECT id FROM finance.dbo.invoices", "finance"));
        rig.Factory.OpenedConnectionStrings.ShouldBeEmpty();
    }
}
