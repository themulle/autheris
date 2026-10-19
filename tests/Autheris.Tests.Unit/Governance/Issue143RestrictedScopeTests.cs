namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>
/// Issue #143 (PLAN-GOV-143 WP-143-1): a catalog-sensitive column is in clear for a holder of a restricted scope (default
/// <c>Agent.Read</c>) only when a consent column rule written for exactly that scope grants <c>Clear</c>.
/// </summary>
public sealed class Issue143RestrictedScopeTests
{
    private const string Restricted = "Agent.Read";
    private static readonly TableMetadata Meta = CreateTable(gradeSensitive: true);
    private static readonly TableIdentifier Table = Orders;

    private static IReadOnlySet<string>? Scopes(params string[] scopes) =>
        scopes.Length == 0 ? null : new HashSet<string>(scopes, StringComparer.OrdinalIgnoreCase);

    private static TableAccessDecision Resolve(
        IReadOnlyList<Consent> consents, string[] scopes, IRestrictedScopePolicy? policy = null, ILogger<ConsentResolutionService>? logger = null) =>
        new ConsentResolutionService(null, policy, logger).ResolveAccess(
            new Sid(UserSid), new HashSet<Sid>(), new HashSet<string>(), Table, consents, callerScopes: Scopes(scopes));

    private static ColumnAccessLevel Level(TableAccessDecision d, string column = "grade") => d.GetEffectiveColumnAccess(column, Meta);

    private static ConsentColumnRule IdClear => Rule("id", ColumnAccessLevel.Clear);

    private static IRestrictedScopePolicy PolicyOf(params string[] scopes) =>
        RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions { Scopes = [.. scopes] });

    // AC-1
    [Fact]
    public void BaseClearOnly_RestrictedToken_Masks_UnrestrictedAndScopeless_Clear()
    {
        var consents = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear)) };

        Level(Resolve(consents, [Restricted])).ShouldBe(ColumnAccessLevel.Mask);
        Level(Resolve(consents, ["other.scope"])).ShouldBe(ColumnAccessLevel.Clear);
        Level(Resolve(consents, [])).ShouldBe(ColumnAccessLevel.Clear);
    }

    // AC-2
    [Fact]
    public void BaseAndScopeClear_BothTokensClear()
    {
        var consents = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, Restricted)) };

        Level(Resolve(consents, [Restricted])).ShouldBe(ColumnAccessLevel.Clear);
        Level(Resolve(consents, [])).ShouldBe(ColumnAccessLevel.Clear);
    }

    // AC-3
    [Theory]
    [InlineData("Agent.Read|scope-a", "scope-a", false)]
    [InlineData("Agent.Read|scope-a", "Agent.Read", true)]
    [InlineData("Agent.Read|scope-a", "Agent.Read|scope-a", true)]
    [InlineData("scope-a|scope-b", "scope-a", true)]
    public void MultiScope_RestrictedWins(string tokenScopes, string clearRuleScopes, bool expectedClear)
    {
        var consents = new[]
        {
            Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, clearRuleScopes.Split('|')))
        };

        var level = Level(Resolve(consents, tokenScopes.Split('|')));
        level.ShouldBe(expectedClear ? ColumnAccessLevel.Clear : ColumnAccessLevel.Mask);
    }

    // AC-4
    [Fact]
    public void MonotonicHardening_Unchanged()
    {
        var hardened = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, Restricted), Rule("grade", ColumnAccessLevel.Mask, Restricted)) };
        Level(Resolve(hardened, [Restricted])).ShouldBe(ColumnAccessLevel.Mask);

        var scopeOnly = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear, Restricted)) };
        Level(Resolve(scopeOnly, [Restricted])).ShouldBe(ColumnAccessLevel.Clear);
        Level(Resolve(scopeOnly, [])).ShouldBe(ColumnAccessLevel.Deny);
        Level(Resolve(scopeOnly, ["scope-a"])).ShouldBe(ColumnAccessLevel.Deny);
    }

    // AC-5
    [Fact]
    public void NonSensitiveColumn_Unchanged()
    {
        var d = Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], [Restricted]);
        Level(d, "id").ShouldBe(ColumnAccessLevel.Clear);
    }

    // AC-6
    [Fact]
    public void RowGroups_EveryGroupMustSupplyScopeClear()
    {
        Consent Group(string id, bool scopeClear)
        {
            var c = scopeClear
                ? Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, Restricted))
                : Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear));
            c.RowFilters = [RowFilter(id)];
            return c;
        }

        Level(Resolve([Group("1", true), Group("2", false)], [Restricted])).ShouldBe(ColumnAccessLevel.Mask);
        Level(Resolve([Group("1", true), Group("2", true)], [Restricted])).ShouldBe(ColumnAccessLevel.Clear);
        Level(Resolve([Group("1", false), Group("2", false)], [])).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void ConsentWithoutColumnRules_DoesNotSupplyScopeClear()
    {
        var consents = new[] { Allow(), Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear)) };

        var d = Resolve(consents, [Restricted]);
        Level(d).ShouldBe(ColumnAccessLevel.Mask);
        Level(Resolve(consents, [])).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void DenyEffectConsent_DoesNotSupply()
    {
        var consents = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear)), DenyEffect(Rule("grade", ColumnAccessLevel.Clear, Restricted)) };

        Level(Resolve(consents, [Restricted])).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void RuleWithPrefixedScope_DoesNotClear()
    {
        var consents = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, "api://x/Agent.Read")) };

        Level(Resolve(consents, [Restricted])).ShouldBe(ColumnAccessLevel.Mask);
    }

    // SEC-GOV143-03 / A-2 (ii): a bare rule does not clear for a token that carries the prefixed form, but the caller is still restricted.
    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_05_PrefixedTokenScope_BareRule_DoesNotClear_PrefixedRule_NotApplied()
    {
        var bare = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, Restricted)) };
        var d = Resolve(bare, ["api://x/Agent.Read"]);
        d.ScopeClearance.ShouldNotBeNull();
        d.ScopeClearance!.RestrictedScopesHeld.ShouldContain(Restricted);
        Level(d).ShouldBe(ColumnAccessLevel.Mask);

        var prefixedRule = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Mask, "api://x/Agent.Read")) };
        Level(Resolve(prefixedRule, [Restricted])).ShouldBe(ColumnAccessLevel.Mask);
        Resolve(prefixedRule, [Restricted]).GetColumnAccess("grade").ShouldBe(ColumnAccessLevel.Clear); // rule not applied to the bare token scope
    }

    [Fact]
    public void PrefixedTokenScope_BareRule_DoesNotClear()
    {
        var consents = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear, Restricted)) };

        Level(Resolve(consents, ["api://x/Agent.Read"])).ShouldBe(ColumnAccessLevel.Deny);
    }

    // SEC-GOV143-03 / S-3
    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_04_Restriction_NeverRaises_NorTouchesDeny()
    {
        var denied = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Deny), Rule("grade", ColumnAccessLevel.Clear, Restricted)) };
        var d = Resolve(denied, [Restricted]);
        Level(d).ShouldBe(ColumnAccessLevel.Deny);
        d.ScopeClearance!.ClearedColumns.ShouldNotContain("grade");

        var hardDeny = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear, Restricted)), DenyEffect(Rule("grade", ColumnAccessLevel.Deny, Restricted)) };
        var d2 = Resolve(hardDeny, [Restricted]);
        Level(d2).ShouldBe(ColumnAccessLevel.Deny);
        d2.ScopeClearance!.ClearedColumns.ShouldNotContain("grade");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ScopeClear_OnBaseMaskColumn_NotInClearedColumns()
    {
        var consents = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Mask), Rule("grade", ColumnAccessLevel.Clear, Restricted)) };

        var d = Resolve(consents, [Restricted]);
        d.GetColumnAccess("grade").ShouldBe(ColumnAccessLevel.Mask);
        d.ScopeClearance!.ClearedColumns.ShouldNotContain("grade");
        Level(d).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ClearedColumns_SubsetOfClearLevels()
    {
        var rnd = new Random(143);
        string[] columns = ["grade", "id", "email"];
        ColumnAccessLevel[] levels = [ColumnAccessLevel.Clear, ColumnAccessLevel.Mask, ColumnAccessLevel.Deny];
        string[][] scopeSets = [[], [Restricted], ["scope-a"], [Restricted, "scope-a"]];

        for (var i = 0; i < 300; i++)
        {
            var consents = new List<Consent>();
            for (var c = 0; c < rnd.Next(1, 4); c++)
            {
                var rules = new List<ConsentColumnRule>();
                for (var r = 0; r < rnd.Next(0, 6); r++)
                {
                    rules.Add(Rule(columns[rnd.Next(columns.Length)], levels[rnd.Next(levels.Length)], scopeSets[rnd.Next(scopeSets.Length)]));
                }

                var consent = rnd.Next(5) == 0 ? DenyEffect([.. rules]) : Allow([.. rules]);
                if (rnd.Next(3) == 0)
                {
                    consent.RowFilters = [RowFilter(rnd.Next(1, 3).ToString())];
                }

                consents.Add(consent);
            }

            var d = Resolve(consents, [Restricted, "scope-a"]);
            if (d.ScopeClearance is null)
            {
                d.IsAllowed.ShouldBeFalse();
                continue;
            }

            foreach (var column in d.ScopeClearance.ClearedColumns)
            {
                d.ColumnAccess.TryGetValue(column, out var level).ShouldBeTrue();
                level.ShouldBe(ColumnAccessLevel.Clear);
            }
        }
    }

    // SEC-GOV143-01
    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_01_ResolutionFault_MasksAllSensitive_NeverNullClearance()
    {
        var policy = Substitute.For<IRestrictedScopePolicy>();
        policy.Scopes.Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Restricted });
        policy.HeldBy(Arg.Any<IReadOnlySet<string>?>()).Returns(_ => throw new InvalidOperationException("SECRET-FAULT-DETAIL"));
        var logger = new CapturingLogger();
        var consents = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, Restricted)) };

        var d = Resolve(consents, [Restricted], policy, logger);

        d.IsAllowed.ShouldBeTrue();
        d.ScopeClearance.ShouldNotBeNull();
        d.ScopeClearance!.RestrictedScopesHeld.ShouldNotBeEmpty();
        d.ScopeClearance.ClearedColumns.ShouldBeEmpty();
        Level(d).ShouldBe(ColumnAccessLevel.Mask);
        Level(d, "id").ShouldBe(ColumnAccessLevel.Clear);
        logger.Messages.ShouldContain(m => m.Contains(nameof(InvalidOperationException)));
        logger.Messages.ShouldNotContain(m => m.Contains("SECRET-FAULT-DETAIL"));
    }

    [Fact]
    public void ClearanceFailure_MasksAllSensitive()
    {
        var policy = Substitute.For<IRestrictedScopePolicy>();
        policy.Scopes.Returns(_ => throw new InvalidOperationException("boom"));
        policy.HeldBy(Arg.Any<IReadOnlySet<string>?>()).Returns(_ => throw new InvalidOperationException("boom"));

        var d = Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], [Restricted], policy);

        d.ScopeClearance.ShouldNotBeNull();
        d.ScopeClearance!.RestrictedScopesHeld.ShouldNotBeEmpty();
        Level(d).ShouldBe(ColumnAccessLevel.Mask);
    }

    // T-GOV143-06
    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_06_MultiScope_OneScopeWithoutClear_Masks()
    {
        var policy = PolicyOf("Agent.Read", "Data.Restricted");
        string[] token = ["Agent.Read", "Data.Restricted"];

        var onlyOne = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, "Agent.Read")) };
        Level(Resolve(onlyOne, token, policy)).ShouldBe(ColumnAccessLevel.Mask);

        var both = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, "Agent.Read"), Rule("grade", ColumnAccessLevel.Clear, "Data.Restricted")) };
        Level(Resolve(both, token, policy)).ShouldBe(ColumnAccessLevel.Clear);

        var sharedRule = new[] { Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear), Rule("grade", ColumnAccessLevel.Clear, "Agent.Read", "Data.Restricted")) };
        Level(Resolve(sharedRule, token, policy)).ShouldBe(ColumnAccessLevel.Clear);

        // A holder of only one of the two restricted scopes needs only that scope's clear.
        Level(Resolve(onlyOne, ["Agent.Read"], policy)).ShouldBe(ColumnAccessLevel.Clear);
    }

    // T-GOV143-09 (SEC-GOV143-05)
    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Agent.Read", "Agent.Read")]
    [InlineData("api://app/Agent.Read", "Agent.Read")]
    [InlineData("AGENT.read", "AGENT.read")]
    [InlineData("api://app/", "api://app/")]
    [InlineData("/", "/")]
    [InlineData("", "")]
    public void T_GOV143_09_ToBareName(string input, string expected) => ScopeNames.ToBareName(input).ShouldBe(expected);

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Agent.Read", true)]
    [InlineData("api://other/Agent.Read", true)]
    [InlineData("agent.read", true)]
    [InlineData("Agent.Read.All", false)]
    [InlineData("", false)]
    [InlineData("api://x/", false)]
    [InlineData(" ", false)]
    public void T_GOV143_09_Normalisation_CaseAndPrefix_MatchesRestricted_EmptyBareNeverMatches(string tokenScope, bool restricted)
    {
        var policy = PolicyOf("api://app/Agent.Read", "");

        policy.Scopes.ShouldBe(["Agent.Read"], ignoreOrder: true);
        policy.HeldBy(Scopes(tokenScope, "other")).Any().ShouldBe(restricted);
        RestrictedScopePolicy.Default.HeldBy(Scopes(tokenScope)).Any().ShouldBe(restricted);
    }

    [Fact]
    public void Policy_FingerprintIsStableAcrossOrderAndForm_OffWhenDisabled()
    {
        PolicyOf("A.One", "api://x/B.Two").Fingerprint.ShouldBe(PolicyOf("b.two", "a.one", "A.One").Fingerprint);
        PolicyOf("A.One").Fingerprint.ShouldNotBe(PolicyOf("A.One", "B.Two").Fingerprint);
        RestrictedScopePolicy.Disabled.Fingerprint.ShouldBe("off");
        RestrictedScopePolicy.Disabled.Scopes.ShouldBeEmpty();
        RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions { Enabled = false }).ShouldBeSameAs(RestrictedScopePolicy.Disabled);
        RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions()).ShouldBeSameAs(RestrictedScopePolicy.Default);
        RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions { Scopes = [] }).ShouldBeSameAs(RestrictedScopePolicy.Default);
    }

    [Fact]
    public void DisabledPolicy_NoClearance_BaseClearIsClear()
    {
        var d = Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], [Restricted], RestrictedScopePolicy.Disabled);

        d.ScopeClearance.ShouldBeNull();
        Level(d).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void ParameterlessResolver_UsesDefaultList()
    {
        var d = new ConsentResolutionService().ResolveAccess(
            new Sid(UserSid), new HashSet<Sid>(), new HashSet<string>(), Table,
            [Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], callerScopes: Scopes(Restricted));

        Level(d).ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public void CustomList_WithoutAgentRead_DoesNotRestrictAgentRead()
    {
        var d = Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], [Restricted], PolicyOf("Data.Restricted"));

        d.ScopeClearance.ShouldBeNull();
        Level(d).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public void Denied_Decision_HasNoClearance()
    {
        Resolve([], [Restricted]).ScopeClearance.ShouldBeNull();
    }

    [Fact]
    public void IsMaskedByScopeRestriction_OnlyForSensitiveExplicitClearNotPermitted()
    {
        var d = Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], [Restricted]);

        d.IsMaskedByScopeRestriction("grade", Meta).ShouldBeTrue();
        d.IsMaskedByScopeRestriction("id", Meta).ShouldBeFalse();
        Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], []).IsMaskedByScopeRestriction("grade", Meta).ShouldBeFalse();
        TableAccessDecision.IsCatalogSensitive("grade", Meta).ShouldBeTrue();
        TableAccessDecision.IsCatalogSensitive("id", Meta).ShouldBeFalse();
        TableAccessDecision.IsCatalogSensitive("grade", null).ShouldBeFalse();
    }

    [Fact]
    public void Clearance_Permits_IsCaseInsensitive_AndUnknownFactory()
    {
        var c = new RestrictedScopeClearance(new HashSet<string> { Restricted }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Grade" });
        c.Permits("grade").ShouldBeTrue();
        c.Permits("id").ShouldBeFalse();

        var unknown = RestrictedScopeClearance.Unknown(RestrictedScopePolicy.Default)!;
        unknown.RestrictedScopesHeld.ShouldContain(Restricted);
        unknown.ClearedColumns.ShouldBeEmpty();
        RestrictedScopeClearance.Unknown(RestrictedScopePolicy.Disabled).ShouldBeNull();
    }

    // AC-9 (decision level) and decision copies
    [Fact]
    public void DecisionCopies_KeepClearance()
    {
        var d = Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], [Restricted]);

        // An access profile with Unmasked writes Clear into the column map (TableAccessPolicy.ApplyProfileToDecision); the restriction still masks.
        var unmasked = d with { ColumnAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["grade"] = ColumnAccessLevel.Clear, ["id"] = ColumnAccessLevel.Clear } };
        Level(unmasked).ShouldBe(ColumnAccessLevel.Mask);

        Level(d.WithAdditionalRowFilter("1 = 1")).ShouldBe(ColumnAccessLevel.Mask);
        d.WithAdditionalRowFilter("1 = 1").ScopeClearance.ShouldBeSameAs(d.ScopeClearance);
    }

    // T-GOV143-20 (cache-hit part)
    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_20_UnknownClearance_SurvivesDecisionCopiesAndEnvelopeRoundTrip()
    {
        var d = TableAccessDecision.Allowed(Table, new Dictionary<string, ColumnAccessLevel> { ["grade"] = ColumnAccessLevel.Clear }) with
        {
            ScopeClearance = RestrictedScopeClearance.Unknown(RestrictedScopePolicy.Default)
        };

        Level(d with { CombinedRowFilterSql = "1=1" }).ShouldBe(ColumnAccessLevel.Mask);
        var restored = CachedConsentEnvelope.FromDecision(d, 1).ToDecision(RestrictedScopePolicy.Default.Fingerprint);
        restored.ShouldNotBeNull();
        restored!.ScopeClearance.ShouldNotBeNull();
        restored.ScopeClearance!.RestrictedScopesHeld.ShouldContain(Restricted);
        Level(restored).ShouldBe(ColumnAccessLevel.Mask);
    }

    // T-GOV143-15 (SEC-GOV143-10)
    [Fact]
    [Trait("Category", "Security")]
    public void T_GOV143_15_AppOnlyToken_Unrestricted_GetScopesUnchanged()
    {
        var appOnly = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Data.Read")], "Bearer"));
        TokenAccessScope.GetScopes(appOnly).ShouldBeEmpty();
        TokenAccessScope.GetScopeSet(appOnly.Claims).ShouldBeEmpty();
        RestrictedScopePolicy.Default.HeldBy(TokenAccessScope.GetScopeSet(appOnly.Claims)).ShouldBeEmpty();
        Level(Resolve([Allow(IdClear, Rule("grade", ColumnAccessLevel.Clear))], [])).ShouldBe(ColumnAccessLevel.Clear);

        // GetScopes stays scp-only (a plain "scope" claim must not make an app-only token look delegated); the claims overload also reads "scope".
        var plainScope = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", "Agent.Read")], "Bearer"));
        TokenAccessScope.GetScopes(plainScope).ShouldBeEmpty();
        TokenAccessScope.GetScopeSet(plainScope.Claims).ShouldContain("Agent.Read");

        var delegated = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scp", "Agent.Read other")], "Bearer"));
        TokenAccessScope.GetScopes(delegated).ShouldBe(["Agent.Read", "other"]);
    }

    private sealed class CapturingLogger : ILogger<ConsentResolutionService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : "|EXC:" + exception.GetType().Name));
    }
}
