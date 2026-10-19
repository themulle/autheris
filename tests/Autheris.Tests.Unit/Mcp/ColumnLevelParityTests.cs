namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>
/// Issue #127 regression gate (FR-127-6, ST-10): for every enumerated combination of base rule, scope rule, caller scopes,
/// consent shape, Deny-effect rule and catalog sensitivity, the level observed through <c>describe_dataset</c> (for a listed column) and through the
/// query analysis equals the level the real data-path resolver produces; a column hidden by catalog visibility is stricter, never looser. The catalog DENY masking rule and access profiles are excluded by construction (they only make the catalog stricter).
/// </summary>
[Trait("Category", "Security")]
public sealed class ColumnLevelParityTests
{
    private static readonly ColumnAccessLevel?[] BaseLevels = [null, ColumnAccessLevel.Clear, ColumnAccessLevel.Mask, ColumnAccessLevel.Deny];
    private static readonly string[][] CallerScopeSets = [[ScopeA], [ScopeB], [], [ScopeA, ScopeB]];
    private static readonly string[] ScopeClaimTypes = ["scp", "scope", "http://schemas.microsoft.com/identity/claims/scope"];

    // Consent shapes: single, two unfiltered, row-filtered + unfiltered, two with different row filters, plus a role grantee.
    private enum Shape { Single, TwoUnfiltered, FilteredPlusUnfilteredMask, TwoFiltered, RoleGrantee }

    private static IEnumerable<(ColumnAccessLevel? Base, ColumnAccessLevel? ScopeRule, string[] Scopes, Shape Shape, bool DenyEffectMask, bool Sensitive, string ClaimType)> Cases()
    {
        foreach (var baseLevel in BaseLevels)
        foreach (var scopeRule in new ColumnAccessLevel?[] { null, ColumnAccessLevel.Clear, ColumnAccessLevel.Mask, ColumnAccessLevel.Deny })
        foreach (var scopes in CallerScopeSets)
        foreach (var shape in Enum.GetValues<Shape>())
        foreach (var denyEffectMask in new[] { false, true })
        foreach (var sensitive in new[] { false, true })
        {
            // The claim spelling only varies on a subset to keep the matrix bounded.
            var claimType = ScopeClaimTypes[(scopes.Length + (int)shape) % ScopeClaimTypes.Length];
            yield return (baseLevel, scopeRule, scopes, shape, denyEffectMask, sensitive, claimType);
        }
    }

    private static IReadOnlyList<ConsentColumnRule> PrimaryRules(ColumnAccessLevel? baseLevel, ColumnAccessLevel? scopeRule)
    {
        var rules = new List<ConsentColumnRule> { Rule("id", ColumnAccessLevel.Clear) };
        if (baseLevel is { } b)
        {
            rules.Add(Rule("grade", b));
        }

        if (scopeRule is { } s)
        {
            rules.Add(Rule("grade", s, ScopeA));
        }

        return rules;
    }

    private static List<Consent> BuildConsents(ColumnAccessLevel? baseLevel, ColumnAccessLevel? scopeRule, Shape shape, bool denyEffectMask)
    {
        var primaryRules = PrimaryRules(baseLevel, scopeRule).ToArray();
        var consents = new List<Consent>();
        switch (shape)
        {
            case Shape.Single:
                consents.Add(Allow(primaryRules));
                break;
            case Shape.TwoUnfiltered:
                consents.Add(Allow(primaryRules));
                consents.Add(Allow(Rule("id", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Mask)));
                break;
            case Shape.FilteredPlusUnfilteredMask:
                consents.Add(Allow(primaryRules).WithFilters(RowFilter("1")));
                consents.Add(Allow(Rule("id", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Mask)));
                break;
            case Shape.TwoFiltered:
                consents.Add(Allow(primaryRules).WithFilters(RowFilter("1")));
                consents.Add(Allow(Rule("id", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear)).WithFilters(RowFilter("2")));
                break;
            case Shape.RoleGrantee:
                consents.Add(Allow(primaryRules).AsRole("analyst"));
                break;
        }

        if (denyEffectMask)
        {
            consents.Add(DenyEffect(Rule("grade", ColumnAccessLevel.Mask, ScopeA)));
        }

        return consents;
    }

    private static ClaimsPrincipal PrincipalFor(string[] scopes, string claimType) =>
        scopes.Length == 0
            ? Principal(roles: ["analyst"])
            : Principal(claimType, string.Join(' ', scopes), roles: ["analyst"]);

    /// <summary>A Deny column rule for the column under test that applies to the caller scopes (hides the column in the catalog, SR15-52).</summary>
    private static bool HasApplicableDenyRule(IEnumerable<Consent> consents, IReadOnlySet<string> scopes) =>
        consents.SelectMany(c => c.ColumnRules).Any(r =>
            r.AccessLevel == ColumnAccessLevel.Deny &&
            r.ColumnName == "grade" &&
            (r.ApplicableScopes.Count == 0 || r.ApplicableScopes.Any(scopes.Contains)));

    /// <summary>Observation class of a level on the profile channel: Deny = no profile or column, Mask = no values, Clear = values.</summary>
    private static ColumnAccessLevel Observe(McpDatasetColumn? column)
    {
        var profile = column?.ValueProfile;
        if (profile == null)
        {
            return ColumnAccessLevel.Deny;
        }

        // A catalog-sensitive column never exposes values, so Clear and Mask look alike there.
        return profile.AllowedValues == null ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear;
    }

    [Fact]
    public async Task Catalog_And_Analysis_Equal_DataPath_ForEveryCombination()
    {
        var mismatches = new List<string>();
        var checkedCases = 0;
        var skipped = 0;
        var skippedReasons = new List<string>();
        var total = 0;

        foreach (var (baseLevel, scopeRule, scopes, shape, denyEffectMask, sensitive, claimType) in Cases())
        {
            total++;
            var table = CreateTable(gradeSensitive: sensitive);
            var consents = BuildConsents(baseLevel, scopeRule, shape, denyEffectMask);
            var principal = PrincipalFor(scopes, claimType);
            var label = $"base={baseLevel?.ToString() ?? "-"} scopeRule={scopeRule?.ToString() ?? "-"} scopes=[{string.Join(',', scopes)}] claim={claimType} shape={shape} denyMask={denyEffectMask} sensitive={sensitive}";

            // Oracle: the real resolver exactly as the data path calls it.
            var scopeSet = TokenAccessScope.GetScopeSet(principal.Claims);
            TableAccessDecision decision;
            try
            {
                decision = new ConsentResolutionService().ResolveAccess(
                    new Sid(UserSid),
                    new HashSet<Sid>(),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "analyst" },
                    Orders,
                    consents,
                    DatabaseDialect.SqlServer,
                    null,
                    scopeSet.Count > 0 ? scopeSet : null);
            }
            catch (Exception ex)
            {
                skippedReasons.Add(label + " :: " + ex.Message);
                skipped++;
                continue; // the oracle itself cannot evaluate this combination (for example an unsupported filter)
            }

            var oracle = decision.IsAllowed ? decision.GetEffectiveColumnAccess("grade", table) : ColumnAccessLevel.Deny;
            checkedCases++;

            // (a) describe_dataset (value profile channel).
            var catalog = CreateCatalog(table, consents);
            McpDatasetDescription description;
            try
            {
                description = await catalog.DescribeDatasetAsync(principal, "sales.public.orders");
            }
            catch (Autheris.Domain.Exceptions.TableNotFoundException)
            {
                // Not visible at all: acceptable only when the data path denies the whole table or the column.
                if (oracle != ColumnAccessLevel.Deny && !HasApplicableDenyRule(consents, scopeSet))
                {
                    mismatches.Add($"{label}: table invisible but oracle={oracle}");
                }

                continue;
            }

            var column = description.Columns.SingleOrDefault(c => c.Name == "grade");
            var observed = Observe(column);
            var expected = oracle == ColumnAccessLevel.Clear && sensitive ? ColumnAccessLevel.Mask : oracle;
            // A column hidden by catalog visibility is stricter than the data path: a Deny column rule of any consent hides it
            // (SR15-52, names only, fail closed). A listed column must carry exactly the data-path level.
            if (column != null && observed != expected)
            {
                mismatches.Add($"{label}: catalog={observed} oracle={oracle}");
            }

            if (column == null && oracle != ColumnAccessLevel.Deny && !HasApplicableDenyRule(consents, scopeSet))
            {
                mismatches.Add($"{label}: column hidden without an applicable Deny rule and oracle={oracle}");
            }

            // (b) query analysis, caller is target.
            var analysis = new QueryAnalysisService(MetadataRepository(table), ConsentRepository(consents));
            var result = await analysis.AnalyzeQueriesAsync(
                [QueryAnalysisItem.FromSql("SELECT id, grade FROM sales.public.orders", name: "q")], principal);
            var predicted = result.ValidationReport.OutputColumns.SingleOrDefault(c => c.Column == "grade")?.EffectiveAccessLevel ?? ColumnAccessLevel.Deny;
            if (predicted != oracle)
            {
                mismatches.Add($"{label}: analysis={predicted} oracle={oracle}");
            }
        }

        skipped.ShouldBe(0, "the oracle must evaluate every enumerated combination: " + skippedReasons.FirstOrDefault());
        checkedCases.ShouldBe(total);
        total.ShouldBe(4 * 4 * 4 * 5 * 2 * 2);
        mismatches.ShouldBeEmpty(string.Join('\n', mismatches.Take(20)));
    }

    /// <summary>Safety property of the impersonated target (BC-4): never more permissive than either evaluation.</summary>
    [Theory]
    [InlineData(ColumnAccessLevel.Clear, ColumnAccessLevel.Mask, ColumnAccessLevel.Mask)]
    [InlineData(ColumnAccessLevel.Clear, ColumnAccessLevel.Deny, ColumnAccessLevel.Deny)]
    [InlineData(null, ColumnAccessLevel.Clear, ColumnAccessLevel.Deny)]
    [InlineData(ColumnAccessLevel.Clear, null, ColumnAccessLevel.Clear)]
    public async Task Analysis_ImpersonatedTarget_IsMinimumOfNoScopeAndAllRuleScopes(
        ColumnAccessLevel? baseLevel, ColumnAccessLevel? scopeRule, ColumnAccessLevel expected)
    {
        var table = CreateTable();
        var consents = new List<Consent> { Allow(PrimaryRules(baseLevel, scopeRule).ToArray()) };
        var service = new QueryAnalysisService(MetadataRepository(table), ConsentRepository(consents));
        var admin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-AUDITOR"), new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "governance:simulate")], "MCP"));

        var result = await service.AnalyzeQueriesAsync(
            [QueryAnalysisItem.FromSql("SELECT id, grade FROM sales.public.orders", name: "q")], admin, Principal());

        (result.ValidationReport.OutputColumns.SingleOrDefault(c => c.Column == "grade")?.EffectiveAccessLevel ?? ColumnAccessLevel.Deny)
            .ShouldBe(expected);
    }
}

internal static class ConsentTestExtensions
{
    public static Consent WithFilters(this Consent consent, params ConsentRowFilter[] filters)
    {
        consent.RowFilters = filters;
        return consent;
    }

    public static Consent AsRole(this Consent consent, string role) => new()
    {
        TableIdentifier = consent.TableIdentifier,
        TenantId = consent.TenantId,
        Effect = consent.Effect,
        GranteeType = GranteeType.Role,
        RoleName = role,
        ValidFrom = consent.ValidFrom,
        ValidTo = consent.ValidTo,
        ColumnRules = consent.ColumnRules
    };
}
