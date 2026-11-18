namespace Autheris.Tests.Unit.Mcp;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
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

    private static void ShouldShowNoValues(McpDatasetDescription d)
    {
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

    // ST-1 / T-1: every scope claim spelling masks or hides meta values unconditionally.
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

    // T-2: legacy describe_dataset never synthesizes ValueProfile from column.Meta.
    [Theory]
    [InlineData(null)]
    [InlineData(ScopeB)]
    public async Task Describe_BaseClear_ScopeAMask_OtherScopeOrNoScope_NoMetaProfileLeak(string? scope)
    {
        var consent = Allow(IdClear, GradeClear, Rule("grade", ColumnAccessLevel.Mask, ScopeA));
        var catalog = CreateCatalog(CreateTable(), [consent]);

        var description = await DescribeAsync(catalog, scope == null ? WithScopes() : WithScopes(scope));

        ShouldShowNoValues(description);
    }

    // ST-2: Deny consent resolves without leaking meta values.


    // ST-3: an unconditional table Deny leaves no profile for any column.

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
    public async Task Describe_ThrowingResolver_NoColumnHasProfile()
    {
        var resolver = ResolverReturning(() => throw new InvalidOperationException("boom"));
        var catalog = CreateCatalog(CreateTable(), [Allow()], resolver);

        var description = await DescribeAsync(catalog, WithScopes());
        description.Columns.ShouldAllBe(c => c.ValueProfile == null);
        Json(description).ShouldNotContain("SENTINEL_");
    }

    [Fact]
    public async Task Describe_ResolverThrowsOperationCanceled_Propagates()
    {
        var resolver = ResolverReturning(() => throw new OperationCanceledException());
        var catalog = CreateCatalog(CreateTable(), [Allow()], resolver);

        await Should.ThrowAsync<OperationCanceledException>(() => DescribeAsync(catalog, WithScopes()));
    }


    // ST-6: a consent of another tenant never grants.

    // ST-9: only canonical ClusterAdmin and GovernanceAdmin are treated as admin.

    // The raw column meta accepted_values is never emitted in describe_dataset; descriptive keys stay.
    [Fact]
    public async Task Describe_RawMeta_OmitsValueKeys_KeepsDescriptiveKeys()
    {
        var consent = Allow(IdClear, GradeClear);
        var catalog = CreateCatalog(CreateTable(), [consent]);

        var description = await DescribeAsync(catalog, WithScopes(ScopeB));
        Json(description).ShouldNotContain("SENTINEL_");
        Grade(description).ShouldNotBeNull().Semantics.ShouldNotBeNull().RawMeta.ShouldNotBeNull().ContainsKey("accepted_values").ShouldBeFalse();
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

    // M2 / AC-127-9: under MCP auth bypass without sid, no meta profile values leak.
    [Fact]
    public async Task Describe_McpAuthBypass_WithoutSid_KeepsNoMetaProfileValues()
    {
        var bypass = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true } };
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());

        var open = CreateCatalog(CreateTable(), [], gatewayOptions: bypass);
        ShouldShowNoValues(await DescribeAsync(open, anonymous));

        var sensitive = CreateCatalog(CreateTable(gradeSensitive: true), [], gatewayOptions: bypass);
        ShouldShowNoValues(await DescribeAsync(sensitive, anonymous));
    }

    // MCP-17: the bypass path must not serve inactive (quarantined) tables.
    [Fact]
    public async Task Describe_McpAuthBypass_InactiveTable_IsNotFound()
    {
        var bypass = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true } };
        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        var table = CreateTable();
        var catalog = CreateCatalog(table with { Table = table.Table with { IsActive = false } }, [], gatewayOptions: bypass);

        await Should.ThrowAsync<Autheris.Domain.Exceptions.TableNotFoundException>(() => DescribeAsync(catalog, anonymous));
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
        ShouldShowNoValues(await DescribeAsync(catalog, WithScopes(ScopeB)));
        ShouldShowNoValues(await DescribeAsync(catalog, WithScopes(ScopeA)));

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
            (Masked: i % 2 == 0, Description: await DescribeAsync(catalog, i % 2 == 0 ? WithScopes(ScopeA) : WithScopes(ScopeB)))));
        foreach (var (masked, description) in results)
        {
            ShouldShowNoValues(description);
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
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new GatewayOptions { Catalog = new Autheris.Domain.Options.DataCatalogOptions { SharedTables = [table.Identifier.ToString()] } }));
        services.AddScoped<Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService, Autheris.Application.Catalog.Services.CatalogVisibilityService>();
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
