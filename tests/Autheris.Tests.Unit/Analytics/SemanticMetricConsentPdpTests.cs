namespace Autheris.Tests.Unit.Analytics;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>
/// MISC-06: a metric consent can only cure <see cref="TableAccessDenialKind.ConsentMissing"/>; every other PDP denial stays final and
/// the cured decision comes from the full PDP pipeline (no direct consent resolution).
/// </summary>
public sealed class SemanticMetricConsentPdpTests
{
    private static readonly TableMetadata Meta = CreateTable();

    private static readonly DbtMetric Mrr = new("mrr", "simple", null, null, null, "{ \"measure\": \"total\" }", [], ["total"],
        new Dictionary<string, string> { ["model"] = "m" });

    private static Consent MetricConsent() => new()
    {
        TableIdentifier = new TableIdentifier("sales", "public", "metric:mrr"),
        TableId = Meta.Table.Id,
        TenantId = new TenantId(Tenant),
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(UserSid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = [Rule("id", ColumnAccessLevel.Clear)],
        RowFilters = []
    };

    private static TableAccessDecision Allowed() =>
        TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear });

    private static TableAccessDecision ConsentMissing() =>
        TableAccessDecision.Denied(Orders, "Zero Trust: No active ALLOW consent granted to subject.") with { DenialKind = TableAccessDenialKind.ConsentMissing };

    private static async Task<(TableAccessDecision Decision, bool HasMetricConsent, IUnifiedPolicyDecisionPoint Pdp)> RunAsync(
        TableAccessDecision pdpTableDecision,
        TableAccessDecision? pdpMetricDecision = null)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>([]));
        repo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>([MetricConsent()]));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(pdpTableDecision));
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyList<Consent>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(pdpMetricDecision ?? TableAccessDecision.Denied(Orders, "metric pipeline denied")));

        var service = new SemanticMetricQueryService(
            Substitute.For<ISemanticCatalogService>(),
            Substitute.For<IGovernedAggregationService>(),
            Substitute.For<ITableMetadataRepository>(),
            consentRepository: repo,
            pdp: pdp);

        var task = (Task<(TableAccessDecision Decision, bool HasMetricConsent)>)typeof(SemanticMetricQueryService)
            .GetMethod("ResolveMetricAndTableDecisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [WithScopes(), new Sid(UserSid), new TenantId(Tenant), Meta, new List<DbtMetric> { Mrr }, CancellationToken.None])!;
        var (decision, hasMetricConsent) = await task;
        return (decision, hasMetricConsent, pdp);
    }

    private static async Task AssertStaysDeniedAsync(string reason)
    {
        var denial = TableAccessDecision.Denied(Orders, reason);

        var (decision, hasMetricConsent, pdp) = await RunAsync(denial, Allowed());

        decision.IsAllowed.ShouldBeFalse();
        hasMetricConsent.ShouldBeFalse();
        await pdp.DidNotReceiveWithAnyArgs().EvaluateAccessAsync(default!, default!, default!, default, (IReadOnlyList<Consent>?)null, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public Task MetricConsent_CasbinDenied_StaysDenied() =>
        AssertStaysDeniedAsync("Casbin ABAC Policy Denial: Access denied for subject.");

    [Fact]
    [Trait("Category", "Security")]
    public Task MetricConsent_RebacDenied_StaysDenied() =>
        AssertStaysDeniedAsync("ReBAC: relation check failed.");

    [Fact]
    [Trait("Category", "Security")]
    public Task MetricConsent_ContractDenied_StaysDenied() =>
        AssertStaysDeniedAsync("Contract violation: schema contract denies access.");

    [Fact]
    [Trait("Category", "Security")]
    public Task MetricConsent_VirtualFilterDenied_StaysDenied() =>
        AssertStaysDeniedAsync("Denied by virtual filters.");

    [Fact]
    [Trait("Category", "Security")]
    public Task MetricConsent_ProfileSourceUnavailable_StaysDenied() =>
        AssertStaysDeniedAsync("Access profile source unavailable (fail-closed): boom");

    [Fact]
    [Trait("Category", "Security")]
    public async Task MetricConsent_ConsentDenyKind_StaysDenied()
    {
        var denial = TableAccessDecision.Denied(Orders, "Hard DENY") with { DenialKind = TableAccessDenialKind.ConsentDeny };

        var (decision, _, pdp) = await RunAsync(denial, Allowed());

        decision.IsAllowed.ShouldBeFalse();
        await pdp.DidNotReceiveWithAnyArgs().EvaluateAccessAsync(default!, default!, default!, default, (IReadOnlyList<Consent>?)null, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MetricConsent_ConsentMissing_AllowedWithVirtualFilterPredicateApplied()
    {
        var curedByPdp = Allowed().WithMandatoryPredicate("tenant_scope = 7", ["vf-tenant"]) with { EvaluatedByPdp = true };

        var (decision, hasMetricConsent, pdp) = await RunAsync(ConsentMissing(), curedByPdp);

        decision.IsAllowed.ShouldBeTrue();
        hasMetricConsent.ShouldBeTrue();
        decision.MandatoryRowPredicateSql.ShouldBe("tenant_scope = 7");
        await pdp.Received(1).EvaluateAccessAsync(
            Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Is<IReadOnlyList<Consent>?>(c => c != null && c.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MetricConsent_CasbinRestrict_RowFilterApplied()
    {
        var restricted = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }, rowFilterSql: "region = 'EU'")
            with { EvaluatedByPdp = true };

        var (decision, _, _) = await RunAsync(ConsentMissing(), restricted);

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("region = 'EU'");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MetricConsent_PdpDeniesCuredDecision_StaysDenied()
    {
        var (decision, hasMetricConsent, _) = await RunAsync(ConsentMissing(), TableAccessDecision.Denied(Orders, "Casbin ABAC Policy Denial"));

        decision.IsAllowed.ShouldBeFalse();
        hasMetricConsent.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Aggregation_PreEvaluatedDecisionWithoutPdpProvenance_IsReevaluated()
    {
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Denied(Orders, "Casbin ABAC Policy Denial")));
        var audit = Substitute.For<IAuditLogRepository>();
        var service = new GovernedAggregationService(Substitute.For<ITableMetadataRepository>(), audit, pdp: pdp);
        var forged = Allowed();
        var query = new AggregationQuery(Orders, PreEvaluatedDecision: forged, BypassTableConsent: true);

        var untrusted = await ResolveAsync(service, query);
        var trusted = await ResolveAsync(service, query with { PreEvaluatedDecision = forged with { EvaluatedByPdp = true } });

        untrusted.IsAllowed.ShouldBeFalse();
        trusted.IsAllowed.ShouldBeTrue();
    }

    private static async Task<TableAccessDecision> ResolveAsync(GovernedAggregationService service, AggregationQuery query)
    {
        var task = (Task<TableAccessDecision>)typeof(GovernedAggregationService)
            .GetMethod("ResolveDecisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [WithScopes(), new Sid(UserSid), new TenantId(Tenant), Meta, query, CancellationToken.None])!;
        return await task;
    }
}
