namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Federation;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Mcp;
using Autheris.GraphQL.Subscriptions;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Lineage;
using HotChocolate;
using HotChocolate.AspNetCore.Subscriptions;
using HotChocolate.AspNetCore.Subscriptions.Protocols;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Regressionstests für die GraphQL-Befunde (Abschnitt 6) des Security Reviews vom 2026-10-02.
/// </summary>
public sealed class SecurityReview20261002GraphQLTests
{
    private sealed class IsolatedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static ClaimsPrincipal CreatePrincipal(string sid, string? tenant = null, string? clientId = null, params Claim[] extra)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new(ClaimTypes.NameIdentifier, sid)
        };
        if (tenant != null) claims.Add(new Claim("tenant_id", tenant));
        if (clientId != null) claims.Add(new Claim("client_id", clientId));
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    // ---------------------------------------------------------------- M-13

    private static async Task<ISchemaDefinition> BuildQuerySchemaAsync()
    {
        return await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .BuildSchemaAsync();
    }

    [Fact]
    public async Task M13_TablePayloadWithLargeFirst_IsCostedByRowCount()
    {
        var schema = await BuildQuerySchemaAsync();
        var rule = new QueryCostAnalyzerRule(maxAllowedCost: 500, maxResponseRows: 5000);
        var doc = Utf8GraphQLParser.Parse("query { table(domain: \"hr\", name: \"hr_table_1\", first: 5000) { jsonRows } }");

        var cost = rule.ComputeCost(doc, schema);

        // Früher ca. 8 Punkte, weil TableRecordPayload kein Listentyp ist.
        cost.ShouldBeGreaterThan(500);
    }

    [Fact]
    public async Task M13_SixtyTableAliases_ExceedCostBudgetAndRootFieldLimit()
    {
        var schema = await BuildQuerySchemaAsync();
        var sb = new StringBuilder("query {");
        for (int i = 0; i < 60; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $" a{i}: table(domain: \"hr\", name: \"hr_table_1\", first: 5000) {{ jsonRows }}");
        }
        sb.Append(" }");
        var doc = Utf8GraphQLParser.Parse(sb.ToString());

        var rule = new QueryCostAnalyzerRule(maxAllowedCost: 500, maxResponseRows: 5000);
        rule.ComputeCost(doc, schema).ShouldBeGreaterThan(500);
        QueryCostAnalyzerRule.CountMaxRootFields(doc).ShouldBe(60);
        QueryCostAnalyzerRule.CountMaxRootFields(doc, stopAfter: 10).ShouldBeGreaterThan(10);
    }

    [Fact]
    public void M13_RootAliasesHiddenInFragmentSpread_AreCounted()
    {
        var sb = new StringBuilder("query { ...Heavy } fragment Heavy on Query {");
        for (int i = 0; i < 12; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $" t{i}: table(domain: \"hr\", name: \"hr_table_1\") {{ tableName }}");
        }
        sb.Append(" }");
        var doc = Utf8GraphQLParser.Parse(sb.ToString());

        QueryCostAnalyzerRule.CountMaxRootFields(doc).ShouldBe(12);
    }

    [Fact]
    public async Task M13_VariableRowLimit_AssumesWorstCase()
    {
        var schema = await BuildQuerySchemaAsync();
        var rule = new QueryCostAnalyzerRule(maxAllowedCost: 500, maxResponseRows: 5000);
        var doc = Utf8GraphQLParser.Parse("query($n: Int!) { table(domain: \"hr\", name: \"hr_table_1\", first: $n) { tableName } }");

        rule.ComputeCost(doc, schema).ShouldBeGreaterThan(500);
    }

    [Fact]
    public async Task M13_VariableRowLimit_WithDefaultValue_UsesDefaultLimit()
    {
        var schema = await BuildQuerySchemaAsync();
        var rule = new QueryCostAnalyzerRule(maxAllowedCost: 500, maxResponseRows: 5000);
        var doc = Utf8GraphQLParser.Parse("query($n: Int = 10) { table(domain: \"hr\", name: \"hr_table_1\", first: $n) { tableName } }");

        var cost = rule.ComputeCost(doc, schema);
        cost.ShouldBeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task M13_DefaultTableQuery_StaysWithinDefaultBudget()
    {
        var schema = await BuildQuerySchemaAsync();
        var rule = new QueryCostAnalyzerRule(maxAllowedCost: 500, maxResponseRows: 5000);
        var doc = Utf8GraphQLParser.Parse("query { table(domain: \"finance\", name: \"finance_table_1\") { tableName totalCount jsonRows } }");

        var cost = rule.ComputeCost(doc, schema);

        cost.ShouldBeGreaterThanOrEqualTo(QueryCostAnalyzerRule.DefaultPayloadPageSize);
        cost.ShouldBeLessThanOrEqualTo(500);
        QueryCostAnalyzerRule.CountMaxRootFields(doc).ShouldBe(1);
    }

    // ---------------------------------------------------------------- M-14

    [Fact]
    public void M14_ResolveSessionExpiry_UsesExpClaim()
    {
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddMinutes(5);
        var principal = CreatePrincipal("S-1-5-21-WS-1", extra: [new Claim("exp", exp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))]);

        var expiry = WebSocketAuthInterceptor.ResolveSessionExpiry(principal, null, now, TimeSpan.FromHours(8));

        expiry.ToUnixTimeSeconds().ShouldBe(exp.ToUnixTimeSeconds());
    }

    [Fact]
    public void M14_ResolveSessionExpiry_UsesEarlierAuthenticationPropertiesExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var principal = CreatePrincipal("S-1-5-21-WS-2", extra: [new Claim("exp", now.AddHours(2).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))]);

        var expiry = WebSocketAuthInterceptor.ResolveSessionExpiry(principal, now.AddMinutes(10), now, TimeSpan.FromHours(8));

        expiry.ShouldBe(now.AddMinutes(10));
    }

    [Fact]
    public void M14_ResolveSessionExpiry_WithoutExpiry_IsCappedAtMaxLifetime()
    {
        var now = DateTimeOffset.UtcNow;
        var principal = CreatePrincipal("S-1-5-21-WS-3");

        var expiry = WebSocketAuthInterceptor.ResolveSessionExpiry(principal, null, now, TimeSpan.FromHours(1));

        expiry.ShouldBe(now.AddHours(1));
    }

    [Fact]
    public void M14_ExtractToken_FromJsonConnectionInitPayload()
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"authorization\":\"Bearer ws-token-1\",\"other\":42}");
        payload.ShouldNotBeNull();

        WebSocketAuthInterceptor.ExtractToken(payload).ShouldBe("ws-token-1");
    }

    private static (ISocketSession Session, IOperationMessagePayload Payload, DefaultHttpContext HttpContext) CreateSocketSession(string token)
    {
        var httpContext = new DefaultHttpContext();
        var session = Substitute.For<ISocketSession>();
        session.Connection.HttpContext.Returns(httpContext);

        using var doc = JsonDocument.Parse($"{{\"authorization\":\"Bearer {token}\"}}");
        var payload = Substitute.For<IOperationMessagePayload>();
        payload.Payload.Returns(doc.RootElement.Clone());

        return (session, payload, httpContext);
    }

    [Fact]
    public async Task M14_OnConnect_WithExpiredToken_IsRejected()
    {
        var (session, payload, _) = CreateSocketSession("expired-token");
        var expired = CreatePrincipal("S-1-5-21-WS-EXPIRED",
            extra: [new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))]);

        var validator = Substitute.For<ISocketTokenValidator>();
        validator.ValidateTokenAsync("expired-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool IsValid, ClaimsPrincipal? Principal)>((true, expired)));

        var interceptor = new WebSocketAuthInterceptor(NullLogger<WebSocketAuthInterceptor>.Instance, validator);

        var status = await interceptor.OnConnectAsync(session, payload);

        status.Accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task M14_OnConnect_WithValidToken_IsAcceptedAndExpiryIsTracked()
    {
        var (session, payload, httpContext) = CreateSocketSession("valid-token");
        var exp = DateTimeOffset.UtcNow.AddMinutes(30);
        var valid = CreatePrincipal("S-1-5-21-WS-VALID",
            extra: [new Claim("exp", exp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))]);

        var validator = Substitute.For<ISocketTokenValidator>();
        validator.ValidateTokenAsync("valid-token", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool IsValid, ClaimsPrincipal? Principal)>((true, valid)));

        var interceptor = new WebSocketAuthInterceptor(NullLogger<WebSocketAuthInterceptor>.Instance, validator);

        var status = await interceptor.OnConnectAsync(session, payload);

        status.Accepted.ShouldBeTrue();
        httpContext.User.ShouldBeSameAs(valid);
        httpContext.Items[WebSocketAuthInterceptor.SessionExpiresAtItemKey].ShouldBeOfType<DateTimeOffset>()
            .ToUnixTimeSeconds().ShouldBe(exp.ToUnixTimeSeconds());
    }

    // ---------------------------------------------------------------- M-15

    private static LineageImpactAnalyzerService CreateLineageService(
        IConsentRepository consentRepo,
        IDataOwnershipRepository ownershipRepo,
        IAuditLogRepository auditRepo,
        LineageGraphStore graphStore)
    {
        return new LineageImpactAnalyzerService(
            consentRepo,
            ownershipRepo,
            graphStore,
            auditRepo,
            Substitute.For<ITableMetadataRepository>(),
            NullLogger<LineageImpactAnalyzerService>.Instance);
    }

    private static IAuditLogRepository CreateAuditRepoWithReads(string rootId)
    {
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var now = DateTimeOffset.UtcNow;
        auditRepo.QueryAuditLogsAsync(
                targetTable: rootId,
                actorSid: null,
                since: Arg.Any<DateTimeOffset>(),
                limit: Arg.Any<int>(),
                tenantId: Arg.Any<TenantId?>(),
                ct: Arg.Any<CancellationToken>())
            .Returns(new List<AuditLogEntry>
            {
                new() { OccurredAt = now.AddDays(-1), ActorSid = new Sid("S-1-5-21-VICTIM-1"), TargetTable = rootId, Decision = "ALLOW", EventType = "TABLE_QUERY" },
                new() { OccurredAt = now.AddHours(-2), ActorSid = new Sid("S-1-5-21-VICTIM-2"), TargetTable = rootId, Decision = "ALLOW", EventType = "TABLE_QUERY" }
            });
        return auditRepo;
    }

    [Fact]
    public async Task M15_TableConsumers_RegularUser_DoesNotSeeActorSids()
    {
        var table = new TableIdentifier("hr", "dbo", "salaries");
        var graph = new LineageGraphStore();
        graph.UpdateGraph([new LineageNode(table.ToString(), "salaries", LineageNodeType.Table, [], "HR", "hr@corp.local")]);
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(table, Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = CreateLineageService(Substitute.For<IConsentRepository>(), ownership, CreateAuditRepoWithReads(table.ToString()), graph);

        var attacker = new CallerSecurityContext(new Sid("S-1-5-21-ATTACKER"), [], ["Analyst"], new TenantId("tenant-1"), false, false);
        var report = await sut.GetTableConsumersAsync(table, 3650, attacker);

        report.RuntimeConsumers.ShouldBeEmpty();
        report.ActiveReadersCount.ShouldBe(2); // nur aggregiert
    }

    [Theory]
    [InlineData("PrivacyAdmin", false)]
    [InlineData("GovernanceAdmin", false)]
    [InlineData("Analyst", true)]
    public async Task M15_TableConsumers_OwnerOrPrivilegedRole_SeesRuntimeConsumers(string role, bool isOwner)
    {
        var table = new TableIdentifier("hr", "dbo", "salaries");
        var graph = new LineageGraphStore();
        graph.UpdateGraph([new LineageNode(table.ToString(), "salaries", LineageNodeType.Table, [], "HR", "hr@corp.local")]);
        var caller = new CallerSecurityContext(new Sid("S-1-5-21-CALLER"), [], [role], new TenantId("tenant-1"), false, false);
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(table, caller.UserSid, Arg.Any<CancellationToken>()).Returns(isOwner);
        var sut = CreateLineageService(Substitute.For<IConsentRepository>(), ownership, CreateAuditRepoWithReads(table.ToString()), graph);

        var report = await sut.GetTableConsumersAsync(table, 30, caller);

        report.RuntimeConsumers.Count.ShouldBe(2);
    }

    private static Consent CreateConsent(Guid id, TableIdentifier table, TenantId tenant) => new()
    {
        Id = id,
        TableId = Guid.NewGuid(),
        TableIdentifier = table,
        TenantId = tenant,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-5-21-GRANTEE"),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1)
    };

    [Fact]
    public async Task M15_ConsentRevocationImpact_NonOwner_IsForbidden()
    {
        var table = new TableIdentifier("finance", "dbo", "salaries");
        var consentId = Guid.NewGuid();
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(CreateConsent(consentId, table, new TenantId("tenant-a")));
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(table, Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = CreateLineageService(consentRepo, ownership, Substitute.For<IAuditLogRepository>(), new LineageGraphStore());

        var caller = new CallerSecurityContext(new Sid("S-1-5-21-ANALYST"), [], ["Analyst"], new TenantId("tenant-a"), false, false);

        await Should.ThrowAsync<GatewayForbiddenException>(() =>
            sut.CalculateConsentRevocationImpactAsync(new TenantId("tenant-a"), consentId, caller));
    }

    [Fact]
    public async Task M15_ConsentRevocationImpact_LegacySingleTenantConsent_IsNotVisibleToOtherTenant()
    {
        var table = new TableIdentifier("finance", "dbo", "salaries");
        var consentId = Guid.NewGuid();
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(CreateConsent(consentId, table, TenantId.LegacySingleTenant));
        var sut = CreateLineageService(consentRepo, Substitute.For<IDataOwnershipRepository>(), Substitute.For<IAuditLogRepository>(), new LineageGraphStore());

        var caller = new CallerSecurityContext(new Sid("S-1-5-21-GOV"), [], ["GovernanceAdmin"], new TenantId("tenant-a"), true, false);

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            sut.CalculateConsentRevocationImpactAsync(new TenantId("tenant-a"), consentId, caller));
    }

    [Fact]
    public async Task M15_ConsentRevocationImpact_TableOwner_IsAllowed()
    {
        var table = new TableIdentifier("finance", "dbo", "salaries");
        var consentId = Guid.NewGuid();
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(CreateConsent(consentId, table, new TenantId("tenant-a")));
        var caller = new CallerSecurityContext(new Sid("S-1-5-21-OWNER"), [], ["Analyst"], new TenantId("tenant-a"), false, false);
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(table, caller.UserSid, Arg.Any<CancellationToken>()).Returns(true);
        var sut = CreateLineageService(consentRepo, ownership, Substitute.For<IAuditLogRepository>(), new LineageGraphStore());

        var report = await sut.CalculateConsentRevocationImpactAsync(new TenantId("tenant-a"), consentId, caller);

        report.ShouldNotBeNull();
    }

    // ---------------------------------------------------------------- M-16

    [Fact]
    public async Task M16_RandomUnknownApiKeys_FallBackToSameIpBucket()
    {
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance);

        var c1 = await resolver.ResolveAsync(null, Guid.NewGuid().ToString("N"), "203.0.113.7");
        var c2 = await resolver.ResolveAsync(null, Guid.NewGuid().ToString("N"), "203.0.113.7");

        c1.SubjectId.ShouldBe("anon_203.0.113.7");
        c2.SubjectId.ShouldBe(c1.SubjectId);
        c1.Tier.ShouldBe(ClientTier.Free);
    }

    [Fact]
    public async Task M16_AuthenticatedUserWithUnknownApiKey_UsesUserBucket()
    {
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance);
        var principal = CreatePrincipal("S-1-5-21-USER-A", tenant: "tenant-a", clientId: "shared-spa");

        var ctx = await resolver.ResolveAsync(principal, "random-unregistered-key", "203.0.113.7");

        ctx.SubjectId.ShouldStartWith("user:tenant-a:S-1-5-21-USER-A");
        ctx.SubjectId.ShouldNotStartWith("key_");
    }

    [Fact]
    public async Task M16_TwoUsersOfSameClientId_GetSeparateBuckets()
    {
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance);
        var alice = CreatePrincipal("S-1-5-21-ALICE", tenant: "tenant-a", clientId: "shared-spa");
        var bob = CreatePrincipal("S-1-5-21-BOB", tenant: "tenant-a", clientId: "shared-spa");

        var a = await resolver.ResolveAsync(alice, null, "10.0.0.1");
        var b = await resolver.ResolveAsync(bob, null, "10.0.0.1");

        a.SubjectId.ShouldNotBe(b.SubjectId);
        a.SubjectId.ShouldBe("user:tenant-a:S-1-5-21-ALICE:shared-spa");
    }

    [Fact]
    public async Task M16_SameSidInDifferentTenants_GetSeparateBuckets()
    {
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance);

        var a = await resolver.ResolveAsync(CreatePrincipal("S-1-5-21-SAME", tenant: "tenant-a"), null, null);
        var b = await resolver.ResolveAsync(CreatePrincipal("S-1-5-21-SAME", tenant: "tenant-b"), null, null);

        a.SubjectId.ShouldNotBe(b.SubjectId);
    }

    [Fact]
    public async Task M16_RegisteredApiKey_StillGrantsAssignedTier()
    {
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance);
        resolver.RegisterApiKey("registered-partner-key", ClientTier.Enterprise);

        var ctx = await resolver.ResolveAsync(null, "registered-partner-key", "203.0.113.7");

        ctx.Tier.ShouldBe(ClientTier.Enterprise);
        ctx.SubjectId.ShouldStartWith("key_");
    }

    // ---------------------------------------------------------------- M-17

    private static McpSessionContext CreateMcpSession() =>
        new("sess-m17", "svc-agent", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-AGENT-USER");

    private static GatewayMcpQueryExecutor CreateMcpExecutor(IGatewayExecutionService executionService) =>
        new(Substitute.For<IRequestExecutorProvider>(), executionService, NullLogger<GatewayMcpQueryExecutor>.Instance);

    private static void AssertStructuredError(string json, string expectedCode)
    {
        json.ShouldNotContain("Mustermann");
        json.ShouldNotContain("DE89 3704");
        json.ShouldNotContain("Diabetes");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("isError").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(expectedCode);
    }

    [Fact]
    public async Task M17_FastPathForbidden_ReturnsStructuredForbiddenErrorInsteadOfMockData()
    {
        var execution = Substitute.For<IGatewayExecutionService>();
        execution.ExecuteTableQueryAsync(
                Arg.Any<ClaimsPrincipal?>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(),
                Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)>(
                new GatewayForbiddenException()));

        var tool = new McpToolDefinition(
            "query_customers",
            "Customers",
            "{}",
            "query GetCustomers { customers { id } }",
            new TableIdentifier("finance", "dbo", "customers"));

        var json = await CreateMcpExecutor(execution).ExecuteOperationAsync(tool, "{}", CreateMcpSession());

        AssertStructuredError(json, McpErrorCodes.Forbidden);
    }

    [Fact]
    public async Task M17_GraphQLExecutionFailure_ReturnsStructuredErrorInsteadOfMockData()
    {
        var tool = new McpToolDefinition("custom_report", "Custom", "{}", "query { doesNotExist }");

        var json = await CreateMcpExecutor(Substitute.For<IGatewayExecutionService>()).ExecuteOperationAsync(tool, "{}", CreateMcpSession());

        AssertStructuredError(json, McpErrorCodes.ExecutionFailed);
    }

    [Fact]
    public async Task M17_ToolWithoutOperation_ReturnsNotAvailableError()
    {
        var tool = new McpToolDefinition("query_data_catalog", "Catalog", "{}", string.Empty);

        var json = await CreateMcpExecutor(Substitute.For<IGatewayExecutionService>()).ExecuteOperationAsync(tool, "{}", CreateMcpSession());

        AssertStructuredError(json, McpErrorCodes.NotAvailable);
    }

    [Fact]
    public async Task M17_SimulateQueryWithoutSimulator_DoesNotFakeApproval()
    {
        var tool = new McpToolDefinition("simulate_query", "Simulator", "{}", string.Empty);

        var json = await CreateMcpExecutor(Substitute.For<IGatewayExecutionService>())
            .ExecuteOperationAsync(tool, "{\"query\":\"{ table(domain: \\\"a\\\", name: \\\"b\\\") { tableName } }\"}", CreateMcpSession());

        json.ShouldNotContain("\"isAllowed\":true");
        AssertStructuredError(json, McpErrorCodes.NotAvailable);
    }

    // ---------------------------------------------------------------- Niedrig

    [Fact]
    public async Task Low_RequestTableAccess_UnknownTable_ReturnsNeutralForbidden()
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(null));
        var approvals = Substitute.For<IConsentApprovalRepository>();
        var accessor = new IsolatedHttpContextAccessor(new DefaultHttpContext { User = CreatePrincipal("S-1-5-21-ENUM") });

        var ex = await Should.ThrowAsync<GraphQLException>(async () =>
            await new Mutation().RequestTableAccessAsync("hr", "dbo", "secret_probe_table", "Business need for analysis", 7, null, metadata, approvals, accessor));

        ex.Errors.ShouldContain(e => e.Code == "FORBIDDEN");
        ex.Errors.ShouldNotContain(e => e.Code == "NOT_FOUND");
        ex.Errors.ShouldAllBe(e => !e.Message.Contains("secret_probe_table"));
    }

    [Fact]
    public void Low_FederationAliasMap_ResolvesNamedFragments()
    {
        var doc = Utf8GraphQLParser.Parse("query { customer { ...Pii } } fragment Pii on Customer { harmless: email }");

        var map = SubgraphResultMaskingMiddleware.ExtractAliasToFieldMap(doc);

        map.ShouldNotBeNull();
        // Alias map is keyed by response path (GAP-B PART-FED).
        map["customer.harmless"].ShouldBe("email");
    }
}
