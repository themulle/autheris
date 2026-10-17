namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>
/// Issue #127: <c>describe_dataset</c> value profiles and synthetic samples follow the column level the data path resolves for
/// the caller's token scopes (base rule plus scope rules, Deny-effect consents, tenant, failure modes).
/// </summary>
[Trait("Category", "Security")]
public sealed class McpDatasetCatalogScopeLevelTests
{
    private const string Dataset = "sales.public.orders";

    private static readonly ConsentColumnRule IdClear = Rule("id", ColumnAccessLevel.Clear);
    private static readonly ConsentColumnRule GradeClear = Rule("grade", ColumnAccessLevel.Clear);

    private static async Task<McpDatasetDescription> DescribeAsync(McpDatasetCatalog catalog, System.Security.Claims.ClaimsPrincipal principal) =>
        await catalog.DescribeDatasetAsync(principal, Dataset);

    private static McpDatasetColumn? Grade(McpDatasetDescription d) => d.Columns.SingleOrDefault(c => c.Name == "grade");

    private static void ShouldShowFullProfile(McpDatasetDescription d)
    {
        var grade = Grade(d).ShouldNotBeNull();
        grade.ValueProfile.ShouldNotBeNull().AllowedValues.ShouldNotBeNull().ShouldBe(SentinelValues, ignoreOrder: true);
    }

    private static void ShouldShowNoValues(McpDatasetDescription d)
    {
        // Mask keeps a profile object without any value, Deny has no profile or no column at all.
        var profile = Grade(d)?.ValueProfile;
        if (profile != null)
        {
            profile.AllowedValues.ShouldBeNull();
            profile.SampleValues.ShouldBeNull();
            profile.MinValue.ShouldBeNull();
            profile.MaxValue.ShouldBeNull();
            profile.DistinctCountBucket.ShouldBeNull();
        }

        Json(d).ShouldNotContain("SENTINEL_");
    }

    // ST-1 / T-1: every scope claim spelling the data path understands masks the column.
    [Theory]
    [InlineData("scp", ScopeA)]
    [InlineData("scp", "SCOPE-A")]
    [InlineData("http://schemas.microsoft.com/identity/claims/scope", ScopeA)]
    [InlineData("scope", ScopeA)]
    [InlineData("scope", "read scope-a write")]
    public async Task Describe_BaseClear_ScopeAMask_ScopeHolder_ProfileHasNoValues(string claimType, string scopes)
    {
        var consent = Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA));
        var catalog = CreateCatalog(CreateTable(), [consent]);

        var description = await DescribeAsync(catalog, Principal(claimType, scopes));

        ShouldShowNoValues(description);
        Grade(description).ShouldNotBeNull();
    }

    // T-2: no over-masking for other tokens.
    [Theory]
    [InlineData(null)]
    [InlineData(ScopeB)]
    public async Task Describe_BaseClear_ScopeAMask_OtherScopeOrNoScope_FullProfile(string? scope)
    {
        var consent = Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA));
        var catalog = CreateCatalog(CreateTable(), [consent]);

        var description = await DescribeAsync(catalog, scope == null ? WithScopes() : WithScopes(scope));

        ShouldShowFullProfile(description);
    }

    // T-3 / ST-16: scope-A Deny hides the column and its profile for scope A only.
    [Fact]
    public async Task Describe_BaseClear_ScopeADeny_HidesColumnForScopeAOnly()
    {
        var consent = Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Deny, ScopeA));
        var catalog = CreateCatalog(CreateTable(), [consent]);

        var forA = await DescribeAsync(catalog, WithScopes(ScopeA));
        Grade(forA).ShouldBeNull();
        Json(forA).ShouldNotContain("SENTINEL_");

        ShouldShowFullProfile(await DescribeAsync(catalog, WithScopes(ScopeB)));
    }

    // T-4: a scope-only Clear rule opens the column for its holder only (issue #116).
    [Fact]
    public async Task Describe_ScopeOnlyClear_ScopeHolderGetsProfile_OthersGetNoColumn()
    {
        var consent = Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear, ScopeA));
        var catalog = CreateCatalog(CreateTable(), [consent]);

        ShouldShowFullProfile(await DescribeAsync(catalog, WithScopes(ScopeA)));

        var other = await DescribeAsync(catalog, WithScopes(ScopeB));
        Grade(other).ShouldBeNull();
        Json(other).ShouldNotContain("SENTINEL_");
    }

    // T-5: synthetic rows follow the same level.
    [Fact]
    public async Task SampleRows_Synthetic_BaseClear_ScopeAMask_ScopeHolder_NoProfileDerivedValues()
    {
        var consent = Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA));
        var catalog = CreateCatalog(CreateTable(), [consent]);

        var masked = await catalog.SampleRowsAsync(WithScopes(ScopeA), Dataset, count: 5, synthetic: true);
        Json(masked).ShouldNotContain("SENTINEL_");

        var clear = await catalog.SampleRowsAsync(WithScopes(ScopeB), Dataset, count: 5, synthetic: true);
        Json(clear).ShouldContain("SENTINEL_ALLOWED");
    }

    // T-10 (guard): the not-granted baseline of ReleaseProfileToCatalogViewers stays.
    [Fact]
    public async Task Describe_NotGranted_ReleaseProfile_StillShowsBaselineProfile_SensitiveStaysMasked()
    {
        var open = CreateCatalog(CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true), []);
        var openDescription = await DescribeAsync(open, WithScopes(ScopeA));
        openDescription.AccessStatus.ShouldBe("NONE");
        ShouldShowFullProfile(openDescription);

        var sensitive = CreateCatalog(CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true, gradeSensitive: true), []);
        ShouldShowNoValues(await DescribeAsync(sensitive, WithScopes()));
    }

    // ST-2: a Deny-effect consent still denies in the not-granted branch, also scope bound.
    [Fact]
    public async Task Describe_NotGranted_DenyEffectColumnDeny_StaysDeny_EvenWithReleaseProfile()
    {
        var table = CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true);
        var catalog = CreateCatalog(table, [DenyEffect(Rule("grade", ColumnAccessLevel.Deny))]);

        var description = await DescribeAsync(catalog, WithScopes());

        Grade(description).ShouldNotBeNull().ValueProfile.ShouldBeNull();
        Json(description).ShouldNotContain("SENTINEL_");
        description.Columns.Single(c => c.Name == "id").ValueProfile.ShouldNotBeNull();
    }

    [Fact]
    public async Task Describe_NotGranted_ScopeBoundDenyEffectDeny_AppliesToScopeHolderOnly()
    {
        var table = CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true);
        var catalog = CreateCatalog(table, [DenyEffect(Rule("grade", ColumnAccessLevel.Deny, ScopeA))]);

        Grade(await DescribeAsync(catalog, WithScopes(ScopeA)))!.ValueProfile.ShouldBeNull();
        ShouldShowFullProfile(await DescribeAsync(catalog, WithScopes(ScopeB)));
        ShouldShowFullProfile(await DescribeAsync(catalog, WithScopes()));
    }

    // ST-3: an unconditional table Deny leaves no profile for any column.
    [Fact]
    public async Task Describe_NotGranted_UnconditionalTableDeny_NoProfileForAnyColumn()
    {
        var table = CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true);
        var catalog = CreateCatalog(table, [DenyEffect()]);

        var description = await DescribeAsync(catalog, WithScopes());

        description.Columns.ShouldAllBe(c => c.ValueProfile == null);
        Json(description).ShouldNotContain("SENTINEL_");
    }

    // ST-4: GRANTED status with a denied, null or throwing resolver never yields a value.
    private static IConsentResolutionService FailingResolver(int mode) => mode switch
    {
        0 => ResolverReturning(() => TableAccessDecision.Denied(Orders, "denied")),
        1 => ResolverReturning(() => null),
        2 => ResolverReturning(() => throw new InvalidOperationException("boom")),
        _ => ResolverReturning(() => TableAccessDecision.Allowed(
            Orders,
            new Dictionary<string, ColumnAccessLevel> { ["grade"] = (ColumnAccessLevel)99 },
            null,
            hasUnconstrainedColumnAllow: true))
    };

    [Theory]
    [InlineData(0)] // denied decision
    [InlineData(1)] // null decision
    [InlineData(2)] // throws
    [InlineData(3)] // undefined enum value
    public async Task Describe_StatusGranted_ResolverDeniesOrFails_ColumnUnderTestIsDenied(int mode)
    {
        var catalog = CreateCatalog(CreateTable(), [Allow()], FailingResolver(mode));

        var description = await DescribeAsync(catalog, WithScopes());

        description.AccessStatus.ShouldBe("GRANTED");
        Grade(description).ShouldNotBeNull().ValueProfile.ShouldBeNull();
        Json(description).ShouldNotContain("SENTINEL_");
    }

    [Fact]
    public async Task Describe_ThrowingResolver_NoColumnIsMaskedOrClear_AndSyntheticRowsOmitColumns()
    {
        var resolver = ResolverReturning(() => throw new InvalidOperationException("boom"));
        var catalog = CreateCatalog(CreateTable(), [Allow()], resolver);

        var description = await DescribeAsync(catalog, WithScopes());
        description.Columns.ShouldAllBe(c => c.ValueProfile == null);

        var sample = await catalog.SampleRowsAsync(WithScopes(), Dataset, count: 3, synthetic: true);
        Json(sample).ShouldNotContain("SENTINEL_");
        sample.Rows.ShouldAllBe(r => !r.ContainsKey("grade") && !r.ContainsKey("id"));
    }

    [Fact]
    public async Task Describe_ResolverThrowsOperationCanceled_Propagates()
    {
        var resolver = ResolverReturning(() => throw new OperationCanceledException());
        var catalog = CreateCatalog(CreateTable(), [Allow()], resolver);

        await Should.ThrowAsync<OperationCanceledException>(() => DescribeAsync(catalog, WithScopes()));
    }

    [Fact]
    public async Task Describe_CatalogDenyMaskingRule_StaysDeny_InGrantedAndBaselineBranch()
    {
        var masking = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["grade"] = new MaskingRule { RuleType = "DENY" } };

        var baseline = CreateCatalog(CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true, maskingRules: masking), []);
        Grade(await DescribeAsync(baseline, WithScopes()))?.ValueProfile.ShouldBeNull();

        var granted = CreateCatalog(CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true, maskingRules: masking), [Allow()]);
        var description = await DescribeAsync(granted, WithScopes());
        Grade(description)?.ValueProfile.ShouldBeNull();
        Json(description).ShouldNotContain("SENTINEL_");
    }

    // ST-5: a scope-bound Deny-effect Mask applies to scope holders only.
    [Fact]
    public async Task Describe_ScopeBoundDenyEffectMask_AppliesOnlyToScopeHolders()
    {
        var catalog = CreateCatalog(
            CreateTable(),
            [Allow(), DenyEffect(Rule("grade", ColumnAccessLevel.Mask, ScopeA))]);

        ShouldShowNoValues(await DescribeAsync(catalog, WithScopes(ScopeA)));
        ShouldShowFullProfile(await DescribeAsync(catalog, WithScopes(ScopeB)));
        ShouldShowFullProfile(await DescribeAsync(catalog, WithScopes()));
    }

    // ST-6: a consent of another tenant never grants.
    [Fact]
    public async Task Describe_ForeignTenantAllowConsent_DoesNotGrant()
    {
        var table = CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true);
        var catalog = CreateCatalog(table, [Allow("tenant-other")]);

        var description = await DescribeAsync(catalog, WithScopes());
        description.Columns.ShouldAllBe(c => c.ValueProfile == null);

        var sample = await catalog.SampleRowsAsync(WithScopes(), Dataset, count: 3, synthetic: true);
        Json(sample).ShouldNotContain("SENTINEL_");
    }

    // ST-9: only the canonical ClusterAdmin and GovernanceAdmin short-circuit to Clear.
    [Fact]
    public async Task Describe_Admin_ForwardAuthClusterAdminRole_IsNotAdmin_CanonicalAdminsStayClear()
    {
        var table = CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: true);
        var catalog = CreateCatalog(table, [DenyEffect(Rule("grade", ColumnAccessLevel.Deny))],
            gatewayOptions: Autheris.Tests.Unit.Catalog.Wp0.Options(Autheris.Domain.Options.TenancyMode.Multi, "sales.public.orders"));

        var forwardAuth = Principal(authenticationType: "ForwardAuth", roles: ["ClusterAdmin"]);
        Grade(await DescribeAsync(catalog, forwardAuth))!.ValueProfile.ShouldBeNull();

        ShouldShowFullProfile(await DescribeAsync(catalog, Principal(roles: ["ClusterAdmin"])));
        ShouldShowFullProfile(await DescribeAsync(catalog, Principal(roles: ["GovernanceAdmin"])));
    }

    // The raw column meta carries accepted values: only a Clear column returns it, descriptive keys always stay.
    [Fact]
    public async Task Describe_RawMeta_ValueKeysOnlyForClearColumn()
    {
        var consent = Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA));
        var catalog = CreateCatalog(CreateTable(), [consent]);

        var masked = await DescribeAsync(catalog, WithScopes(ScopeA));
        Json(masked).ShouldNotContain("SENTINEL_");
        Grade(masked).ShouldNotBeNull().Semantics.ShouldNotBeNull().RawMeta.ShouldNotBeNull().ContainsKey("accepted_values").ShouldBeFalse();

        var clear = await DescribeAsync(catalog, WithScopes(ScopeB));
        Json(clear).ShouldContain(SentinelOne);
        Grade(clear).ShouldNotBeNull().Semantics.ShouldNotBeNull().RawMeta.ShouldNotBeNull().ContainsKey("accepted_values").ShouldBeTrue();
    }

    [Fact]
    public async Task Describe_RawMeta_NotGrantedWithoutRelease_OmitsValueKeys()
    {
        var table = CreateTable(DatasetVisibility.Full, releaseProfileToCatalogViewers: false);
        var catalog = CreateCatalog(table, []);

        var description = await DescribeAsync(catalog, WithScopes());

        Json(description).ShouldNotContain("SENTINEL_");
    }

    // M1: a Clear column returns its raw meta values only when the profile layer publishes values.
    [Fact]
    public async Task Describe_RawMeta_ClearButSensitiveColumn_OmitsValueKeys()
    {
        var catalog = CreateCatalog(CreateTable(gradeSensitive: true), [Allow(IdClear, GradeClear)]);

        var description = await DescribeAsync(catalog, WithScopes());

        Json(description).ShouldNotContain("SENTINEL_");
    }

    [Fact]
    public async Task Describe_RawMeta_ClearColumnWithActiveRowFilter_OmitsValueKeysUnderDefaultPolicy()
    {
        var table = CreateTable() with { ProfilePublicationPolicy = DatasetProfilePublicationPolicy.FormatAndEmptyShareOnly };
        var catalog = CreateCatalog(table, [Allow(IdClear, GradeClear).WithFilters(RowFilter("1"))]);

        var description = await DescribeAsync(catalog, WithScopes());

        Json(description).ShouldNotContain("SENTINEL_");
    }

    // P1: meta keys differing only by case must not break the response and must stay descriptive-only.
    [Fact]
    public async Task Describe_RawMeta_CaseVariantKeys_DoNotThrow_AndValueKeysAreOmitted()
    {
        var table = CreateTable();
        var grade = new TableColumn
        {
            ColumnName = "grade",
            DataType = "varchar",
            Meta = new Dictionary<string, string>
            {
                ["role"] = "dimension",
                ["Role"] = "measure",
                ["accepted_values"] = System.Text.Json.JsonSerializer.Serialize(SentinelValues)
            }
        };
        var catalog = CreateCatalog(table with { Columns = [table.Columns[0], grade] }, [Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA))]);

        var description = await DescribeAsync(catalog, WithScopes(ScopeA));

        Json(description).ShouldNotContain("SENTINEL_");
        Grade(description).ShouldNotBeNull().Semantics.ShouldNotBeNull().RawMeta.ShouldNotBeNull().ContainsKey("role").ShouldBeTrue();
    }

    // M2 / AC-127-9: under the explicit MCP auth bypass the catalog baseline stays (Clear, Mask for sensitive columns).
    [Fact]
    public async Task Describe_McpAuthBypass_WithoutSid_KeepsBaseline()
    {
        var bypass = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true } };
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());

        var open = CreateCatalog(CreateTable(), [], gatewayOptions: bypass);
        ShouldShowFullProfile(await DescribeAsync(open, anonymous));

        var sensitive = CreateCatalog(CreateTable(gradeSensitive: true), [], gatewayOptions: bypass);
        ShouldShowNoValues(await DescribeAsync(sensitive, anonymous));
    }

    // ST-11: access profile Strict is not modelled; the catalog reports Mask (no values) for a sensitive column.
    [Fact]
    public async Task Describe_SensitiveColumn_NoValues()
    {
        var catalog = CreateCatalog(CreateTable(gradeSensitive: true), [Allow()]);

        ShouldShowNoValues(await DescribeAsync(catalog, WithScopes()));
    }

    // ST-12 / BC-9: the failure warning carries the table and the exception type only.
    [Fact]
    public async Task Describe_ResolutionFailure_LogsNoValues()
    {
        var logger = new CapturingLogger<McpDatasetCatalog>();
        var resolver = ResolverReturning(() => throw new InvalidOperationException("SECRET_EXCEPTION_MESSAGE " + SentinelOne));
        var catalog = CreateCatalog(CreateTable(), [Allow(Rule("grade", ColumnAccessLevel.Clear, "scope-secret-name"))], resolver, logger);

        await DescribeAsync(catalog, WithScopes("scope-secret-name"));

        var log = string.Join('\n', logger.Messages);
        log.ShouldContain(Dataset);
        log.ShouldContain(nameof(InvalidOperationException));
        log.ShouldNotContain("SECRET_EXCEPTION_MESSAGE");
        log.ShouldNotContain("SENTINEL_");
        log.ShouldNotContain("scope-secret-name");
    }

    // ST-14 / BC-11: no state is shared between callers of one catalog instance.
    [Fact]
    public async Task Describe_TwoCallersDifferentScopes_NoCrossContamination()
    {
        var consent = Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA));
        var catalog = CreateCatalog(CreateTable(), [consent]);

        ShouldShowNoValues(await DescribeAsync(catalog, WithScopes(ScopeA)));
        ShouldShowFullProfile(await DescribeAsync(catalog, WithScopes(ScopeB)));
        ShouldShowNoValues(await DescribeAsync(catalog, WithScopes(ScopeA)));

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
            (Masked: i % 2 == 0, Description: await DescribeAsync(catalog, i % 2 == 0 ? WithScopes(ScopeA) : WithScopes(ScopeB)))));
        foreach (var (masked, description) in results)
        {
            if (masked)
            {
                ShouldShowNoValues(description);
            }
            else
            {
                ShouldShowFullProfile(description);
            }
        }
    }

    // ST-13 / BC-10: the container wires the registered resolver into both services (through the real constructors).
    [Fact]
    public async Task Container_ResolvesCatalogAndAnalysis_WithRegisteredResolver()
    {
        var table = CreateTable();
        var spy = new CountingResolver();
        var services = new ServiceCollection();
        services.AddSingleton(MetadataRepository(table));
        services.AddSingleton(ConsentRepository([Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA))]));
        services.AddSingleton(Substitute.For<IGatewayExecutionService>());
        services.AddSingleton(Substitute.For<IAuditLogRepository>());
        services.AddSingleton<IConsentResolutionService>(spy);
        services.AddScoped<IMcpDatasetCatalog, McpDatasetCatalog>();
        services.AddScoped<IQueryAnalysisService, QueryAnalysisService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var description = await scope.ServiceProvider.GetRequiredService<IMcpDatasetCatalog>()
            .DescribeDatasetAsync(WithScopes(ScopeA), Dataset);
        spy.Calls.ShouldBeGreaterThan(0);
        var gradeProfile = description.Columns.Single(c => c.Name == "grade").ValueProfile;
        (gradeProfile?.AllowedValues).ShouldBeNull();

        var before = spy.Calls;
        var analysis = await scope.ServiceProvider.GetRequiredService<IQueryAnalysisService>()
            .AnalyzeQueriesAsync([QueryAnalysisItem.FromSql("SELECT id, grade FROM sales.public.orders", name: "q")], WithScopes(ScopeA));
        spy.Calls.ShouldBeGreaterThan(before);
        analysis.ValidationReport.OutputColumns.Single(c => c.Column == "grade").EffectiveAccessLevel.ShouldBe(ColumnAccessLevel.Mask);
    }

    private sealed class CountingResolver : IConsentResolutionService
    {
        private readonly ConsentResolutionService inner = new();
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public TableAccessDecision ResolveAccess(
            Sid userSid,
            IReadOnlySet<Sid> subjectGroupSids,
            IReadOnlySet<string> userRoles,
            TableIdentifier table,
            IReadOnlyList<Consent> activeConsents,
            Autheris.Domain.Common.DatabaseDialect dialect = Autheris.Domain.Common.DatabaseDialect.SqlServer,
            IReadOnlySet<Sid>? allUserSids = null,
            IReadOnlySet<string>? callerScopes = null)
        {
            Interlocked.Increment(ref calls);
            return inner.ResolveAccess(userSid, subjectGroupSids, userRoles, table, activeConsents, dialect, allUserSids, callerScopes);
        }
    }
}
