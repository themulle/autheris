namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Federation.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Federation;
using Autheris.GraphQL.Mcp;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Security;
using HotChocolate.AspNetCore.Subscriptions;
using HotChocolate.AspNetCore.Subscriptions.Protocols;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

/// <summary>
/// Runde 3 / GAP-B: OpenSchema kein MCP-Bypass (GAP06), MCP isError (GAP07), Token-Entzug bei
/// WebSocket-Subscriptions (PART-WS, M-14 Rest) und pfadbezogene Federation-Alias-Auflösung (PART-FED).
/// </summary>
public sealed class IntegrationGapBTests
{
    // ---------------------------------------------------------------- helpers

    private sealed class DenyingPolicyService : IPolicyEnforcementService
    {
        public int Calls { get; private set; }

        public ValueTask<TableAccessDecision> EvaluatePolicyAsync(SecurityEvaluationContext context, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(TableAccessDecision.Denied(context.TargetTable, "denied by test policy"));
        }

        public bool HasPolicies(TenantId tenant) => true;
        public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default) => Task.CompletedTask;
        public void LoadPolicyFromText(TenantId tenant, string policyText) { }
        public void LoadPolicyFromFile(TenantId tenant, string filePath, bool watchFile = false) { }
    }

    private sealed class RecordingLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingTimeProvider : TimeProvider
    {
        public TimeSpan? Period { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Period = period;
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    private static McpSessionContext CreateSession() =>
        new("sess-gapb", "svc-agent", "tenant-alpha", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-GAPB-AGENT");

    private static ClaimsPrincipal CreatePrincipal(string sid, DateTimeOffset? issuedAt = null, string? jti = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("exp", DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
        };
        if (issuedAt is { } iat)
        {
            claims.Add(new Claim("iat", iat.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        }
        if (jti != null)
        {
            claims.Add(new Claim("jti", jti));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static (ISocketSession Session, IOperationMessagePayload Payload, DefaultHttpContext HttpContext, RecordingLifetimeFeature Lifetime) CreateSocketSession(string token)
    {
        var httpContext = new DefaultHttpContext();
        var lifetime = new RecordingLifetimeFeature();
        httpContext.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        var session = Substitute.For<ISocketSession>();
        session.Connection.HttpContext.Returns(httpContext);

        using var doc = JsonDocument.Parse($"{{\"authorization\":\"Bearer {token}\"}}");
        var payload = Substitute.For<IOperationMessagePayload>();
        payload.Payload.Returns(doc.RootElement.Clone());

        return (session, payload, httpContext, lifetime);
    }

    private static ISocketTokenValidator ValidatorFor(string token, ClaimsPrincipal principal)
    {
        var validator = Substitute.For<ISocketTokenValidator>();
        validator.ValidateTokenAsync(token, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(bool IsValid, ClaimsPrincipal? Principal)>((true, principal)));
        return validator;
    }

    // ---------------------------------------------------------------- GAP06

    [Fact]
    public async Task GAP06_OpenSchema_DoesNotExemptMcpToolsFromAbac()
    {
        var options = Options.Create(new GatewayOptions
        {
            OpenSchema = true,
            Catalog = new DataCatalogOptions { OpenSchema = true }
        });
        var policy = new DenyingPolicyService();
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"assets":[]}"""));

        var guardrail = new AiDataGuardrailService(new McpToolRegistry(), options, NullLogger<AiDataGuardrailService>.Instance,
            executor, policyEnforcementService: policy);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_data_catalog", "{}"), CreateSession());

        result.IsSuccess.ShouldBeFalse();
        policy.Calls.ShouldBe(1);
        await executor.DidNotReceive().ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GAP06_OnlyExplicitMcpAuthBypass_SkipsAbac()
    {
        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true },
            Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true }
        });
        var policy = new DenyingPolicyService();
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"assets":[]}"""));

        var guardrail = new AiDataGuardrailService(new McpToolRegistry(), options, NullLogger<AiDataGuardrailService>.Instance,
            executor, policyEnforcementService: policy);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_data_catalog", "{}"), CreateSession());

        result.IsSuccess.ShouldBeTrue();
        policy.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GAP06_OpenSchema_DoesNotOpenMcpResourcesForAnonymousCallers()
    {
        var table = new TableIdentifier("finance", "dbo", "ledger");
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(new List<TableMetadata>
        {
            new()
            {
                Identifier = table,
                Table = new Table { SchemaName = "dbo", TableName = "ledger", Description = "General ledger" },
                Columns = [new TableColumn { ColumnName = "iban", DataType = "varchar", IsSensitive = true }]
            }
        }));
        var consents = Substitute.For<IConsentRepository>();
        var options = Options.Create(new GatewayOptions { OpenSchema = true, Catalog = new DataCatalogOptions { OpenSchema = true } });
        var compiler = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance, null, consents, options);

        (await compiler.GetSemanticResourcesAsync(null, null)).ShouldBeEmpty();
        (await compiler.GetSemanticResourcesAsync(null, CreatePrincipal("ANONYMOUS_MCP_CLIENT"))).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- GAP07

    [Fact]
    public async Task GAP07_ExecutorErrorResult_IsNotAuditedAsAllow_AndIsReportedAsFailure()
    {
        var errorJson = GatewayMcpQueryExecutor.CreateErrorResult("tenant-alpha", "query_customers", McpErrorCodes.ExecutionFailed, "Tool execution failed.");
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(errorJson));
        var audit = Substitute.For<IAuditLogRepository>();

        var guardrail = new AiDataGuardrailService(new McpToolRegistry(), Options.Create(new GatewayOptions()),
            NullLogger<AiDataGuardrailService>.Instance, executor, audit);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_customers", "{}"), CreateSession());

        result.IsSuccess.ShouldBeFalse();
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "MCP_TOOL_EXECUTION" && e.Decision == "DENY" && e.DetailsJson.Contains("EXECUTION_FAILED")),
            Arg.Any<CancellationToken>());
        await audit.DidNotReceive().RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.Decision == "ALLOW"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GAP07_ForbiddenExecutorResult_SetsIsErrorInToolsCallResponse()
    {
        var errorJson = GatewayMcpQueryExecutor.CreateErrorResult("tenant-alpha", "query_customers", McpErrorCodes.Forbidden, "Access denied by data governance policy.");
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(errorJson));

        var registry = new McpToolRegistry();
        var guardrail = new AiDataGuardrailService(registry, Options.Create(new GatewayOptions()), NullLogger<AiDataGuardrailService>.Instance, executor);
        var handler = new McpProtocolHandler(new McpSessionStore(NullLogger<McpSessionStore>.Instance), registry, guardrail, NullLogger<McpProtocolHandler>.Instance);
        var session = handler.CreateSession("svc-agent", "tenant-alpha");

        var response = await handler.HandleMessageAsync(session.SessionId,
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"query_customers","arguments":{}}}""");

        using var doc = JsonDocument.Parse(response);
        var result = doc.RootElement.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        text.ShouldNotBeNull();
        text.ShouldContain("FORBIDDEN");
    }

    [Fact]
    public async Task GAP07_SuccessfulExecutorResult_KeepsIsErrorFalse()
    {
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"items":[{"id":"1"}],"isError":false}"""));

        var registry = new McpToolRegistry();
        var guardrail = new AiDataGuardrailService(registry, Options.Create(new GatewayOptions()), NullLogger<AiDataGuardrailService>.Instance, executor);
        var handler = new McpProtocolHandler(new McpSessionStore(NullLogger<McpSessionStore>.Instance), registry, guardrail, NullLogger<McpProtocolHandler>.Instance);
        var session = handler.CreateSession("svc-agent", "tenant-alpha");

        var response = await handler.HandleMessageAsync(session.SessionId,
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"query_customers","arguments":{}}}""");

        using var doc = JsonDocument.Parse(response);
        doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
    }

    // ---------------------------------------------------------------- PART-WS (M-14)

    [Fact]
    public async Task PARTWS_InMemory_SubjectRevocation_RevokesOlderTokensOnly()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryTokenRevocationService(clock);

        await store.RevokeAsync("S-1-5-21-WS-USER", clock.Now.AddHours(1));

        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-USER", clock.Now.AddMinutes(-5)))).ShouldBeTrue();
        (await store.IsRevokedAsync(CreatePrincipal("s-1-5-21-ws-user", clock.Now.AddMinutes(-5)))).ShouldBeTrue();
        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-USER"))).ShouldBeTrue(); // no iat -> conservative
        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-USER", clock.Now.AddMinutes(5)))).ShouldBeFalse(); // re-login after revocation
        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-OTHER", clock.Now.AddMinutes(-5)))).ShouldBeFalse();
    }

    [Fact]
    public async Task PARTWS_InMemory_JtiRevocation_RevokesOnlyThatToken_AndExpires()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryTokenRevocationService(clock);

        await store.RevokeAsync("jti-stolen-1", clock.Now.AddMinutes(30));

        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-USER", clock.Now.AddMinutes(-1), "jti-stolen-1"))).ShouldBeTrue();
        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-USER", clock.Now.AddMinutes(-1), "jti-fresh-2"))).ShouldBeFalse();

        clock.Now = clock.Now.AddMinutes(31);
        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-USER", clock.Now.AddMinutes(-40), "jti-stolen-1"))).ShouldBeFalse();
    }

    [Fact]
    public async Task PARTWS_Redis_RevocationFromOtherNode_IsDetected()
    {
        var revokedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(RedisValue.Null));
        db.StringGetAsync(Arg.Is<RedisKey>(k => k.ToString() == "Autheris:revoked:S-1-5-21-WS-REMOTE"), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)revokedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var store = new RedisTokenRevocationService(multiplexer, Options.Create(new GatewayOptions()), NullLogger<RedisTokenRevocationService>.Instance);

        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-REMOTE", revokedAt.AddMinutes(-10)))).ShouldBeTrue();
        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-REMOTE", revokedAt.AddMinutes(10)))).ShouldBeFalse();
        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-OTHER", revokedAt.AddMinutes(-10)))).ShouldBeFalse();
    }

    [Fact]
    public async Task PARTWS_Redis_Unavailable_LocalRevocationStillEnforced()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_ => throw new InvalidOperationException("redis down"));
        var store = new RedisTokenRevocationService(multiplexer, Options.Create(new GatewayOptions()), NullLogger<RedisTokenRevocationService>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => store.RevokeAsync("S-1-5-21-WS-LOCAL", DateTimeOffset.UtcNow.AddHours(1)));

        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-LOCAL", DateTimeOffset.UtcNow.AddMinutes(-5)))).ShouldBeTrue();
        (await store.IsRevokedAsync(CreatePrincipal("S-1-5-21-WS-UNKNOWN", DateTimeOffset.UtcNow.AddMinutes(-5)))).ShouldBeFalse();
    }

    [Fact]
    public async Task PARTWS_OnConnect_RevokedToken_IsRejected()
    {
        var (session, payload, _, _) = CreateSocketSession("revoked-token");
        var principal = CreatePrincipal("S-1-5-21-WS-REVOKED", DateTimeOffset.UtcNow.AddMinutes(-10));
        var revocation = new InMemoryTokenRevocationService();
        await revocation.RevokeAsync("S-1-5-21-WS-REVOKED", DateTimeOffset.UtcNow.AddHours(1));

        var interceptor = new WebSocketAuthInterceptor(NullLogger<WebSocketAuthInterceptor>.Instance, ValidatorFor("revoked-token", principal),
            revocationService: revocation);

        var status = await interceptor.OnConnectAsync(session, payload);

        status.Accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task PARTWS_RevocationDuringOpenSubscription_AbortsConnection()
    {
        var (session, payload, httpContext, lifetime) = CreateSocketSession("live-token");
        var principal = CreatePrincipal("S-1-5-21-WS-LIVE", DateTimeOffset.UtcNow.AddMinutes(-10), "jti-live-1");
        var revocation = new InMemoryTokenRevocationService();
        var interceptor = new WebSocketAuthInterceptor(NullLogger<WebSocketAuthInterceptor>.Instance, ValidatorFor("live-token", principal),
            revocationService: revocation);

        var status = await interceptor.OnConnectAsync(session, payload);
        status.Accepted.ShouldBeTrue();

        var watch = httpContext.Items[WebSocketAuthInterceptor.RevocationWatchItemKey].ShouldBeOfType<WebSocketAuthInterceptor.RevocationWatch>();
        try
        {
            (await watch.CheckAsync()).ShouldBeFalse();
            lifetime.Aborted.ShouldBeFalse();

            await revocation.RevokeAsync("jti-live-1", DateTimeOffset.UtcNow.AddMinutes(30));

            (await watch.CheckAsync()).ShouldBeTrue();
            lifetime.Aborted.ShouldBeTrue();
        }
        finally
        {
            watch.Dispose();
        }

        watch.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task PARTWS_RevalidationInterval_IsTakenFromOptions_DefaultSixtySeconds()
    {
        new GraphQLOptions().SubscriptionRevalidationSeconds.ShouldBe(60);
        WebSocketAuthInterceptor.DefaultRevalidationInterval.ShouldBe(TimeSpan.FromSeconds(60));

        var (session, payload, httpContext, _) = CreateSocketSession("interval-token");
        var principal = CreatePrincipal("S-1-5-21-WS-INTERVAL", DateTimeOffset.UtcNow.AddMinutes(-1));
        var clock = new CapturingTimeProvider();
        var options = Options.Create(new GatewayOptions { GraphQL = new GraphQLOptions { SubscriptionRevalidationSeconds = 15 } });
        var interceptor = new WebSocketAuthInterceptor(NullLogger<WebSocketAuthInterceptor>.Instance, ValidatorFor("interval-token", principal),
            clock, null, new InMemoryTokenRevocationService(), options);

        (await interceptor.OnConnectAsync(session, payload)).Accepted.ShouldBeTrue();

        clock.Period.ShouldBe(TimeSpan.FromSeconds(15));
        httpContext.Items[WebSocketAuthInterceptor.RevocationWatchItemKey].ShouldBeOfType<WebSocketAuthInterceptor.RevocationWatch>().Dispose();
    }

    [Fact]
    public void PARTWS_AdminEndpoint_RetentionIsBounded()
    {
        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        TokenRevocationEndpoints.ResolveUntil(null, now).ShouldBe(now.AddHours(24));
        TokenRevocationEndpoints.ResolveUntil(now.AddDays(365), now).ShouldBe(now.AddDays(30));
        TokenRevocationEndpoints.ResolveUntil(now.AddHours(2), now).ShouldBe(now.AddHours(2));
    }

    // ---------------------------------------------------------------- PART-FED

    private static SubgraphResultMasker CreateMasker()
    {
        var provider = Substitute.For<IColumnMaskingProvider>();
        provider.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("[MASKED]");
        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions { Enabled = true, EnableResultMasking = true }
        });
        return new SubgraphResultMasker(provider, options, NullLogger<SubgraphResultMasker>.Instance);
    }

    private static readonly ClaimsPrincipal Reader = new(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Reader")], "TestAuth"));

    [Theory]
    [InlineData("{ a { x: email } b { x: id } }")]
    [InlineData("{ b { x: id } a { x: email } }")]
    public void PARTFED_SameAliasOnDifferentPaths_MasksOnlyTheSensitivePath(string query)
    {
        var map = SubgraphResultMaskingMiddleware.ExtractAliasToFieldMap(Utf8GraphQLParser.Parse(query));
        map.ShouldNotBeNull();
        map["a.x"].ShouldBe("email");
        map["b.x"].ShouldBe("id");

        var data = new Dictionary<string, object?>
        {
            ["a"] = new Dictionary<string, object?> { ["x"] = "alice@example.com" },
            ["b"] = new Dictionary<string, object?> { ["x"] = "42" }
        };

        var masked = CreateMasker().MaskResultData(data, Reader, map) as Dictionary<string, object?>;

        masked.ShouldNotBeNull();
        ((Dictionary<string, object?>)masked["a"]!)["x"].ShouldBe("[MASKED]");
        ((Dictionary<string, object?>)masked["b"]!)["x"].ShouldBe("42");
    }

    [Fact]
    public void PARTFED_AliasInsideListAndNamedFragment_IsResolvedByPath()
    {
        var doc = Utf8GraphQLParser.Parse("query { customers { ...Pii } } fragment Pii on Customer { contact: email name }");
        var map = SubgraphResultMaskingMiddleware.ExtractAliasToFieldMap(doc);
        map.ShouldNotBeNull();
        map["customers.contact"].ShouldBe("email");

        var data = new Dictionary<string, object?>
        {
            ["customers"] = new List<object?>
            {
                new Dictionary<string, object?> { ["contact"] = "a@example.com", ["name"] = "A" },
                new Dictionary<string, object?> { ["contact"] = "b@example.com", ["name"] = "B" }
            }
        };

        var masked = CreateMasker().MaskResultData(data, Reader, map) as Dictionary<string, object?>;

        masked.ShouldNotBeNull();
        var items = masked["customers"].ShouldBeOfType<List<object?>>();
        foreach (var item in items)
        {
            var row = item.ShouldBeOfType<Dictionary<string, object?>>();
            row["contact"].ShouldBe("[MASKED]");
            row["name"].ShouldNotBe("[MASKED]");
        }
    }

    [Fact]
    public void PARTFED_ConflictingTypeConditionsOnSamePath_KeepSensitiveTarget()
    {
        var doc = Utf8GraphQLParser.Parse("{ node { ... on A { x: email } ... on B { x: id } } }");
        var map = SubgraphResultMaskingMiddleware.ExtractAliasToFieldMap(doc);

        map.ShouldNotBeNull();
        map["node.x"].ShouldBe("email");
    }
}
