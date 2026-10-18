namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Connectors;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Policy;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>
/// Issue #143 (PLAN-GOV-143 WP-143-2): the consumers of the decision (rebuild sites, connector reader, procedures, explanation, analysis,
/// describe_dataset) apply the restricted-scope clearance exactly like the resolver does.
/// </summary>
public sealed class Issue143ConsumersTests
{
    private const string Restricted = "Agent.Read";
    private static readonly TableMetadata Meta = CreateTable(gradeSensitive: true);
    private static readonly ConsentColumnRule IdClear = Rule("id", ColumnAccessLevel.Clear);
    private static readonly ConsentColumnRule GradeClear = Rule("grade", ColumnAccessLevel.Clear);

    private static IReadOnlySet<string>? Scopes(params string[] scopes) =>
        scopes.Length == 0 ? null : new HashSet<string>(scopes, StringComparer.OrdinalIgnoreCase);

    /// <summary>Decision of the production resolver (default restricted list) for the kit user.</summary>
    private static TableAccessDecision Resolve(IReadOnlyList<Consent> consents, params string[] scopes) =>
        new ConsentResolutionService().ResolveAccess(
            new Sid(UserSid), new HashSet<Sid>(), new HashSet<string>(), Orders, consents, callerScopes: Scopes(scopes));

    private static IReadOnlyList<Consent> BaseClear() => [Allow(IdClear, GradeClear)];

    // ---------------------------------------------------------------- D-7 aggregation fallback (T-GOV143-11)

    private static TableAccessDecision Fallback(TableMetadata meta, TableAccessDecision? existing) =>
        (TableAccessDecision)typeof(GovernedAggregationService)
            .GetMethod("BuildMetricScopedFallbackDecision", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [meta, existing])!;

    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_11_AggregationFallback_ExistingNull_SensitiveMasked_ExistingClearance_Kept()
    {
        Fallback(Meta, null).GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Mask);

        var existing = Resolve(BaseClear(), Restricted);
        existing.ScopeClearance.ShouldNotBeNull();
        var rebuilt = Fallback(Meta, existing);

        rebuilt.ScopeClearance.ShouldBeSameAs(existing.ScopeClearance);
        rebuilt.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Mask);
        rebuilt.GetEffectiveColumnAccess("id", Meta).ShouldBe(ColumnAccessLevel.Clear);

        // An unrestricted caller keeps what it had (no clearance is invented).
        var unrestricted = Fallback(Meta, Resolve(BaseClear()));
        unrestricted.ScopeClearance.ShouldBeNull();
        unrestricted.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void AggregationFallback_KeepsClearance()
    {
        var existing = Resolve([Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Clear, Restricted))], Restricted);

        var rebuilt = Fallback(Meta, existing);

        rebuilt.ScopeClearance.ShouldBeSameAs(existing.ScopeClearance);
        rebuilt.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Clear);
    }

    // ---------------------------------------------------------------- D-7 metrics fallback without resolver (T-GOV143-12)

    private static readonly DbtMetric Mrr = new("mrr", "simple", null, null, null, "{ \"measure\": \"total\" }", [], ["total"],
        new Dictionary<string, string> { ["model"] = "m" });

    private static Consent MetricConsent(IReadOnlyList<ConsentColumnRule> rules, IReadOnlyList<ConsentRowFilter>? filters = null) => new()
    {
        TableIdentifier = new TableIdentifier("sales", "public", "metric:mrr"),
        TableId = Meta.Table.Id,
        TenantId = new TenantId(Tenant),
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(UserSid),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = rules,
        RowFilters = filters ?? []
    };

    private static async Task<TableAccessDecision> MetricDecisionWithoutResolverAsync(ClaimsPrincipal principal, Consent metricConsent)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>([]));
        repo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>([metricConsent]));
        var service = new SemanticMetricQueryService(
            Substitute.For<ISemanticCatalogService>(),
            Substitute.For<IGovernedAggregationService>(),
            Substitute.For<ITableMetadataRepository>(),
            consentRepository: repo);

        var task = (Task<(TableAccessDecision Decision, bool HasMetricConsent)>)typeof(SemanticMetricQueryService)
            .GetMethod("ResolveMetricAndTableDecisionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [principal, new Sid(UserSid), new TenantId(Tenant), Meta, new List<DbtMetric> { Mrr }, CancellationToken.None])!;
        var (decision, hasMetricConsent) = await task;
        hasMetricConsent.ShouldBeTrue();
        return decision;
    }

    [Fact]
    public async Task MetricWithoutResolver_RestrictedMasked()
    {
        var consent = MetricConsent([IdClear, GradeClear]);

        var restricted = await MetricDecisionWithoutResolverAsync(WithScopes(Restricted), consent);
        restricted.IsAllowed.ShouldBeTrue();
        restricted.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Mask);

        var other = await MetricDecisionWithoutResolverAsync(WithScopes(ScopeA), consent);
        other.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Clear);

        var scopeClear = await MetricDecisionWithoutResolverAsync(WithScopes(Restricted),
            MetricConsent([IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Clear, Restricted)]));
        scopeClear.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_GOV143_12_MetricFallback_DoesNotWiden_VsLegacyBranch()
    {
        // The legacy branch released the table without any row filter and without the scope restriction; the shared resolver keeps both.
        var filtered = await MetricDecisionWithoutResolverAsync(WithScopes(Restricted), MetricConsent([IdClear, GradeClear], [RowFilter("7")]));
        filtered.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();
        filtered.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Mask);

        // Scope-bound Deny and a scope-only Allow stay as the resolver computes them.
        var denied = await MetricDecisionWithoutResolverAsync(WithScopes(Restricted),
            MetricConsent([IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Deny, Restricted)]));
        denied.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Deny);
    }

    // ---------------------------------------------------------------- D-7 connector reader

    [Fact]
    public void ConnectorReader_RestrictedMasked()
    {
        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(default!, default, default!).ReturnsForAnyArgs("MASKED");
        var policy = new GovernedRowPolicy(masking, null);
        var raw = new Dictionary<string, object?> { ["id"] = 1, ["grade"] = SentinelOne };

        var restricted = GovernedConnectorReader.ProjectRow(raw, Meta, Resolve(BaseClear(), Restricted), Tenant, policy, alreadyMasked: false);
        restricted["grade"].ShouldBe("MASKED");
        restricted["id"].ShouldBe(1);

        var unrestricted = GovernedConnectorReader.ProjectRow(raw, Meta, Resolve(BaseClear()), Tenant, policy, alreadyMasked: false);
        unrestricted["grade"].ShouldBe(SentinelOne);
    }

    // ---------------------------------------------------------------- D-8 procedures (T-GOV143-13)

    private static HashSet<string>? SensitivityOnly(TableAccessDecision decision) =>
        (HashSet<string>?)typeof(GovernedProcedureExecutionService)
            .GetMethod("ComputeSensitivityOnlyMask", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [decision, Meta]);

    [Fact]
    public void ProcedureClearedColumns_UnrestrictedToken_ContractLift()
    {
        // No column rule: the sensitive column is masked by catalog sensitivity only, so the contract may lift it.
        var unrestricted = Resolve([Allow()]);
        SensitivityOnly(unrestricted).ShouldNotBeNull().ShouldContain("grade");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_13_Procedure_ClearedColumns_RestrictedToken_NoLift_AllPaths()
    {
        // The lift set is the only source of a procedure `cleared_columns` lift (sync, async and nested result masking read it from the
        // table decision): empty for every restricted decision, whatever the consent says.
        SensitivityOnly(Resolve([Allow()], Restricted)).ShouldNotBeNull().ShouldBeEmpty();
        SensitivityOnly(Resolve(BaseClear(), Restricted)).ShouldNotBeNull().ShouldBeEmpty();
        SensitivityOnly(Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear, Restricted))], Restricted)).ShouldNotBeNull().ShouldBeEmpty();
        SensitivityOnly(Resolve([Allow()], ScopeA)).ShouldNotBeNull().ShouldContain("grade");

        // A decision copy (the ABAC rewrite) keeps the clearance, so the rule holds after it.
        var copy = Resolve([Allow()], Restricted) with { HasUnconstrainedColumnAllow = true };
        SensitivityOnly(copy).ShouldNotBeNull().ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- D-9 explanation (T-GOV143-14)

    private static EffectiveAccessExplanationService Explainer(IReadOnlyList<Consent> consents)
    {
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(Meta);
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Meta]));
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(consents));
        consentRepo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(consents));
        return new EffectiveAccessExplanationService(tableRepo, consentRepo, new ConsentResolutionService());
    }

    [Fact]
    public async Task ExplainMyAccess_ReportsRestrictedScopeAndColumns()
    {
        var service = Explainer(BaseClear());

        var restricted = await service.ExplainMyAccessAsync(WithScopes(Restricted), Orders);
        restricted.RestrictedScope.ShouldBeTrue();
        restricted.ScopeClearRequiredColumns.ShouldNotBeNull().ShouldBe(["grade"]);
        restricted.MaskedColumns.ShouldContain("grade");

        var unrestricted = await service.ExplainMyAccessAsync(WithScopes(ScopeA), Orders);
        unrestricted.RestrictedScope.ShouldBeFalse();
        unrestricted.ScopeClearRequiredColumns.ShouldBeNull();
        unrestricted.AllowedColumns.ShouldContain("grade");

        var cleared = await Explainer([Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Clear, Restricted))])
            .ExplainMyAccessAsync(WithScopes(Restricted), Orders);
        cleared.RestrictedScope.ShouldBeTrue();
        cleared.ScopeClearRequiredColumns.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_GOV143_14_Explain_RestrictedFields_NoDenyColumns_NoScopeNames_NonSubjectForbidden()
    {
        // grade is Deny for the restricted scope: it is no scope-clear candidate (never a Deny column), and no scope name is echoed.
        var service = Explainer([Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Deny, Restricted))]);

        var result = await service.ExplainMyAccessAsync(WithScopes(Restricted), Orders);

        result.RestrictedScope.ShouldBeTrue();
        result.DeniedColumns.ShouldContain("grade");
        result.ScopeClearRequiredColumns.ShouldNotBeNull().ShouldBeEmpty();
        Json(result).ShouldNotContain(Restricted);

        var masked = await Explainer(BaseClear()).ExplainMyAccessAsync(WithScopes(Restricted), Orders);
        Json(masked).ShouldNotContain(Restricted);
        Json(masked).ShouldNotContain("Agent");

        // The service has no subject parameter: only the principal's own access is explained. A principal without a SID gets nothing.
        var anonymous = await service.ExplainMyAccessAsync(new ClaimsPrincipal(new ClaimsIdentity()), Orders);
        anonymous.RestrictedScope.ShouldBeFalse();
        anonymous.ScopeClearRequiredColumns.ShouldBeNull();
        anonymous.AllowedColumns.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- D-10 analysis (AC-13, T-GOV143-16)

    private static Task<QueryAnalysisResult> AnalyzeImpersonatedAsync(
        IReadOnlyList<Consent> consents, IRestrictedScopePolicy? analysisPolicy, IRestrictedScopePolicy? resolverPolicy)
    {
        var service = new QueryAnalysisService(
            MetadataRepository(Meta),
            ConsentRepository(consents),
            consentResolution: new ConsentResolutionService(null, resolverPolicy),
            restrictedScopes: analysisPolicy);
        var simulator = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-AUDITOR"), new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "governance:simulate")], "MCP"));
        return service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql("SELECT id, grade FROM sales.public.orders", name: "q")], simulator, Principal());
    }

    private static ColumnAccessLevel GradeLevel(QueryAnalysisResult r) =>
        r.ValidationReport.OutputColumns.Single(c => c.Column == "grade").EffectiveAccessLevel;

    [Fact]
    public async Task ImpersonatedAnalysis_PredictsMask()
    {
        var result = await AnalyzeImpersonatedAsync(BaseClear(), null, null);

        GradeLevel(result).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_GOV143_16_ImpersonatedAnalysis_EnabledFalse_NoExtraEvaluation_EnabledTrue_Minimum()
    {
        // Enabled: the extra evaluation with the restricted list lowers the scopeless Clear to Mask.
        GradeLevel(await AnalyzeImpersonatedAsync(BaseClear(), RestrictedScopePolicy.Default, RestrictedScopePolicy.Default))
            .ShouldBe(ColumnAccessLevel.Mask);

        // Disabled: no extra evaluation (and a resolver of the same policy), so the prediction stays Clear.
        GradeLevel(await AnalyzeImpersonatedAsync(BaseClear(), RestrictedScopePolicy.Disabled, RestrictedScopePolicy.Disabled))
            .ShouldBe(ColumnAccessLevel.Clear);

        // A scope-specific Clear satisfies the restricted evaluation too: the minimum across evaluations stays Clear.
        GradeLevel(await AnalyzeImpersonatedAsync(
                [Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Clear, Restricted))],
                RestrictedScopePolicy.Default, RestrictedScopePolicy.Default))
            .ShouldBe(ColumnAccessLevel.Clear);

        // A scope-only Clear is Deny for the scopeless evaluation, so the minimum is Deny (unchanged by the restriction).
        GradeLevel(await AnalyzeImpersonatedAsync(
                [Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear, Restricted))],
                RestrictedScopePolicy.Default, RestrictedScopePolicy.Default))
            .ShouldBe(ColumnAccessLevel.Deny);
    }

    // ---------------------------------------------------------------- describe_dataset

    [Fact]
    public async Task Describe_ScopeMaskedColumn_ShowsNoValueProfileNorSamples()
    {
        var table = CreateTable(gradeSensitive: true);
        var catalog = CreateCatalog(table, BaseClear());

        var restricted = await catalog.DescribeDatasetAsync(WithScopes(Restricted), "sales.public.orders");
        var profile = restricted.Columns.Single(c => c.Name == "grade").ValueProfile;
        if (profile != null)
        {
            profile.AllowedValues.ShouldBeNull();
            profile.SampleValues.ShouldBeNull();
        }

        Json(restricted).ShouldNotContain("SENTINEL_");

        var sample = await catalog.SampleRowsAsync(WithScopes(Restricted), "sales.public.orders", count: 3, synthetic: true);
        Json(sample).ShouldNotContain("SENTINEL_");

        // Positive control (a plain column of the same shape shows its values), so the assertions above are not vacuous. A catalog-sensitive
        // column never carries values in describe_dataset (profile service), the scope restriction only adds the data-path level on top.
        var plain = await CreateCatalog(CreateTable(), BaseClear()).DescribeDatasetAsync(WithScopes(Restricted), "sales.public.orders");
        Json(plain).ShouldContain(SentinelOne);
    }

    // ---------------------------------------------------------------- parity (unit seams; AC-7, T-GOV143-17)

    /// <summary>Every consumer of one decision sees the same level: the planner/SQL/GraphQL/WebSQL/MCP/OLAP/export all read GetEffectiveColumnAccess.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_17_Parity_RestrictedStillMasked_AcrossConsumerSeams()
    {
        var restricted = Resolve(BaseClear(), Restricted);
        var withAbacCopy = restricted.WithAdditionalRowFilter("id > 0");
        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(default!, default, default!).ReturnsForAnyArgs("MASKED");

        foreach (var decision in new[] { restricted, withAbacCopy })
        {
            decision.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Mask);                       // SQL / GraphQL / REST / OData / MCP query
            MaskedColumnSet.For(Meta, decision).ShouldContain("grade");                                              // OLAP and SQL federation staging
            GovernedConnectorReader.ProjectRow(new Dictionary<string, object?> { ["grade"] = "x" }, Meta, decision, Tenant,
                new GovernedRowPolicy(masking, null), alreadyMasked: false)["grade"].ShouldBe("MASKED");             // export / connectors
            Fallback(Meta, decision).GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Mask);       // governed aggregation
            decision.IsMaskedByScopeRestriction("grade", Meta).ShouldBeTrue();                                       // describe / explain
        }

        MaskedColumnSet.For(Meta, Resolve(BaseClear())).ShouldNotContain("grade");
    }

    // ---------------------------------------------------------------- AC-8: filter / group on a masked column

    [Fact]
    public void FilterOrGroupOnMaskedColumn_RejectedForRestrictedOnly()
    {
        // The planners reject filter/group/sort on a column whose effective level is Mask: that level is what differs per token.
        var restricted = Resolve(BaseClear(), Restricted);
        var other = Resolve(BaseClear(), ScopeA);

        restricted.GetEffectiveColumnAccess("grade", Meta).ShouldNotBe(ColumnAccessLevel.Clear);
        other.GetEffectiveColumnAccess("grade", Meta).ShouldBe(ColumnAccessLevel.Clear);
    }

    // ---------------------------------------------------------------- T-GOV143-15 remainder: scp survives the security context (async export, PDP)

    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_15_SecurityContextClaims_KeepScp_ForAsyncExport()
    {
        var context = SecurityPrincipalContext.FromPrincipal(WithScopes(Restricted), new TenantId(Tenant));

        context.Claims.ShouldNotBeNull();
        TokenAccessScope.GetScopeSet(context.Claims).ShouldContain(Restricted);
    }
}
