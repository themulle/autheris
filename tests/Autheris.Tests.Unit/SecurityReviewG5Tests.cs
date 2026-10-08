namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Services;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security;
using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Itsm;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Itsm;
using Autheris.Infrastructure.Persistence;
using HotChocolate;
using Path = System.IO.Path;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Security review G5: subscriptions, MCP, SSRF/outbound HTTP, webhooks, ITSM, Iceberg guard, audit anchors.</summary>
public sealed class SecurityReviewG5Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "g5-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(_dir, true);
            }
        }
        catch (IOException) { }
    }

    // ---------------------------------------------------------------- A-3

    private sealed class BlockingChannel : ICdcEventChannel
    {
        public async IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            yield break;
        }

        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    /// <summary>GQL-3/GQL-4: admitted subscriptions need the governor and an allowing access decision.</summary>
    private static IServiceCollection SubscriptionServices()
    {
        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new ResolvedTableAccess(
                new TableMetadata { Identifier = ci.ArgAt<TableIdentifier>(1) },
                TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(1), new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                TenantId.LegacySingleTenant,
                new Sid("S-1-5-21-G5"),
                ci.ArgAt<ClaimsPrincipal?>(0)!)));
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(Options.Create(new GatewayOptions()))
            .AddSingleton<CdcSubscriptionGovernor>()
            .AddSingleton(resolver);
    }

    private static ClaimsPrincipal TokenPrincipal(DateTimeOffset exp) => new(new ClaimsIdentity(
        [new Claim("exp", exp.ToUnixTimeSeconds().ToString()), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-G5")], "Bearer"));

    [Fact]
    public async Task A3_Subscription_ExpiredToken_IsRejected()
    {
        var enumerator = new Subscription().SubscribeToTableEventsAsync(
            "orders", null, new BlockingChannel(), Substitute.For<IStreamRlsPolicyEnforcer>(),
            new ServiceCollection().BuildServiceProvider(), TokenPrincipal(DateTimeOffset.UtcNow.AddMinutes(-1)), CancellationToken.None).GetAsyncEnumerator();

        await Should.ThrowAsync<GraphQLException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task A3_Subscription_RevokedToken_IsRejected()
    {
        var revocation = Substitute.For<ITokenRevocationService>();
        revocation.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        var sp = new ServiceCollection().AddSingleton(revocation).BuildServiceProvider();

        var enumerator = new Subscription().SubscribeToTableEventsAsync(
            "orders", null, new BlockingChannel(), Substitute.For<IStreamRlsPolicyEnforcer>(),
            sp, TokenPrincipal(DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None).GetAsyncEnumerator();

        await Should.ThrowAsync<GraphQLException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task A3_Subscription_StreamEndsWhenTokenExpiresMidStream()
    {
        var enumerator = new Subscription().SubscribeToTableEventsAsync(
            "orders", null, new BlockingChannel(), Substitute.For<IStreamRlsPolicyEnforcer>(),
            SubscriptionServices().BuildServiceProvider(), TokenPrincipal(DateTimeOffset.UtcNow.AddSeconds(2)), CancellationToken.None).GetAsyncEnumerator();

        var moveNext = enumerator.MoveNextAsync().AsTask();
        var finished = await Task.WhenAny(moveNext, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.ShouldBe(moveNext);
        (await moveNext).ShouldBeFalse();
    }

    [Fact]
    public async Task A3_Subscription_StreamEndsWhenTokenIsRevokedMidStream()
    {
        var revoked = 0;
        var revocation = Substitute.For<ITokenRevocationService>();
        revocation.IsRevokedAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<bool>(Volatile.Read(ref revoked) == 1));
        var options = Options.Create(new GatewayOptions { GraphQL = new GraphQLOptions { SubscriptionRevalidationSeconds = 1 } });
        var sp = SubscriptionServices().AddSingleton(revocation).AddSingleton(options).BuildServiceProvider();

        var enumerator = new Subscription().SubscribeToTableEventsAsync(
            "orders", null, new BlockingChannel(), Substitute.For<IStreamRlsPolicyEnforcer>(),
            sp, TokenPrincipal(DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None).GetAsyncEnumerator();

        var moveNext = enumerator.MoveNextAsync().AsTask();
        Volatile.Write(ref revoked, 1);
        var finished = await Task.WhenAny(moveNext, Task.Delay(TimeSpan.FromSeconds(10)));
        finished.ShouldBe(moveNext);
        (await moveNext).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- A-4

    [Fact]
    public void A4_CuratedOperation_AllRootFieldsAreResolved()
    {
        var tool = new McpToolDefinition("curated", "d", "{}", "query { orders { id } payroll { id } }");

        var tables = AiDataGuardrailService.ParseTablesFromTool(tool);

        tables.ShouldNotBeNull();
        tables.Select(t => t.TableName).ShouldBe(["orders", "payroll"], ignoreOrder: true);
    }

    [Fact]
    public void A4_TableRootField_UsesLiteralDomainSchemaAndName()
    {
        var tool = new McpToolDefinition("curated", "d", "{}", "{ table(domain: \"hr\", schema: \"sec\", name: \"salaries\") { rows } }");

        var tables = AiDataGuardrailService.ParseTablesFromTool(tool);

        tables.ShouldNotBeNull();
        tables.Single().ShouldBe(new TableIdentifier("hr", "sec", "salaries"));
    }

    [Theory]
    [InlineData("{ table(domain: $d, name: \"x\") { rows } }")]
    [InlineData("{ table(name: \"x\") { rows } }")]
    [InlineData("this is not graphql")]
    [InlineData("fragment F on Query { id }")]
    public void A4_UnresolvableOperation_FailsClosed(string operation)
    {
        var tool = new McpToolDefinition("curated", "d", "{}", operation);

        AiDataGuardrailService.ParseTablesFromTool(tool).ShouldBeNull();
    }

    [Fact]
    public void A4_FragmentsAtRoot_AreExpanded()
    {
        var tool = new McpToolDefinition("curated", "d", "{}", "query { ...F } fragment F on Query { secrets { id } }");

        AiDataGuardrailService.ParseTablesFromTool(tool)!.Single().TableName.ShouldBe("secrets");
    }

    // ---------------------------------------------------------------- MCP client IP

    [Fact]
    public void Mcp_RefreshPrincipalContext_UpdatesClientIp()
    {
        var store = new McpSessionStore(NullLogger<McpSessionStore>.Instance);
        var session = store.CreateSession("svc", "tenant-a", "S-1", ["Reader"], [], "10.0.0.1");

        var refreshed = store.RefreshPrincipalContext(session.SessionId, ["Reader"], [], "10.0.0.2");

        refreshed!.ClientIp.ShouldBe("10.0.0.2");
        store.RefreshPrincipalContext(session.SessionId, ["Reader"], []).ShouldNotBeNull().ClientIp.ShouldBe("10.0.0.2");
    }

    // ---------------------------------------------------------------- I-1

    [Fact]
    public async Task I1_ValidateResolved_UnresolvableHost_FailsClosed()
    {
        await Should.ThrowAsync<SecurityException>(() =>
            EgressUrlPolicy.ValidateResolvedAsync(new Uri("https://does-not-exist.invalid/x"), isDev: false));
    }

    [Fact]
    public void I1_PrimaryHandler_DoesNotUseSystemProxyByDefault()
    {
        using var closed = SecureOutboundHttp.CreatePrimaryHandler(false, EgressAllowlist.Empty, false, (_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        using var opted = SecureOutboundHttp.CreatePrimaryHandler(false, EgressAllowlist.Empty, false, (_, _) => Task.FromResult(Array.Empty<IPAddress>()), allowSystemProxy: true);

        closed.UseProxy.ShouldBeFalse();
        opted.UseProxy.ShouldBeTrue();
    }

    [Fact]
    public void I1_EgressOptions_SystemProxyIsOffByDefault() => new GatewayOptions().Egress.AllowSystemProxy.ShouldBeFalse();

    // ---------------------------------------------------------------- Webhook dispatcher

    [Theory]
    [InlineData("Host")]
    [InlineData("host")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("ce-id")]
    [InlineData("X-Autheris-Signature")]
    public void Webhook_ForbiddenCustomHeaders(string name) => CloudEventWebhookDispatcher.IsForbiddenCustomHeader(name).ShouldBeTrue();

    [Theory]
    [InlineData("X-Api-Key")]
    [InlineData("Authorization")]
    [InlineData("X-Custom-Tenant")]
    public void Webhook_AllowedCustomHeaders(string name) => CloudEventWebhookDispatcher.IsForbiddenCustomHeader(name).ShouldBeFalse();

    // ---------------------------------------------------------------- Secrets

    [Fact]
    public void Secrets_NullEnvironment_IsTreatedAsProduction()
    {
        Should.Throw<SecurityException>(() => SecretReferenceResolver.Resolve(null, "literal-secret", null));
    }

    [Fact]
    public void Secrets_Development_StillAllowsPlaintextFallback()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        SecretReferenceResolver.Resolve(null, "literal-secret", env).ShouldBe("literal-secret");
    }

    // ---------------------------------------------------------------- WORM anchors

    [Fact]
    public void Worm_InvalidNewestFile_FallsBackToNewestValidAnchor()
    {
        var worm = new WormDirectoryAuditChainAnchorStore(_dir);
        worm.Save(new AuditChainAnchor(1, "h1", DateTimeOffset.UtcNow.AddMinutes(-2), "sig1"));
        worm.Save(new AuditChainAnchor(2, "h2", DateTimeOffset.UtcNow.AddMinutes(-1), "sig2"));
        File.WriteAllText(Path.Combine(_dir, "anchor-" + new string('9', 20) + "-" + new string('9', 20) + ".json"), "{ not json");

        var loaded = worm.Load();

        loaded.ShouldNotBeNull();
        loaded.Sequence.ShouldBe(2);
    }

    [Fact]
    public void Worm_NewestFileWithBadSignature_IsSkipped()
    {
        var worm = new WormDirectoryAuditChainAnchorStore(_dir, a => a.Signature == "good");
        worm.Save(new AuditChainAnchor(1, "h1", DateTimeOffset.UtcNow.AddMinutes(-2), "good"));
        worm.Save(new AuditChainAnchor(5, "forged", DateTimeOffset.UtcNow, "bad"));

        worm.Load()!.Sequence.ShouldBe(1);
    }

    [Fact]
    public void Worm_OnlyInvalidFiles_ReturnsInvalidAnchor()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "anchor-00000000000000000001-00000000000000000001.json"), "garbage");

        new WormDirectoryAuditChainAnchorStore(_dir).Load()!.Sequence.ShouldBe(-1);
    }

    // ---------------------------------------------------------------- Break-glass attribution

    [Fact]
    public async Task BreakGlass_AuditUsesSidAndTenantNotIdentityName()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        AuditLogEntry? captured = null;
        repo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => captured = e), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var interceptor = new JustificationAndBreakGlassInterceptor(
            Options.Create(new GatewayOptions()), NullLogger<JustificationAndBreakGlassInterceptor>.Instance, repo);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "operator"), new Claim(ClaimTypes.Role, "BreakGlassOperator"),
             new Claim(ClaimTypes.PrimarySid, "S-1-5-21-OPS"), new Claim("tenant_id", "tenant-x")], "TestAuth"));
        var context = new IngressContext
        {
            Headers = new Dictionary<string, string> { ["X-Break-Glass"] = "true", ["X-Access-Justification"] = "INC-1" },
            User = user
        };

        await interceptor.OnIngressAsync(context);

        captured.ShouldNotBeNull();
        captured.ActorSid.Value.ShouldBe("S-1-5-21-OPS");
        captured.TenantId.Value.ShouldBe("tenant-x");
    }

    // ---------------------------------------------------------------- Iceberg guard

    private static readonly TableIdentifier OrdersId = new("tenant-1", "raw", "orders");

    private static (IcebergRestCatalogFederationService svc, IPolicyEnforcementService policy) IcebergService(bool tableActive, bool abacAllowed)
    {
        var meta = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "tenant-1", SchemaName = "raw", TableName = "orders", DataSourceType = DataSourceType.LakehouseIceberg, IsActive = tableActive },
            Columns = [new TableColumn { ColumnName = "id" }]
        };
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(meta));
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IReadOnlyList<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([new Consent
            {
                TableIdentifier = OrdersId,
                TenantId = new TenantId("tenant-1"),
                GranteeType = GranteeType.User,
                GranteeSid = new Sid("analyst@corp.com"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules = Array.Empty<ConsentColumnRule>()
            }]));
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(abacAllowed
                ? TableAccessDecision.Allowed(OrdersId, new Dictionary<string, ColumnAccessLevel>())
                : TableAccessDecision.Denied(OrdersId, "abac")));
        var svc = new IcebergRestCatalogFederationService(
            Substitute.For<IIcebergMetadataReader>(), metaRepo, Options.Create(new GatewayOptions()),
            NullLogger<IcebergRestCatalogFederationService>.Instance, new Autheris.Application.Services.ConsentResolutionService(), consentRepo, policy);
        return (svc, policy);
    }

    private static ClaimsPrincipal Analyst() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "analyst@corp.com"), new Claim(ClaimTypes.PrimarySid, "analyst@corp.com")], "Bearer"));

    [Fact]
    public async Task Iceberg_InactiveTable_IsNotServed()
    {
        var (svc, _) = IcebergService(tableActive: false, abacAllowed: true);

        await Should.ThrowAsync<KeyNotFoundException>(() => svc.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
    }

    [Fact]
    public async Task Iceberg_CasbinDeny_BlocksRawAccess()
    {
        var (svc, policy) = IcebergService(tableActive: true, abacAllowed: false);

        await Should.ThrowAsync<SecurityException>(() => svc.LoadTableAsync("tenant-1", "raw", "orders", Analyst()).AsTask());
        await policy.Received().EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Iceberg_CasbinAllow_StillServesFullyConsentedTable()
    {
        var (svc, _) = IcebergService(tableActive: true, abacAllowed: true);

        var response = await svc.LoadTableAsync("tenant-1", "raw", "orders", Analyst());

        response.ShouldNotBeNull();
    }

    // ---------------------------------------------------------------- ITSM

    [Fact]
    public void Itsm_UnknownServiceNowState_IsIgnoredNotRejected()
    {
        var parse = typeof(ItsmWebhookHandler).GetMethod("ParsePayload", BindingFlags.NonPublic | BindingFlags.Static)!;
        var json = "{\"number\":\"CHG1\",\"instance_name\":\"i1\",\"state\":\"2\"}";

        var dto = parse.Invoke(null, [json, NullLogger.Instance]);

        dto.ShouldNotBeNull();
        dto.GetType().GetProperty("Action")!.GetValue(dto)!.ToString().ShouldBe("IGNORE");
    }

    [Fact]
    public void Itsm_ExplicitRejectState_IsStillRejected()
    {
        var parse = typeof(ItsmWebhookHandler).GetMethod("ParsePayload", BindingFlags.NonPublic | BindingFlags.Static)!;
        var json = "{\"number\":\"CHG1\",\"instance_name\":\"i1\",\"approval\":\"rejected\"}";

        var dto = parse.Invoke(null, [json, NullLogger.Instance]);

        dto!.GetType().GetProperty("Action")!.GetValue(dto)!.ToString().ShouldBe("REJECT");
    }

    private sealed class StubEpochValidationService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }

    private static async Task<(SqliteGovernanceRepository repo, ConsentRequest request)> PendingExternalRequestAsync(string status = "PENDING_EXTERNAL_APPROVAL")
    {
        var repo = new SqliteGovernanceRepository(new StubEpochValidationService());
        var tableId = new TableIdentifier("sales", "crm", "g5_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table { Id = Guid.NewGuid(), SourceName = tableId.Domain, SchemaName = tableId.Schema, TableName = tableId.TableName, IsActive = true };
        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "VARCHAR" }]
        });
        var created = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-G5"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-G5",
            BusinessJustification = "G5",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Status = status,
            TenantId = new TenantId("tenant-a")
        });
        return (repo, created);
    }

    [Fact]
    public async Task Itsm_Reject_DoesNotTrustItsmSidPrefix()
    {
        var (repo, request) = await PendingExternalRequestAsync();
        using (repo)
        {
            await Should.ThrowAsync<UnauthorizedAccessException>(() =>
                repo.RejectConsentRequestAsync(request.Id, new Sid("ITSM_ATTACKER"), "no"));
            await Should.ThrowAsync<UnauthorizedAccessException>(() =>
                repo.RejectConsentRequestAsync(request.Id, new Sid("ITSM_ATTACKER"), "no", isExternalItsm: false));
        }
    }

    [Fact]
    public async Task Itsm_Reject_AuthorizedWhenWebhookFlagsExternalItsm()
    {
        var (repo, request) = await PendingExternalRequestAsync();
        using (repo)
        {
            var rejected = await repo.RejectConsentRequestAsync(request.Id, new Sid("ITSM_SERVICENOW:inst"), "no", isExternalItsm: true);

            rejected.Status.ShouldBe("REJECTED");
        }
    }

    [Fact]
    public async Task Consent_ActivateConsent_DoesNotActivatePendingRequests()
    {
        var (repo, request) = await PendingExternalRequestAsync();
        using (repo)
        {
            await repo.ActivateConsentAsync(request.Id, new Sid("ITSM_SERVICENOW:inst"));

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
            (await repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-G5")], request.TableIdentifier, DateTimeOffset.UtcNow))
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Consent_AutoApprovePath_StillActivatesPendingRequests()
    {
        var (repo, request) = await PendingExternalRequestAsync("PENDING");
        using (repo)
        {
            await repo.ActivateConsentForAutoApproveAsync(request.Id);

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("APPROVED");
        }
    }

    [Fact]
    public async Task Outbox_DispatchLockHeldElsewhere_SkipsCycle()
    {
        var outbox = Substitute.For<IItsmOutboxRepository>();
        var cluster = Substitute.For<IDistributedClusterStateProvider>();
        cluster.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null));
        var sp = new ServiceCollection()
            .AddSingleton(outbox)
            .AddSingleton(cluster)
            .AddSingleton(new ItsmWorkflowDispatcher([], NullLogger<ItsmWorkflowDispatcher>.Instance))
            .BuildServiceProvider();
        var service = new ItsmOutboxDispatcherHostedService(sp, NullLogger<ItsmOutboxDispatcherHostedService>.Instance);

        (await service.ProcessPendingMessagesAsync()).ShouldBe(0);

        await outbox.DidNotReceive().GetPendingMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    [Fact]
    public async Task INF4_Outbox_StopsDispatchingBeforeTheLockLeaseRunsOut()
    {
        // INF-4: each dispatch takes 2 minutes here; with a 5 minute lease only messages that still fit are started,
        // the rest stays pending for the next cycle instead of being sent after another instance took the lock.
        var clock = new ManualTimeProvider();
        var messages = Enumerable.Range(1, 5)
            .Select(i => new ItsmOutboxMessage($"m{i}", $"r{i}", "tenant-a", "CREATE", "{\"Title\":\"t\"}", "ServiceNow", ItsmOutboxStatus.Pending, 0, 5, DateTimeOffset.UtcNow))
            .ToList();
        var outbox = Substitute.For<IItsmOutboxRepository>();
        outbox.GetPendingMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(messages);
        outbox.MarkFailedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => { clock.Advance(TimeSpan.FromMinutes(2)); return Task.CompletedTask; });
        var cluster = Substitute.For<IDistributedClusterStateProvider>();
        cluster.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>(Substitute.For<IAsyncDisposable>()));
        var sp = new ServiceCollection()
            .AddSingleton(outbox)
            .AddSingleton(cluster)
            .AddSingleton(new ItsmWorkflowDispatcher([], NullLogger<ItsmWorkflowDispatcher>.Instance))
            .BuildServiceProvider();
        var service = new ItsmOutboxDispatcherHostedService(sp, NullLogger<ItsmOutboxDispatcherHostedService>.Instance, timeProvider: clock);

        await service.ProcessPendingMessagesAsync();

        await outbox.Received(2).MarkFailedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
