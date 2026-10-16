namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>
/// Issue #127: the query analysis, the catalog column visibility and the access explanation use the token scopes exactly as the
/// data path does (claims overload, plain <c>scope</c> claim included).
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue127ScopeAwareLevelTests
{
    private static readonly ConsentColumnRule IdClear = Rule("id", ColumnAccessLevel.Clear);
    private static readonly ConsentColumnRule GradeClear = Rule("grade", ColumnAccessLevel.Clear);
    private static readonly TenantId TenantA = new(Tenant);

    private static Task<QueryAnalysisResult> AnalyzeAsync(IReadOnlyList<Consent> consents, ClaimsPrincipal caller, ClaimsPrincipal? target = null) =>
        new QueryAnalysisService(MetadataRepository(CreateTable()), ConsentRepository(consents))
            .AnalyzeQueriesAsync([QueryAnalysisItem.FromSql("SELECT id, grade FROM sales.public.orders", name: "q")], caller, target);

    private static ClaimsPrincipal Simulator() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-AUDITOR"), new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "governance:simulate")], "MCP"));

    // T-6 / ST-8: base Clear + scope-A Mask is Mask (and requests cleartext) for the scope holder, Clear for others.
    [Theory]
    [InlineData("scp")]
    [InlineData("scope")]
    [InlineData("http://schemas.microsoft.com/identity/claims/scope")]
    public async Task QueryAnalysis_BaseClear_ScopeAMask_ScopeHolder_ReportsMaskAndReadCleartextSuggestion(string claimType)
    {
        var consents = new List<Consent> { Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA)) };

        var holder = await AnalyzeAsync(consents, Principal(claimType, ScopeA));
        holder.ValidationReport.OutputColumns.Single(c => c.Column == "grade").EffectiveAccessLevel.ShouldBe(ColumnAccessLevel.Mask);
        holder.AccessRequirements.MaskedColumnExceptions.ShouldContain(m => m.Column == "grade" && m.RequestedLevel == "READ_CLEARTEXT");

        var other = await AnalyzeAsync(consents, WithScopes(ScopeB));
        other.ValidationReport.OutputColumns.Single(c => c.Column == "grade").EffectiveAccessLevel.ShouldBe(ColumnAccessLevel.Clear);
        other.AccessRequirements.MaskedColumnExceptions.ShouldBeEmpty();
    }

    // T-7 / ST-7: an impersonated target is predicted for the worst case and flagged without values or scope names.
    [Fact]
    public async Task QueryAnalysis_ImpersonatedTarget_BaseClearScopeAMask_PredictsMask_AndEmitsDiagnostic()
    {
        var consents = new List<Consent> { Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA)) };

        var result = await AnalyzeAsync(consents, Simulator(), Principal());

        result.ValidationReport.OutputColumns.Single(c => c.Column == "grade").EffectiveAccessLevel.ShouldBe(ColumnAccessLevel.Mask);
        var diagnostic = result.ValidationReport.Diagnostics.Where(d => d.Code == QueryDiagnosticCodes.TargetScopesUnknown).ToList().ShouldHaveSingleItem();
        diagnostic.Message.ShouldNotContain(ScopeA);
        diagnostic.Message.ShouldNotContain("SENTINEL_");
    }

    [Fact]
    public async Task QueryAnalysis_ImpersonatedTarget_ScopeOnlyGrant_IsNotPredictedAsGranted()
    {
        var consents = new List<Consent> { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear, ScopeA)) };

        var result = await AnalyzeAsync(consents, Simulator(), Principal());

        // The no-scope evaluation denies the column; the minimum keeps it denied.
        result.ValidationReport.OutputColumns.Single(c => c.Column == "grade").EffectiveAccessLevel.ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public async Task QueryAnalysis_CallerIsTarget_NoDiagnostic()
    {
        var consents = new List<Consent> { Allow(IdClear, GradeClear) };

        var result = await AnalyzeAsync(consents, WithScopes(ScopeA), WithScopes(ScopeA));

        result.ValidationReport.Diagnostics.ShouldNotContain(d => d.Code == QueryDiagnosticCodes.TargetScopesUnknown);
    }

    [Fact]
    public async Task QueryAnalysis_ForeignTenantConsent_DoesNotGrant()
    {
        var consents = new List<Consent> { Allow("tenant-other") };

        var result = await AnalyzeAsync(consents, WithScopes());

        result.ValidationReport.OutputColumns.Where(c => c.Column == "grade")
            .ShouldAllBe(c => c.EffectiveAccessLevel == ColumnAccessLevel.Deny);
    }

    // T-8 / ST-15: CatalogVisibility is scope-aware; null scopes behave like a token without scopes.
    private static IReadOnlyList<string> VisibleColumns(IReadOnlyList<Consent> consents, IReadOnlySet<string>? scopes)
    {
        var visible = CatalogVisibility.FilterForSubject([CreateTable()], consents, TenantA, scopes);
        return visible.Single().Columns.Select(c => c.ColumnName).ToList();
    }

    private static IReadOnlySet<string> Scopes(params string[] s) => new HashSet<string>(s, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void CatalogVisibility_ScopeADeny_HidesColumnForScopeAOnly()
    {
        var consents = new List<Consent> { Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Deny, ScopeA)) };

        VisibleColumns(consents, Scopes(ScopeA)).ShouldNotContain("grade");
        VisibleColumns(consents, Scopes(ScopeB)).ShouldContain("grade");
        VisibleColumns(consents, null).ShouldContain("grade");
    }

    [Fact]
    public void CatalogVisibility_ScopeOnlyAllow_ShowsColumnForScopeHolderOnly()
    {
        var consents = new List<Consent> { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear, ScopeA)) };

        VisibleColumns(consents, Scopes(ScopeA)).ShouldContain("grade");
        VisibleColumns(consents, Scopes(ScopeB)).ShouldNotContain("grade");
        VisibleColumns(consents, null).ShouldNotContain("grade");
    }

    [Fact]
    public void CatalogVisibility_DenyEffectScopeBoundDeny_HidesColumnForScopeHolderOnly()
    {
        var consents = new List<Consent> { Allow(), DenyEffect(Rule("grade", ColumnAccessLevel.Deny, ScopeA)) };

        VisibleColumns(consents, Scopes(ScopeA)).ShouldNotContain("grade");
        VisibleColumns(consents, Scopes(ScopeB)).ShouldContain("grade");
        VisibleColumns(consents, null).ShouldContain("grade");
    }

    [Fact]
    public void CatalogVisibility_UnscopedDenyEffectDeny_HidesColumnForEveryone()
    {
        var consents = new List<Consent> { Allow(), DenyEffect(Rule("grade", ColumnAccessLevel.Deny)) };

        VisibleColumns(consents, null).ShouldNotContain("grade");
        VisibleColumns(consents, Scopes(ScopeA)).ShouldNotContain("grade");
    }

    // T-12: a column the data path does not deny is listed (single consent and unconstrained cases are exact).
    [Theory]
    [InlineData(ColumnAccessLevel.Clear, ColumnAccessLevel.Mask, ScopeA, true)]
    [InlineData(ColumnAccessLevel.Mask, ColumnAccessLevel.Clear, ScopeA, true)]
    [InlineData(ColumnAccessLevel.Clear, ColumnAccessLevel.Deny, ScopeA, false)]
    [InlineData(ColumnAccessLevel.Clear, ColumnAccessLevel.Deny, ScopeB, true)]
    public void CatalogVisibility_ListedExactlyWhenDataPathDoesNotDeny_SingleConsent(
        ColumnAccessLevel baseLevel, ColumnAccessLevel scopeLevel, string callerScope, bool expectedListed)
    {
        var consent = Allow(IdClear, Rule("grade", baseLevel), Rule("grade", scopeLevel, ScopeA));
        var scopes = Scopes(callerScope);

        var decision = new ConsentResolutionService().ResolveAccess(
            new Sid(UserSid), new HashSet<Sid>(), new HashSet<string>(), Orders, [consent], DatabaseDialect.SqlServer, null, scopes);
        var dataPathDenies = decision.GetEffectiveColumnAccess("grade", CreateTable()) == ColumnAccessLevel.Deny;

        dataPathDenies.ShouldBe(!expectedListed);
        VisibleColumns([consent], scopes).Contains("grade").ShouldBe(expectedListed);
    }

    // BC-1 follow-up: the access explanation reads a plain 'scope' claim like the data path.
    [Fact]
    public async Task AccessExplanation_PlainScopeClaim_AppliesScopeRule()
    {
        var table = CreateTable();
        var consents = new List<Consent> { Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA)) };

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(table);
        tableRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(consents));
        consentRepo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>(consents));
        var service = new EffectiveAccessExplanationService(tableRepo, consentRepo, new ConsentResolutionService());

        var masked = await service.ExplainMyAccessAsync(Principal("scope", ScopeA), Orders);
        masked.MaskedColumns.ShouldContain("grade");
        masked.AllowedColumns.ShouldNotContain("grade");

        var clear = await service.ExplainMyAccessAsync(WithScopes(ScopeB), Orders);
        clear.AllowedColumns.ShouldContain("grade");
    }
}
