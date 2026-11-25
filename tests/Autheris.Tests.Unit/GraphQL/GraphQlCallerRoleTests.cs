namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Security;
using Autheris.Tests.Unit.Docs;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GraphQlCallerRoleTests
{
    private static ClaimsPrincipal MakePrincipal(
        string scheme = "Bearer",
        string? tenantId = null,
        params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-GQL-USER") };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    [Fact]
    public void HasGatewayRole_AllowsTenantPrefixedGovernanceAdmin_RejectsWildcardAndCrossTenant_KeepsClusterAdminGlobal()
    {
        var prefixedWithClaim = MakePrincipal(tenantId: "tenant-a", roles: ["tenant-a:GovernanceAdmin"]);
        GraphQlCallerPrincipal.HasGatewayRole(prefixedWithClaim, GatewayRole.GovernanceAdmin, "tenant-a").ShouldBeTrue();
        GraphQlCallerPrincipal.HasGatewayRole(prefixedWithClaim, GatewayRole.SecurityAuditor, "tenant-a").ShouldBeTrue();
        GraphQlCallerPrincipal.HasGatewayRole(prefixedWithClaim, GatewayRole.GovernanceAdmin, null).ShouldBeTrue();
        GraphQlCallerPrincipal.HasGatewayRole(prefixedWithClaim, GatewayRole.GovernanceAdmin, "tenant-b").ShouldBeFalse();

        var ctx = GraphQlCallerPrincipal.ResolveCallerSecurityContext(prefixedWithClaim, new TenantId("tenant-a"));
        ctx.IsGovernanceAdmin.ShouldBeTrue();
        ctx.IsClusterAdmin.ShouldBeFalse();

        var prefixedNoClaim = MakePrincipal(tenantId: null, roles: ["tenant-a:GovernanceAdmin"]);
        GraphQlCallerPrincipal.HasGatewayRole(prefixedNoClaim, GatewayRole.GovernanceAdmin, "tenant-a").ShouldBeTrue();
        GraphQlCallerPrincipal.HasGatewayRole(prefixedNoClaim, GatewayRole.GovernanceAdmin, "tenant-b").ShouldBeFalse();
        GraphQlCallerPrincipal.HasGatewayRole(prefixedNoClaim, GatewayRole.GovernanceAdmin, null).ShouldBeFalse();

        var wildcard = MakePrincipal(tenantId: "tenant-a", roles: ["*:GovernanceAdmin", "*:TenantAdmin", "*:Consumer"]);
        GraphQlCallerPrincipal.HasGatewayRole(wildcard, GatewayRole.GovernanceAdmin, "tenant-a").ShouldBeFalse();
        GraphQlCallerPrincipal.HasGatewayRole(wildcard, GatewayRole.TenantAdmin, "tenant-a").ShouldBeFalse();
        GraphQlCallerPrincipal.HasGatewayRole(wildcard, GatewayRole.Consumer, "tenant-a").ShouldBeFalse();

        var prefixedCluster = MakePrincipal(tenantId: "tenant-a", roles: ["tenant-a:ClusterAdmin"]);
        GraphQlCallerPrincipal.HasGatewayRole(prefixedCluster, GatewayRole.ClusterAdmin, "tenant-a").ShouldBeFalse();
        GraphQlCallerPrincipal.HasGatewayRole(prefixedCluster, GatewayRole.TenantAdmin, "tenant-a").ShouldBeFalse();

        var invalidTenant = MakePrincipal(tenantId: "bad<tenant>", roles: ["GovernanceAdmin", "tenant-a:GovernanceAdmin"]);
        GraphQlCallerPrincipal.HasGatewayRole(invalidTenant, GatewayRole.GovernanceAdmin, "tenant-a").ShouldBeFalse();
    }

    [Fact]
    public async Task McpDocTools_RecognizesTenantPrefixedGovernanceAdmin_OnlyForMatchingTenant()
    {
        var search = new InMemoryDocSearchService();
        var repo = new InMemoryDocumentRepository();
        var store = new GovernanceDocStore(repo, search, new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);

        var matchingSession = new McpSessionContext(
            "s-doc-1", "client-1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: "S-1-5-21-GOV", Roles: ["tenant-a:GovernanceAdmin"]);
        var foreignSession = matchingSession with { Roles = ["tenant-b:GovernanceAdmin"] };
        var wildcardSession = matchingSession with { Roles = ["*:GovernanceAdmin"] };

        const string args = """{"appId":"app","category":"Runbook","docId":"doc-1","title":"Title","content":"# Doc"}""";
        var matchingResult = await McpDocTools.ExecuteDocToolAsync(
            McpDocTools.DepositDocumentation, args, search, store, matchingSession, CancellationToken.None);
        var foreignResult = await McpDocTools.ExecuteDocToolAsync(
            McpDocTools.DepositDocumentation, args, search, store, foreignSession, CancellationToken.None);
        var wildcardResult = await McpDocTools.ExecuteDocToolAsync(
            McpDocTools.DepositDocumentation, args, search, store, wildcardSession, CancellationToken.None);

        matchingResult.ShouldContain("\"success\": true");
        foreignResult.ShouldNotContain("\"success\": true");
        wildcardResult.ShouldNotContain("\"success\": true");
    }

    [Fact]
    public async Task DbtHealthMiddleware_RecognizesTenantPrefixedGovernanceAdmin_OnlyForMatchingTenant()
    {
        var tableId = new TableIdentifier("tenant-a", "default", "orders");
        var breaker = new StubDbtHealthCircuitBreaker(tableId);
        var executor = await new ServiceCollection()
            .AddSingleton<IDbtHealthCircuitBreaker>(breaker)
            .AddGraphQLServer()
            .AddQueryType<DbtHealthDummyQuery>()
            .UseRequest<DbtHealthExecutionMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();

        var matching = MakePrincipal(tenantId: "tenant-a", roles: ["tenant-a:GovernanceAdmin"]);
        await using (var res = await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ orders }").SetGlobalState("ClaimsPrincipal", matching).Build()))
        {
            var json = res.ToJson();
            json.ShouldContain("TABLE_IN_QUARANTINE");
            json.ShouldContain("activeFailures");
        }

        foreach (var role in new[] { "tenant-b:GovernanceAdmin", "*:GovernanceAdmin" })
        {
            var denied = MakePrincipal(tenantId: "tenant-a", roles: [role]);
            await using var res = await executor.ExecuteAsync(OperationRequestBuilder.New().SetDocument("{ orders }").SetGlobalState("ClaimsPrincipal", denied).Build());
            var json = res.ToJson();
            json.ShouldNotContain("TABLE_IN_QUARANTINE", Case.Sensitive, role);
            json.ShouldNotContain("activeFailures", Case.Sensitive, role);
        }
    }

    [Fact]
    public async Task EffectiveAccessExplanationService_RecognizesTenantPrefixedGovernanceAdmin_OnlyForMatchingTenant()
    {
        var table = new TableIdentifier("tenant-a", "public", "orders");
        var metadata = new TableMetadata
        {
            Identifier = table,
            Table = new Table { SourceName = "tenant-a", SchemaName = "public", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var decision = TableAccessDecision.Allowed(
            table,
            new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear },
            rowFilterSql: "secret_col = 42",
            hasUnconstrainedColumnAllow: true);

        var service = new EffectiveAccessExplanationService(
            new StubTableRepo(metadata),
            new EmptyConsentRepo(),
            new FixedDecisionResolution(decision));

        var matching = MakePrincipal(tenantId: "tenant-a", roles: ["tenant-a:GovernanceAdmin"]);
        var matchingDto = await service.ExplainMyAccessAsync(matching, table);
        matchingDto.ActiveRowFilters.ShouldBe(["secret_col = 42"]);

        foreach (var role in new[] { "tenant-b:GovernanceAdmin", "*:GovernanceAdmin" })
        {
            var denied = MakePrincipal(tenantId: "tenant-a", roles: [role]);
            var deniedDto = await service.ExplainMyAccessAsync(denied, table);
            deniedDto.ActiveRowFilters.ShouldBe([EffectiveAccessExplanationService.RowFilterDescription], role);
        }
    }

    public sealed class DbtHealthDummyQuery
    {
        public string Orders() => "ok";
    }

    private sealed class StubDbtHealthCircuitBreaker(TableIdentifier quarantinedTable) : IDbtHealthCircuitBreaker
    {
        public ValueTask<DbtHealthState> GetTableHealthAsync(TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(table.Equals(quarantinedTable)
                ? new DbtHealthState(table, DbtModelHealthStatus.Quarantined, [new DbtTestFailure("not_null_orders_id", "orders", "id", "error", "secret failure detail", 5)], DateTimeOffset.UtcNow)
                : new DbtHealthState(table, DbtModelHealthStatus.Healthy, [], DateTimeOffset.UtcNow));

        public Task<DbtHealthState> SetTableHealthAsync(TableIdentifier table, DbtModelHealthStatus status, IReadOnlyList<DbtTestFailure> failures, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<TableIdentifier, DbtHealthState>> GetAllHealthStatesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<DbtRunResultsReport> RecordRunResultsAsync(Stream runResultsStream, TenantId tenant, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ResetTableHealthAsync(TableIdentifier table, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ResetAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubTableRepo(TableMetadata table) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier t, CancellationToken ct = default) =>
            Task.FromResult(t.Equals(table.Identifier) ? table : null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([table]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier t, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<long> FlushCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(1L);
    }

    private sealed class EmptyConsentRepo : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedDecisionResolution(TableAccessDecision decision) : IConsentResolutionService
    {
        public TableAccessDecision ResolveAccess(
            Sid userSid, IReadOnlySet<Sid> subjectGroupSids, IReadOnlySet<string> userRoles, TableIdentifier table,
            IReadOnlyList<Consent> activeConsents, DatabaseDialect dialect = DatabaseDialect.SqlServer,
            IReadOnlySet<Sid>? allUserSids = null, IReadOnlySet<string>? callerScopes = null) => decision;
    }
}
