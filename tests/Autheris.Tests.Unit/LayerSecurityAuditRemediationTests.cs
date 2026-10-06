#pragma warning disable CA2012

namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Federation.Services;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.RateLimiting;
using HotChocolate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class LayerSecurityAuditRemediationTests
{
    [Fact]
    public void Domain_ValidateIdentifier_RejectsTrailingNewline()
    {
        // SEC-DOM-01: \A...\z instead of ^...$
        Should.Throw<ArgumentException>(() => DatabaseDialectExtensions.ValidateIdentifier("customers\n"));
        Should.Throw<ArgumentException>(() => DatabaseDialectExtensions.ValidateIdentifier("customers\r\n"));
        Should.Throw<ArgumentException>(() => DatabaseDialectExtensions.ValidateIdentifier("customers "));
        Should.Throw<ArgumentException>(() => DatabaseDialectExtensions.ValidateIdentifier("customers;DROP TABLE"));
        DatabaseDialectExtensions.ValidateIdentifier("customers_2026");
    }

    [Fact]
    public void Domain_Sid_InvalidTenantIdThrowsSecurityException()
    {
        // SEC-DOM-02: Zero-trust fail-closed when tenant_id claim is malformed
        var badClaims = new List<Claim>
        {
            new("tenant_id", "bad/tenant!id")
        };
        var badPrincipal = new ClaimsPrincipal(new ClaimsIdentity(badClaims, "TestAuth"));

        Should.Throw<SecurityException>(() => badPrincipal.GetTenantId());

        // Valid claim should parse correctly
        var goodClaims = new List<Claim>
        {
            new("tenant_id", "tenant-eu-1")
        };
        var goodPrincipal = new ClaimsPrincipal(new ClaimsIdentity(goodClaims, "TestAuth"));
        goodPrincipal.GetTenantId().Value.ShouldBe("tenant-eu-1");
    }

    [Fact]
    public void Domain_TableIdentifier_RejectsNullBytesAndDelimiters()
    {
        // SEC-DOM-03: TableIdentifier components must reject null bytes, newlines, and dot separators
        Should.Throw<ArgumentException>(() => new TableIdentifier("corp\0", "sales", "invoices"));
        Should.Throw<ArgumentException>(() => new TableIdentifier("corp", "sales\n", "invoices"));
        Should.Throw<ArgumentException>(() => new TableIdentifier("corp", "sales.override", "invoices"));

        var valid = new TableIdentifier("corp", "sales", "invoices_2026");
        valid.Domain.ShouldBe("corp");
    }

    [Fact]
    public async Task Application_Casbin_EnforcesSubRuleOnRlsFilters()
    {
        // CRIT-01: sub_rule must be evaluated during RLS filter collection
        var tenant = new TenantId("tenant-corp");
        var table = new TableIdentifier("sales", "public", "orders");
        var service = new CasbinEnforcementService();

        // Rule with sub_rule checking department == 'HR'
        service.AddPolicy(
            tenant,
            "role:analyst",
            table.ToString(),
            "read",
            "r.ctx.Department == 'HR'",
            "allow",
            rlsFilter: "dept = 'HR'");

        // Context 1: User in Finance department -> subRule does NOT match
        var financeCtx = new SecurityEvaluationContext(
            UserSid: new Sid("S-1-5-21-FINANCE"),
            GroupSids: [new Sid("role:analyst")],
            Tenant: tenant,
            TargetTable: table,
            RequestedColumns: ["id", "amount"],
            ClientIp: System.Net.IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null,
            Attributes: new Dictionary<string, object?> { ["department"] = "Finance" });

        var financeDecision = await service.EvaluatePolicyAsync(financeCtx);
        financeDecision.IsAllowed.ShouldBeFalse();
        financeDecision.CombinedRowFilterSql.ShouldBeNull();

        // Context 2: User in HR department -> subRule DOES match
        var hrCtx = new SecurityEvaluationContext(
            UserSid: new Sid("S-1-5-21-HR"),
            GroupSids: [new Sid("role:analyst")],
            Tenant: tenant,
            TargetTable: table,
            RequestedColumns: ["id", "amount"],
            ClientIp: System.Net.IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null,
            Attributes: new Dictionary<string, object?> { ["department"] = "HR" });

        var hrDecision = await service.EvaluatePolicyAsync(hrCtx);
        hrDecision.IsAllowed.ShouldBeTrue();
        hrDecision.CombinedRowFilterSql.ShouldNotBeNull();
        hrDecision.CombinedRowFilterSql.ShouldContain("dept = 'HR'");
    }

    [Fact]
    public async Task Application_Casbin_DenyTakesPrecedenceOverGroupAllow()
    {
        // FINDING-06: Explicit user deny must not be bypassed by group fallback
        var tenant = new TenantId("tenant-corp");
        var table = new TableIdentifier("sales", "public", "orders");
        var service = new CasbinEnforcementService();

        // Group has allow
        service.AddPolicy(tenant, "group:all_users", table.ToString(), "read", "true", "allow");

        // Specific user has deny
        service.AddPolicy(tenant, "S-1-5-21-BLOCKED-USER", table.ToString(), "read", "true", "deny");

        var blockedCtx = new SecurityEvaluationContext(
            UserSid: new Sid("S-1-5-21-BLOCKED-USER"),
            GroupSids: [new Sid("group:all_users")],
            Tenant: tenant,
            TargetTable: table,
            RequestedColumns: ["id"],
            ClientIp: System.Net.IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null);

        var decision = await service.EvaluatePolicyAsync(blockedCtx);
        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Application_StreamRls_EnforcesCombinedRowFilterOnPayload()
    {
        // CRIT-02: Streaming CDC event row filtering must drop non-matching payloads
        var policyMock = Substitute.For<IPolicyEnforcementService>();
        var metadataMock = Substitute.For<ITableMetadataRepository>();
        var maskingMock = Substitute.For<IColumnMaskingProvider>();
        var epochMock = Substitute.For<IEpochValidationService>();

        var table = new TableIdentifier("corp", "sales", "orders");
        var tenant = new TenantId("tenant-1");

        // Policy allows read but with RLS filter: department = 'HR'
        policyMock.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<TableAccessDecision>(TableAccessDecision.Allowed(
                table,
                new Dictionary<string, ColumnAccessLevel>(),
                rowFilterSql: "department = 'HR'",
                hasUnconstrainedColumnAllow: true)));

        // SEC H-09: Consent decision first (allowed, unconstrained), Casbin RLS filter as additional gate.
        policyMock.HasPolicies(Arg.Any<TenantId>()).Returns(true);
        metadataMock.GetTableMetadataAsync(table, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Identifier = table,
                Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "department" }]
            }));
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(
                Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var enforcer = new StreamRlsPolicyEnforcer(
            policyMock,
            metadataMock,
            maskingMock,
            epochMock,
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            consentRepo,
            resolution,
            Substitute.For<IConsentCacheService>());

        // SEC H-09: A subscriber without subject identifier is denied, therefore the principal carries a SID.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "tenant-1"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-STREAM-USER")], "TestAuth"));

        // Event 1: Matches department 'HR'
        var matchingEvent = new CdcEvent(
            EventId: "ev-1",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: "tenant-1",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 1, ["department"] = "HR" },
            Timestamp: DateTimeOffset.UtcNow);

        var matchResult = await enforcer.EvaluateAndMaskAsync(matchingEvent, principal);
        matchResult.IsAllowed.ShouldBeTrue();

        // Event 2: Does NOT match department (Finance) -> must be denied fail-closed
        var nonMatchingEvent = new CdcEvent(
            EventId: "ev-2",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: "tenant-1",
            Before: null,
            After: new Dictionary<string, object?> { ["id"] = 2, ["department"] = "Finance" },
            Timestamp: DateTimeOffset.UtcNow);

        var nonMatchResult = await enforcer.EvaluateAndMaskAsync(nonMatchingEvent, principal);
        nonMatchResult.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Application_BreakGlass_RejectsUnauthenticated()
    {
        // CRIT-03: Unauthenticated break-glass request must be rejected with 401
        var gatewayOptions = new GatewayOptions
        {
            Extensibility = new ExtensibilityOptions
            {
                Enabled = true,
                EnableBreakGlass = true,
                RequireRoleForBreakGlass = true,
                BreakGlassAllowedRoles = ["EmergencyAdmin"]
            }
        };

        var interceptor = new JustificationAndBreakGlassInterceptor(
            Options.Create(gatewayOptions),
            NullLogger<JustificationAndBreakGlassInterceptor>.Instance);

        var context = new IngressContext
        {
            User = null, // unauthenticated
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Break-Glass"] = "true",
                ["X-Access-Justification"] = "INC-12345"
            }
        };

        var result = await interceptor.OnIngressAsync(context);
        result.Decision.ShouldBe(IngressDecision.Deny);
        result.StatusCode.ShouldBe(401);
    }

    [Fact]
    public void Application_SubgraphResultMasker_MasksAliasesAndNumbers()
    {
        // CRIT-04 & SEC-APP-04: Masking must work with GraphQL aliases and non-string types
        var maskingProviderMock = Substitute.For<IColumnMaskingProvider>();
        maskingProviderMock.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>())
            .Returns("[REDACTED]");

        var options = Options.Create(new GatewayOptions
        {
            Federation = new FederationOptions { Enabled = true, EnableResultMasking = true }
        });

        var masker = new SubgraphResultMasker(maskingProviderMock, options, NullLogger<SubgraphResultMasker>.Instance);

        var payload = new Dictionary<string, object?>
        {
            ["customerName"] = "Alice",
            ["contactEmail"] = "alice@example.com",
            ["annualCompensation"] = 120000m
        };

        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["contactEmail"] = "email",
            ["annualCompensation"] = "salary"
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("role", "User")], "BasicAuth"));
        var masked = masker.MaskResultData(payload, principal, aliasMap) as Dictionary<string, object?>;

        masked.ShouldNotBeNull();
        masked["customerName"].ShouldBe("Alice");
        masked["contactEmail"].ShouldBe("[REDACTED]");
        masked["annualCompensation"].ShouldBe("[REDACTED]");
    }

    [Fact]
    public async Task GraphQL_Subscription_RejectsUnauthenticated()
    {
        // SEC-GQL-04: Subscriptions must reject unauthenticated identities
        var subscription = new Subscription();
        var eventChannelMock = Substitute.For<ICdcEventChannel>();
        var enforcerMock = Substitute.For<IStreamRlsPolicyEnforcer>();

        var unauthenticatedPrincipal = new ClaimsPrincipal(new ClaimsIdentity());

        var enumerator = subscription.SubscribeToTableEventsAsync(
            "orders",
            null,
            eventChannelMock,
            enforcerMock,
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()),
            unauthenticatedPrincipal,
            CancellationToken.None).GetAsyncEnumerator();

        await Should.ThrowAsync<GraphQLException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task Infrastructure_InMemoryRateLimiter_CostBucketsAreBounded()
    {
        // SEC-INFRA-01: Cost buckets must be bounded to prevent OOM
        var limiter = new InMemoryRateLimiterService();
        var policy = new ClientQuotaPolicy(
            Tier: ClientTier.Standard,
            MaxCostPerQuery: 200,
            MaxComplexityDepth: 10,
            MaxTokensCapacity: 100,
            TokenRefillRatePerSecond: 10.0,
            ExposeCostExtensions: false);

        // Standard usage should succeed
        var res1 = await limiter.CheckCostQuotaAsync("client-1", 10, policy);
        res1.Allowed.ShouldBeTrue();
        res1.RemainingTokens.ShouldBe(90);

        // Excess cost should be denied
        var res2 = await limiter.CheckCostQuotaAsync("client-1", 150, policy);
        res2.Allowed.ShouldBeFalse();
    }
}
