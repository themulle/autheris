using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Tests.Unit.TestSupport;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Issue #806: re-applying an unchanged manifest reports <c>unchanged</c> and writes nothing, also for columns with several rules; every real
/// difference still takes the revoke/create path (SEC-806-1).
/// </summary>
public sealed class DeclarativeConsentApplyIdempotencyTests
{
    private const string Group = "S-1-5-21-GROUP-DS";
    private static readonly TableIdentifier Orders = new("default", "sales", "orders");
    private static readonly DateTimeOffset Until = DateTimeOffset.UtcNow.AddDays(30);

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "test"));

    private static ITableMetadataRepository Tables()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Autheris.Domain.Model.Table { Id = Guid.NewGuid() },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "email" }, new TableColumn { ColumnName = "region" }]
            });
        return repo;
    }

    private static DeclarativeConsentPolicy Policy(
        IReadOnlyList<DeclarativeColumnRule>? rules = null,
        IReadOnlyList<DeclarativeRowFilter>? filters = null,
        DateTimeOffset? until = null) =>
        new("default", "sales", "orders", "Group", Group, "Allow", rules, filters, ValidUntil: until ?? Until);

    private static DeclarativeColumnRule Rule(string column, string level, params string[] scopes) =>
        new(column, level, scopes.Length == 0 ? null : scopes);

    private static DeclarativeRowFilter Filter(string column, string op, string valueJson, string valueType = "string") =>
        new(column, op, valueType, valueJson);

    private static readonly IReadOnlyList<DeclarativeColumnRule> BaseAndScopeRules =
    [
        Rule("id", "Clear"),
        Rule("email", "Clear"),
        Rule("email", "Mask", "Agent.Read"),
    ];

    // One rule per column: a difference elsewhere is the only reason for a re-apply (the old code already churned on several rules per column).
    private static readonly IReadOnlyList<DeclarativeColumnRule> SingleRules = [Rule("id", "Clear"), Rule("email", "Mask")];

    private static async Task<(Harness Harness, DeclarativeApplyResult Second)> ApplyTwiceAsync(DeclarativeConsentPolicy first, DeclarativeConsentPolicy second)
    {
        var h = new Harness();
        (await h.ApplyAsync(first)).Applied.ShouldBe(1);
        h.Repo.ReplaceCalls.ShouldBe(1);
        h.Audit.ClearReceivedCalls();
        return (h, await h.ApplyAsync(second));
    }

    private static void ShouldBeUnchanged(Harness h, DeclarativeApplyResult second)
    {
        second.Applied.ShouldBe(0);
        second.Unchanged.ShouldBe(1);
        second.Revoked.ShouldBe(0);
        second.Updated.ShouldBe(0);
        h.Repo.ReplaceCalls.ShouldBe(1);
        h.Repo.ReplaceAuditEntries.Count.ShouldBe(1);
        h.Audit.ReceivedCalls().ShouldBeEmpty();
        h.Repo.Active(DateTimeOffset.UtcNow).Count.ShouldBe(1);
    }

    private static void ShouldBeApplied(Harness h, DeclarativeApplyResult second)
    {
        second.Applied.ShouldBe(1);
        second.Unchanged.ShouldBe(0);
        second.Revoked.ShouldBe(1);
        h.Repo.ReplaceCalls.ShouldBe(2);
        h.Repo.Active(DateTimeOffset.UtcNow).Count.ShouldBe(1);
    }

    [Fact]
    public async Task BaseAndScopeRuleOnSameColumn_ReApplied_IsUnchanged_AndWritesNothing()
    {
        var (h, second) = await ApplyTwiceAsync(Policy(BaseAndScopeRules), Policy(BaseAndScopeRules));

        ShouldBeUnchanged(h, second);
    }

    [Fact]
    public async Task RulesAndScopesInDifferentOrder_AreUnchanged()
    {
        var first = Policy([Rule("email", "Clear"), Rule("email", "Mask", "Agent.Read", "Report.Read"), Rule("id", "Clear")]);
        var reordered = Policy([Rule("id", "Clear"), Rule("email", "Mask", "Report.Read", "Agent.Read"), Rule("email", "Clear")]);

        var (h, second) = await ApplyTwiceAsync(first, reordered);

        ShouldBeUnchanged(h, second);
    }

    [Fact]
    public async Task DuplicateScopesInOneRule_AreComparedAsSet()
    {
        var first = Policy([Rule("email", "Clear"), Rule("email", "Mask", "Agent.Read", "Agent.Read")]);
        var deduplicated = Policy([Rule("email", "Clear"), Rule("email", "Mask", "Agent.Read")]);

        var (h, second) = await ApplyTwiceAsync(first, deduplicated);

        ShouldBeUnchanged(h, second);
    }

    [Fact]
    public async Task SameRuleTwice_ReApplied_IsUnchanged()
    {
        var policy = Policy([Rule("email", "Mask"), Rule("email", "Mask")]);

        var (h, second) = await ApplyTwiceAsync(policy, policy);

        ShouldBeUnchanged(h, second);
    }

    [Fact]
    public async Task TwoRowFiltersOnSameColumn_ReApplied_AreUnchanged()
    {
        var policy = Policy(SingleRules, [Filter("region", "NEQ", "[\"US\"]"), Filter("region", "NEQ", "[\"CN\"]")]);

        var (h, second) = await ApplyTwiceAsync(policy, policy);

        ShouldBeUnchanged(h, second);
    }

    [Fact]
    public async Task TwoRowFiltersOnSameColumn_ReApplied_AreUnchanged_InAnyOrder()
    {
        var first = Policy(SingleRules, [Filter("region", "NEQ", "[\"US\"]"), Filter("region", "NEQ", "[\"CN\"]")]);
        var reordered = Policy(SingleRules, [Filter("region", "NEQ", "[\"CN\"]"), Filter("region", "NEQ", "[\"US\"]")]);

        var (h, second) = await ApplyTwiceAsync(first, reordered);

        ShouldBeUnchanged(h, second);
    }

    [Fact]
    public async Task GroupPolicy_LaterValidUntil_IsUnchanged_ReApplyNeverExtends()
    {
        var (h, second) = await ApplyTwiceAsync(Policy(BaseAndScopeRules), Policy(BaseAndScopeRules, until: Until.AddDays(5)));

        ShouldBeUnchanged(h, second);
        h.Repo.Active(DateTimeOffset.UtcNow).Single().ValidTo.ShouldBe(Until);
    }

    public static TheoryData<string, IReadOnlyList<DeclarativeColumnRule>, IReadOnlyList<DeclarativeColumnRule>> ColumnRuleChanges => new()
    {
        { "scope added", BaseAndScopeRules, [Rule("id", "Clear"), Rule("email", "Clear"), Rule("email", "Mask", "Agent.Read", "Report.Read")] },
        { "scope replaced", BaseAndScopeRules, [Rule("id", "Clear"), Rule("email", "Clear"), Rule("email", "Mask", "Report.Read")] },
        { "scope case changed", BaseAndScopeRules, [Rule("id", "Clear"), Rule("email", "Clear"), Rule("email", "Mask", "agent.read")] },
        { "scope removed (rule becomes a base rule)", BaseAndScopeRules, [Rule("id", "Clear"), Rule("email", "Clear"), Rule("email", "Mask")] },
        { "scope rule removed", BaseAndScopeRules, [Rule("id", "Clear"), Rule("email", "Clear"), Rule("id", "Clear")] },
        { "level of the second rule changed", BaseAndScopeRules, [Rule("id", "Clear"), Rule("email", "Clear"), Rule("email", "Deny", "Agent.Read")] },
        { "level of the base rule changed", BaseAndScopeRules, [Rule("id", "Clear"), Rule("email", "Mask"), Rule("email", "Mask", "Agent.Read")] },
        { "column renamed by case", BaseAndScopeRules, [Rule("id", "Clear"), Rule("Email", "Clear"), Rule("email", "Mask", "Agent.Read")] },
        // The old first-match lookup paired both desired rules with the stored Mask rule and missed the looser stored Clear base rule.
        { "stored looser duplicate base rule", [Rule("email", "Mask"), Rule("email", "Clear")], [Rule("email", "Mask"), Rule("email", "Mask")] },
        // The old scope check (count plus 'every stored scope is desired') missed that the stored rule does not cover Report.Read.
        { "stored duplicate scope against a new scope", [Rule("email", "Clear"), Rule("email", "Mask", "Agent.Read", "Agent.Read")], [Rule("email", "Clear"), Rule("email", "Mask", "Agent.Read", "Report.Read")] },
        { "duplicate rule collapsed", [Rule("email", "Mask"), Rule("email", "Mask"), Rule("id", "Clear")], [Rule("email", "Mask"), Rule("id", "Clear"), Rule("id", "Clear")] },
    };

    [Theory]
    [Trait("Category", "Security")]
    [MemberData(nameof(ColumnRuleChanges))]
    public async Task ColumnRuleDifference_IsNeverTreatedAsUnchanged(string change, IReadOnlyList<DeclarativeColumnRule> stored, IReadOnlyList<DeclarativeColumnRule> desired)
    {
        var (h, second) = await ApplyTwiceAsync(Policy(stored), Policy(desired));

        ShouldBeApplied(h, second);
        h.Repo.Active(DateTimeOffset.UtcNow).Single().ColumnRules.Count.ShouldBe(desired.Count, change);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OneRuleRemoved_IsApplied()
    {
        var (h, second) = await ApplyTwiceAsync(Policy(BaseAndScopeRules), Policy([Rule("id", "Clear"), Rule("email", "Clear")]));

        ShouldBeApplied(h, second);
        h.Repo.Active(DateTimeOffset.UtcNow).Single().ColumnRules.ShouldNotContain(r => r.ApplicableScopes.Count > 0);
    }

    public static TheoryData<string, IReadOnlyList<DeclarativeRowFilter>, IReadOnlyList<DeclarativeRowFilter>> RowFilterChanges => new()
    {
        // The old first-match lookup compared both desired filters with the first stored one.
        { "second filter on the same column changed", [Filter("region", "NEQ", "[\"US\"]"), Filter("region", "NEQ", "[\"CN\"]")], [Filter("region", "NEQ", "[\"US\"]"), Filter("region", "NEQ", "[\"US\"]")] },
        { "filter value changed", [Filter("region", "NEQ", "[\"US\"]"), Filter("region", "NEQ", "[\"CN\"]")], [Filter("region", "NEQ", "[\"US\"]"), Filter("region", "NEQ", "[\"RU\"]")] },
        { "value type changed", [Filter("region", "EQ", "[\"1\"]")], [Filter("region", "EQ", "[\"1\"]", valueType: "int")] },
        { "operator changed", [Filter("region", "EQ", "[\"EU\"]")], [Filter("region", "NEQ", "[\"EU\"]")] },
        { "value source changed", [Filter("region", "EQ", "[]")], [new DeclarativeRowFilter("region", "EQ", "string", "[]", "USER_ATTRIBUTE", "region")] },
        { "user attribute changed", [new DeclarativeRowFilter("region", "EQ", "string", "[]", "USER_ATTRIBUTE", "department")], [new DeclarativeRowFilter("region", "EQ", "string", "[]", "USER_ATTRIBUTE", "region")] },
        { "filter removed", [Filter("region", "EQ", "[\"EU\"]"), Filter("id", "GT", "[\"0\"]", valueType: "int")], [Filter("region", "EQ", "[\"EU\"]")] },
    };

    [Theory]
    [Trait("Category", "Security")]
    [MemberData(nameof(RowFilterChanges))]
    public async Task RowFilterDifference_IsNeverTreatedAsUnchanged(string change, IReadOnlyList<DeclarativeRowFilter> stored, IReadOnlyList<DeclarativeRowFilter> desired)
    {
        var (h, second) = await ApplyTwiceAsync(Policy(SingleRules, stored), Policy(SingleRules, desired));

        ShouldBeApplied(h, second);
        h.Repo.Active(DateTimeOffset.UtcNow).Single().RowFilters.Count.ShouldBe(desired.Count, change);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GroupPolicy_EarlierValidUntil_IsApplied()
    {
        var earlier = Until.AddDays(-10);

        var (h, second) = await ApplyTwiceAsync(Policy(SingleRules), Policy(SingleRules, until: earlier));

        ShouldBeApplied(h, second);
        h.Repo.Active(DateTimeOffset.UtcNow).Single().ValidTo.ShouldBe(earlier);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExistingConsentWithWriteRules_IsNotTreatedAsUnchanged()
    {
        var h = new Harness();
        await h.Repo.CreateConsentAsync(new Consent
        {
            TableIdentifier = Orders,
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid(Group),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = Until,
            GrantSource = ConsentGrantSource.ConsentRequest,
            WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows)],
        });

        var result = await h.ApplyAsync(Policy());

        result.Unchanged.ShouldBe(0);
        result.Applied.ShouldBe(1);
        result.Revoked.ShouldBe(1);
        h.Repo.Active(DateTimeOffset.UtcNow).Single().WriteRules.ShouldBeEmpty();
    }

    // A consent as GovernanceRepository returns it: scopes split and trimmed, rules and filters in storage order, value source filled in.
    private static Consent Stored(IReadOnlyList<ConsentColumnRule> rules, IReadOnlyList<ConsentRowFilter>? filters = null) => new()
    {
        TableIdentifier = Orders,
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.Group,
        GranteeSid = new Sid(Group),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = Until,
        GrantSource = ConsentGrantSource.Declarative,
        ColumnRules = rules,
        RowFilters = filters ?? [],
    };

    private static ConsentColumnRule StoredRule(string column, ColumnAccessLevel level, params string[] scopes) =>
        new() { ColumnName = column, AccessLevel = level, ApplicableScopes = scopes };

    private static ConsentRowFilter StoredFilter(string column, string op, string valueJson, int filterGroup = 0) =>
        new() { ColumnName = column, Operator = op, ValueType = "string", ValueJson = valueJson, ValueSource = "LITERAL", FilterGroup = filterGroup };

    // Stored form of SingleRules, in reverse order.
    private static IReadOnlyList<ConsentColumnRule> StoredSingleRules() => [StoredRule("email", ColumnAccessLevel.Mask), StoredRule("id", ColumnAccessLevel.Clear)];

    private static Consent PatternGrant() => new()
    {
        TableIdentifier = new TableIdentifier("default", "sales", "items"),
        ObjectPattern = "default.sales.*",
        DataSource = "default",
        ColumnSetMode = ColumnSetMode.AllNonHidden,
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.Group,
        GranteeSid = new Sid(Group),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = Until,
        GrantSource = ConsentGrantSource.ConsentRequest,
    };

    [Fact]
    public async Task ConsentInDatabaseForm_ReverseOrder_TrimmedAndDuplicateScopes_IsUnchanged()
    {
        var h = new Harness();
        await h.Repo.CreateConsentAsync(Stored(
            [StoredRule("email", ColumnAccessLevel.Mask, "Agent.Read"), StoredRule("email", ColumnAccessLevel.Clear), StoredRule("id", ColumnAccessLevel.Clear)],
            [StoredFilter("region", "NEQ", "[\"CN\"]"), StoredFilter("region", "NEQ", "[\"US\"]")]));

        var result = await h.ApplyAsync(Policy(
            [Rule("id", "Clear"), Rule("email", "Clear"), Rule("email", "Mask", " Agent.Read", "Agent.Read")],
            [Filter("region", "NEQ", "[\"US\"]"), new DeclarativeRowFilter("region", "NEQ", "string", "[\"CN\"]", ValueSource: null)]));

        result.Unchanged.ShouldBe(1);
        result.Applied.ShouldBe(0);
        result.Revoked.ShouldBe(0);
        h.Repo.ReplaceCalls.ShouldBe(0);
        h.Audit.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task StoredFilterInAnotherFilterGroup_IsApplied()
    {
        var h = new Harness();
        await h.Repo.CreateConsentAsync(Stored(StoredSingleRules(), [StoredFilter("region", "EQ", "[\"EU\"]", filterGroup: 1)]));

        var result = await h.ApplyAsync(Policy(SingleRules, [Filter("region", "EQ", "[\"EU\"]")]));

        result.Unchanged.ShouldBe(0);
        result.Applied.ShouldBe(1);
        result.Revoked.ShouldBe(1);
        h.Repo.Active(DateTimeOffset.UtcNow).Single().RowFilters.Single().FilterGroup.ShouldBe(0);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("row scope")]
    [InlineData("classification access")]
    public async Task ExistingConsentWithRowScopeOrClassificationAccess_IsNotTreatedAsUnchanged(string kind)
    {
        var h = new Harness();
        var existing = Stored(StoredSingleRules());
        if (kind == "row scope")
        {
            existing.RowScope = [new GrantRowScopeEntry("own_region", new Dictionary<string, JsonElement>(StringComparer.Ordinal))];
        }
        else
        {
            existing.ClassificationAccess =
            [
                new ConsentClassificationAccess { TenantId = Autheris.Tests.Common.TestTenants.DefaultId, ConsentId = existing.Id, Code = "PII", AccessLevel = ColumnAccessLevel.Clear },
            ];
        }

        await h.Repo.CreateConsentAsync(existing);

        var result = await h.ApplyAsync(Policy(SingleRules));

        result.Unchanged.ShouldBe(0, kind);
        result.Applied.ShouldBe(1);
        result.Revoked.ShouldBe(1);
        var active = h.Repo.Active(DateTimeOffset.UtcNow).Single();
        active.RowScope.ShouldBeEmpty();
        active.ClassificationAccess.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PatternGrantOfTheGrantee_IsNeverTheIdenticalConsent()
    {
        // The subject read returns the grantee's pattern grants of the whole domain; an Allow pattern grant without rules (even for another
        // table) must not make the policy 'unchanged', or no grant for the table would be created.
        var h = new Harness();
        var pattern = PatternGrant();
        await h.Repo.CreateConsentAsync(pattern);

        var result = await h.ApplyAsync(Policy());

        result.Unchanged.ShouldBe(0);
        result.Applied.ShouldBe(1);
        result.Revoked.ShouldBe(0);
        var active = h.Repo.Active(DateTimeOffset.UtcNow);
        active.Count.ShouldBe(2);
        active.ShouldContain(c => c.Id == pattern.Id);
        active.ShouldContain(c => !c.IsPatternGrant && c.TableIdentifier.Equals(Orders));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ChangedPolicy_NeverRevokesAPatternGrantOfTheGrantee()
    {
        var h = new Harness();
        var pattern = PatternGrant();
        await h.Repo.CreateConsentAsync(pattern);
        (await h.ApplyAsync(Policy(SingleRules))).Applied.ShouldBe(1);

        var result = await h.ApplyAsync(Policy(BaseAndScopeRules));

        result.Applied.ShouldBe(1);
        result.Revoked.ShouldBe(1);
        var active = h.Repo.Active(DateTimeOffset.UtcNow);
        active.Count.ShouldBe(2);
        active.ShouldContain(c => c.Id == pattern.Id);
        active.Single(c => !c.IsPatternGrant).ColumnRules.Count.ShouldBe(BaseAndScopeRules.Count);
    }

    // Declared last: the security test inventory scanner attributes the methods after a nested type to that type.
    private sealed class Harness
    {
        public FakeConsentRepository Repo { get; } = new();
        public IAuditLogRepository Audit { get; } = Substitute.For<IAuditLogRepository>();
        private DeclarativeConsentApplyService Service => new(Tables(), Repo, Audit);

        public Task<DeclarativeApplyResult> ApplyAsync(DeclarativeConsentPolicy policy) =>
            Service.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [policy]));
    }
}
