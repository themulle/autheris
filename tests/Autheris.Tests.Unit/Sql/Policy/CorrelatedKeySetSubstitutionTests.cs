using System.Runtime.CompilerServices;
using Autheris.Application.Governance;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>
/// Issue #107 WP-B (PLAN-GOV-107 A-1, A-10; SEC-RF107-06, -07e, -09, -10): the site registry, the polarity rule of the substitution, the
/// reference identity of every node that is not replaced, padding, the empty set and the reserved parameter names.
/// </summary>
[Trait("Category", "Security")]
public sealed class CorrelatedKeySetSubstitutionTests
{
    private static readonly TableIdentity Orders = new("main", "orders");
    private readonly TypedRowFilterBuilder _builder = new();
    private readonly PolicyPredicateRenderer _renderer = new();

    private static ConsentRowFilter Correlated(string fk = "client_id", string? json = "{\"c.country\": \"CH\"}", int group = 0, Guid? id = null,
        RowFilterEvaluationStrategy? strategy = null, int? duration = null, int? maxKeys = null, string? temporal = null, string? validFrom = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        FilterGroup = group,
        FilterType = RowFilterType.SubqueryCorrelated,
        DependentTable = new TableIdentifier("erp", "main", "customers"),
        DependentTableAlias = "c",
        ForeignKeyColumn = fk,
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = json,
        KeySetStrategy = strategy,
        KeySetDurationSeconds = duration,
        KeySetMaxKeys = maxKeys,
        TargetTemporalColumn = temporal,
        DependentValidFromColumn = validFrom
    };

    private static ConsentRowFilter Simple(string column = "region", string value = "\"EU\"", int group = 0) =>
        new() { FilterGroup = group, ColumnName = column, Operator = "EQ", ValueJson = value };

    private static Consent Allow(params ConsentRowFilter[] filters) => new() { Effect = ConsentEffect.Allow, RowFilters = filters };

    private static Consent Deny(params ConsentRowFilter[] filters) => new() { Effect = ConsentEffect.Deny, RowFilters = filters };

    private PolicyPredicate Build(Consent[] allow, Consent[] deny) => _builder.BuildCombined(allow, deny, Orders)!;

    private static PolicyValue Key(long value) => new(value, SqlParameterType.Int64);

    private static KeySetReplacement Replacement(int ordinal, int count, int padded, string fk = "client_id") =>
        new(fk, Enumerable.Range(1, count).Select(i => Key(i * 10L)).ToList(), padded, ordinal);

    private string Sql(PolicyPredicate predicate, TargetSqlDialect dialect = TargetSqlDialect.SqlServer) =>
        _renderer.Render(predicate, dialect, RowFilterAliases.Target).Sql;

    private static IReadOnlyList<(ExistsExpression Node, KeySetReplacement Replacement)> Replace(params (ExistsExpression Node, KeySetReplacement Replacement)[] pairs) => pairs;

    // ---- registry (A-1, SEC-RF107-10 a) ----

    [Fact]
    public void ConsentSite_IsRegisteredWithRuleIdAndRuleSettings()
    {
        var id = Guid.NewGuid();
        var predicate = _builder.Build([Correlated(id: id, strategy: RowFilterEvaluationStrategy.KeySetCache, duration: 90, maxKeys: 500)], Orders);

        var (node, site) = CorrelatedKeySetSubstitution.FindSites(predicate).ShouldHaveSingleItem();

        CorrelatedFilterSiteRegistry.TryGet(node, out _).ShouldBeTrue();
        site.RuleId.ShouldBe(id);
        site.Source.ShouldBe(CorrelatedFilterSource.Consent);
        site.RuleSettings.ShouldBe(new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 90, 500));
        site.StructurallyEligible.ShouldBeTrue();
    }

    [Fact]
    public void ConsentSite_WithoutSettings_HasNoRuleSettings()
    {
        var predicate = _builder.Build([Correlated()], Orders);

        CorrelatedKeySetSubstitution.FindSites(predicate).ShouldHaveSingleItem().Site.RuleSettings.ShouldBeNull();
    }

    [Fact]
    public void CasbinSite_HasSourceCasbinAndNoRuleSettingsOrRuleId()
    {
        var filter = Correlated(strategy: RowFilterEvaluationStrategy.KeySetCache, duration: 90, maxKeys: 500);

        var predicate = AdvancedRlsFilterGenerator_BuildCasbin(filter);

        var site = CorrelatedKeySetSubstitution.FindSites(predicate).ShouldHaveSingleItem().Site;
        site.Source.ShouldBe(CorrelatedFilterSource.Casbin);
        site.RuleSettings.ShouldBeNull();   // D-14: a Casbin site has no rule level
        site.RuleId.ShouldBeNull();
    }

    private static PolicyPredicate AdvancedRlsFilterGenerator_BuildCasbin(ConsentRowFilter filter) =>
        Autheris.Application.Services.RlsFilterGenerator.Instance.BuildCorrelatedPredicate(filter, "__gql_rls_c0_", CorrelatedFilterSource.Casbin);

    [Fact]
    public void TemporalFilter_IsNotStructurallyEligible()
    {
        var predicate = _builder.Build([Correlated(temporal: "invoice_date", validFrom: "valid_from")], Orders);

        CorrelatedKeySetSubstitution.FindSites(predicate).ShouldHaveSingleItem().Site.StructurallyEligible.ShouldBeFalse();
    }

    [Fact]
    public void ANodeThatWasNotRegistered_IsNotASite()
    {
        var registered = _builder.Build([Correlated()], Orders);
        var copy = ((ExistsExpression)registered.Expression) with { };   // a re-created node, as a rewriter would produce

        var predicate = PolicyPredicate.Create(copy, registered.Parameters);

        CorrelatedKeySetSubstitution.FindSites(predicate).ShouldBeEmpty();
        CorrelatedFilterSiteRegistry.TryGet(copy, out _).ShouldBeFalse();
    }

    [Fact]
    public void Registration_DoesNotChangeTheGeneratedSql()
    {
        var registered = _builder.Build([Correlated()], Orders);
        var unregistered = PolicyPredicate.Create(((ExistsExpression)registered.Expression) with { }, registered.Parameters);

        Sql(registered).ShouldBe(Sql(unregistered));
        registered.Fingerprint.ShouldBe(unregistered.Fingerprint);
    }

    [Fact]
    public void Registry_DoesNotKeepNodesAlive()
    {
        var weak = RegisterAndForget();

        for (int i = 0; i < 4 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        weak.IsAlive.ShouldBeFalse();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference RegisterAndForget()
    {
        var predicate = _builder.Build([Correlated()], Orders);
        var node = (ExistsExpression)predicate.Expression;
        CorrelatedFilterSiteRegistry.TryGet(node, out _).ShouldBeTrue();
        return new WeakReference(node);
    }

    // ---- polarity (A-10, SEC-RF107-06 a, b) ----

    [Fact]
    public void FindSites_ReturnsPositiveSitesInAStablePreOrder()
    {
        var predicate = Build([Allow(Correlated("a_id")), Allow(Correlated("b_id"), Simple("region", "\"EU\"")), Allow(Simple("x", "1"))], []);

        var sites = CorrelatedKeySetSubstitution.FindSites(predicate);

        sites.Count.ShouldBe(2);
        Sql(predicate).ShouldContain("[autheris_target].[a_id]");
        var texts = sites.Select(s => Sql(PolicyPredicate.Create(s.Node, predicate.Parameters))).ToList();
        texts[0].ShouldContain("[autheris_target].[a_id]");
        texts[1].ShouldContain("[autheris_target].[b_id]");
    }

    [Fact]
    public void AllowAndOrGroupsAndParentheses_AreSubstitutable()
    {
        var predicate = Build([Allow(Correlated("a_id", group: 0), Simple(group: 0), Correlated("b_id", group: 1))], []);

        CorrelatedKeySetSubstitution.FindSites(predicate).Count.ShouldBe(2);
    }

    [Fact]
    public void NegatedSite_NotSubstituted()   // SEC-RF107-06 (a)
    {
        var predicate = Build([Allow(Correlated("a_id"))], [Deny(Correlated("d_id"))]);

        var scan = CorrelatedKeySetSubstitution.Scan(predicate);

        scan.Eligible.ShouldHaveSingleItem();
        Sql(PolicyPredicate.Create(scan.Eligible[0].Node, predicate.Parameters)).ShouldContain("[a_id]");
        var excluded = scan.Excluded.ShouldHaveSingleItem();
        Sql(PolicyPredicate.Create(excluded.Node, predicate.Parameters)).ShouldContain("[d_id]");
        Should.Throw<InvalidOperationException>(() =>
            CorrelatedKeySetSubstitution.Substitute(predicate, Replace((excluded.Node, Replacement(1, 2, 2, "d_id"))), TargetSqlDialect.SqlServer));
    }

    [Fact]
    public void DenyOnly_HasNoSubstitutableSite()
    {
        var predicate = Build([], [Deny(Correlated())]);

        var scan = CorrelatedKeySetSubstitution.Scan(predicate);

        scan.Eligible.ShouldBeEmpty();
        scan.Excluded.Count.ShouldBe(1);
    }

    [Fact]
    public void SharedInstanceInTwoPositions_NotSubstituted()   // SEC-RF107-06 (b)
    {
        var built = _builder.Build([Correlated()], Orders);
        var exists = (ExistsExpression)built.Expression;
        // the same registered instance, once positive and once negated
        var shared = PolicyPredicate.Create(
            new BinaryExpression(new ParenthesizedExpression(exists), BinaryOperator.And, new UnaryExpression(UnaryOperator.Not, exists)),
            built.Parameters);

        var scan = CorrelatedKeySetSubstitution.Scan(shared);

        scan.Eligible.ShouldBeEmpty();
        scan.Excluded.ShouldHaveSingleItem();
    }

    [Fact]
    public void SharedInstanceTwicePositive_NotSubstituted()
    {
        var built = _builder.Build([Correlated()], Orders);
        var exists = (ExistsExpression)built.Expression;
        var twice = PolicyPredicate.Create(new BinaryExpression(exists, BinaryOperator.Or, exists), built.Parameters);

        CorrelatedKeySetSubstitution.FindSites(twice).ShouldBeEmpty();
        CorrelatedKeySetSubstitution.Scan(twice).Excluded.ShouldHaveSingleItem();
    }

    [Fact]
    public void SiteInsideCase_NotSubstituted()   // SEC-RF107-06 (a)
    {
        var built = _builder.Build([Correlated()], Orders);
        var exists = (ExistsExpression)built.Expression;
        var caseExpression = new CaseExpression(null,
            [new WhenClause(exists, new LiteralExpression(1L, LiteralType.Integer))],
            new LiteralExpression(0L, LiteralType.Integer));
        var predicate = PolicyPredicate.Create(
            new BinaryExpression(caseExpression, BinaryOperator.Equal, new LiteralExpression(1L, LiteralType.Integer)), built.Parameters);

        CorrelatedKeySetSubstitution.FindSites(predicate).ShouldBeEmpty();
        CorrelatedKeySetSubstitution.Scan(predicate).Excluded.ShouldHaveSingleItem();
    }

    [Fact]
    public void SiteInsideAComparisonOrAFunction_NotSubstituted()
    {
        var built = _builder.Build([Correlated()], Orders);
        var exists = (ExistsExpression)built.Expression;
        var comparison = PolicyPredicate.Create(new BinaryExpression(exists, BinaryOperator.Equal, new LiteralExpression(true, LiteralType.Boolean)), built.Parameters);
        var isNull = PolicyPredicate.Create(new UnaryExpression(UnaryOperator.IsNull, exists), built.Parameters);

        CorrelatedKeySetSubstitution.FindSites(comparison).ShouldBeEmpty();
        CorrelatedKeySetSubstitution.FindSites(isNull).ShouldBeEmpty();
    }

    // ---- substitution ----

    [Fact]
    public void Substitute_ReplacesTheSiteByAPaddedInList_WithReservedParameterNames()
    {
        var predicate = _builder.Build([Correlated()], Orders);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        var result = CorrelatedKeySetSubstitution.Substitute(predicate, Replace((node, Replacement(3, 3, 4))), TargetSqlDialect.SqlServer);

        var rendered = _renderer.Render(result, TargetSqlDialect.SqlServer, RowFilterAliases.Target);
        rendered.Sql.ShouldBe("[autheris_target].[client_id] IN (@__rls_ks3_0, @__rls_ks3_1, @__rls_ks3_2, @__rls_ks3_3)");
        result.Parameters.Keys.ShouldBe(["__rls_ks3_0", "__rls_ks3_1", "__rls_ks3_2", "__rls_ks3_3"], ignoreOrder: true);
        result.Parameters["__rls_ks3_2"].ShouldBe(Key(30));
        result.Parameters["__rls_ks3_3"].ShouldBe(Key(30));   // padded with the last key
        result.Parameters.Values.ShouldAllBe(v => v.Type == SqlParameterType.Int64);
        AstReflection.Collect<PolicyParameterExpression>(result.Expression).ShouldAllBe(p => p.Origin == ParameterOrigin.Policy);
        AstReflection.Collect<LiteralExpression>(result.Expression).ShouldBeEmpty();   // keys are binds, never literals
        AstReflection.Collect<ExistsExpression>(result.Expression).ShouldBeEmpty();
    }

    [Fact]
    public void Substitute_EmptyKeySet_BecomesOneEqualsZero_AndKeepsTheStructure()
    {
        var predicate = Build([Allow(Correlated("a_id")), Allow(Simple())], []);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        var result = CorrelatedKeySetSubstitution.Substitute(predicate,
            Replace((node, new KeySetReplacement("a_id", [], 0, 0))), TargetSqlDialect.PostgreSql);

        Sql(result, TargetSqlDialect.PostgreSql).ShouldBe("(1 = 0 OR \"region\" = @__gql_rls_1)");
        result.Parameters.Keys.ShouldNotContain(k => k.StartsWith("__rls_ks", StringComparison.Ordinal));
    }

    [Fact]
    public void Substitute_AllowOrSimpleFilter_AndDenyOnTheSameTable_KeepTheirStructure()
    {
        var predicate = Build([Allow(Correlated("a_id")), Allow(Simple())], [Deny(Correlated("d_id"))]);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        var sql = Sql(CorrelatedKeySetSubstitution.Substitute(predicate, Replace((node, Replacement(0, 2, 2, "a_id"))), TargetSqlDialect.SqlServer));

        sql.ShouldStartWith("(([autheris_target].[a_id] IN (@__rls_ks0_0, @__rls_ks0_1) OR [region] = @__gql_rls_");
        sql.ShouldContain("AND NOT (EXISTS (SELECT 1 FROM [main].[customers] AS [c]");   // the DENY site stays EXISTS
    }

    [Fact]
    public void Substitute_Oracle_ChunksTheInListAtThe1000ItemLimit()
    {
        var predicate = _builder.Build([Correlated()], Orders);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        var result = CorrelatedKeySetSubstitution.Substitute(predicate, Replace((node, Replacement(0, 1200, 2000))), TargetSqlDialect.Oracle);

        var sql = Sql(result, TargetSqlDialect.Oracle);
        System.Text.RegularExpressions.Regex.Count(sql, " IN \\(").ShouldBe(2);
        sql.ShouldStartWith("(");
        sql.ShouldContain(" OR \"autheris_target\".\"client_id\" IN (");
        result.Parameters.Count.ShouldBe(2000);
    }

    [Fact]
    public void Substitute_WithoutReplacements_ReturnsThePredicate()
    {
        var predicate = _builder.Build([Correlated()], Orders);

        CorrelatedKeySetSubstitution.Substitute(predicate, Replace(), TargetSqlDialect.SqlServer).ShouldBeSameAs(predicate);
    }

    [Fact]
    public void Substitute_DoesNotChangeTheOriginalPredicate()
    {
        var predicate = _builder.Build([Correlated()], Orders);
        var expression = predicate.Expression;
        var fingerprint = predicate.Fingerprint;
        var parameterNames = predicate.Parameters.Keys.ToList();
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        _ = CorrelatedKeySetSubstitution.Substitute(predicate, Replace((node, Replacement(0, 2, 2))), TargetSqlDialect.SqlServer);

        predicate.Expression.ShouldBeSameAs(expression);
        predicate.Fingerprint.ShouldBe(fingerprint);
        predicate.Parameters.Keys.ShouldBe(parameterNames);
        CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node.ShouldBeSameAs(node);
    }

    [Fact]
    public void UnreplacedNodes_AreReferenceEqual()   // SEC-RF107-10 (b)
    {
        var predicate = Build(
            [Allow(Correlated("a_id")), Allow(Correlated("b_id", group: 0), Simple("region", "\"EU\"")), Allow(Simple("amount", "5"))], [Deny(Simple("secret", "true"))]);
        var sites = CorrelatedKeySetSubstitution.FindSites(predicate);
        sites.Count.ShouldBe(2);
        var replaced = sites[0].Node;
        var kept = sites[1].Node;

        var result = CorrelatedKeySetSubstitution.Substitute(predicate, Replace((replaced, Replacement(0, 2, 2, "a_id"))), TargetSqlDialect.SqlServer);

        var original = Nodes(predicate.Expression);
        var updated = Nodes(result.Expression);
        var path = PathTo(predicate.Expression, replaced)!;
        var gone = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var node in path) gone.Add(node);   // the replaced node and its ancestors
        foreach (var node in Nodes(replaced)) gone.Add(node);

        // every node outside the replaced subtree and its ancestors is the very same instance in the result
        foreach (var node in original.Where(n => !gone.Contains(n)))
        {
            updated.Contains(node).ShouldBeTrue($"{node.GetType().Name} was re-created");
        }

        // the other site and its instance survive untouched and are still registered
        CorrelatedKeySetSubstitution.FindSites(result).Single().Node.ShouldBeSameAs(kept);
        // nothing but the replacement and its ancestors is new
        foreach (var node in updated.Where(n => !original.Contains(n)))
        {
            (node is ParenthesizedExpression or BinaryExpression or InListExpression or ColumnReference or SqlQualifiedName or SqlIdentifier
                or PolicyParameterExpression).ShouldBeTrue($"unexpected new node {node.GetType().Name}");
        }
    }

    private static HashSet<object> Nodes(object root)
    {
        var set = new HashSet<object>(ReferenceEqualityComparer.Instance);
        AstReflection.Walk(root, node =>
        {
            if (node is SqlNode) set.Add(node);
            return true;
        });
        return set;
    }

    private static List<object>? PathTo(object current, object target)
    {
        if (ReferenceEquals(current, target)) return [current];
        List<object>? found = null;
        AstReflection.Children(current, child =>
        {
            if (found is null && PathTo(child, target) is { } below) found = [current, .. below];
        });
        return found;
    }

    [Fact]
    public void Substitute_RefusesANodeThatIsNotASite()
    {
        var predicate = _builder.Build([Correlated()], Orders);
        var stranger = (ExistsExpression)_builder.Build([Correlated()], Orders).Expression;

        Should.Throw<InvalidOperationException>(() =>
            CorrelatedKeySetSubstitution.Substitute(predicate, Replace((stranger, Replacement(0, 1, 1))), TargetSqlDialect.SqlServer));
    }

    [Fact]
    public void Substitute_RefusesATemporalSite_NotStructurallyEligible()   // SEC-RF107-06 (F2)
    {
        var predicate = _builder.Build([Correlated(temporal: "invoice_date", validFrom: "valid_from")], Orders);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        Should.Throw<InvalidOperationException>(() =>
            CorrelatedKeySetSubstitution.Substitute(predicate, Replace((node, Replacement(0, 1, 1))), TargetSqlDialect.SqlServer));
    }

    [Fact]
    public void Substitute_ParametersAreExactlyTheReferencedOnes_NoStaleNames()   // SEC-RF107-07e (F6)
    {
        var predicate = _builder.Build([Correlated(), Simple()], Orders);
        predicate.Parameters.Count.ShouldBeGreaterThan(1);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        var result = CorrelatedKeySetSubstitution.Substitute(predicate, Replace((node, Replacement(0, 2, 2))), TargetSqlDialect.SqlServer);

        var referenced = AstReflection.Collect<PolicyParameterExpression>(result.Expression).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        result.Parameters.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(referenced).ShouldBeTrue();
        referenced.ShouldContain("__rls_ks0_0");
        foreach (var stale in predicate.Parameters.Keys.Except(referenced)) result.Fingerprint.ShouldNotContain(stale);
        result.Fingerprint.ShouldBe(AstFingerprint.Compute(result.Expression));
    }

    [Fact]
    public void Substitute_RefusesNullKeys_AndUnderPaddedLists()
    {
        var predicate = _builder.Build([Correlated()], Orders);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        Should.Throw<ArgumentException>(() => CorrelatedKeySetSubstitution.Substitute(predicate,
            Replace((node, new KeySetReplacement("client_id", [new PolicyValue(null, SqlParameterType.Null)], 1, 0))), TargetSqlDialect.SqlServer));
        Should.Throw<ArgumentException>(() => CorrelatedKeySetSubstitution.Substitute(predicate,
            Replace((node, new KeySetReplacement("client_id", [Key(1), Key(2)], 1, 0))), TargetSqlDialect.SqlServer));
    }

    [Fact]
    public void SameOrdinalWithDifferentKeys_IsAPolicyConflict_SEC_RF107_07e()
    {
        var a = _builder.Build([Correlated("a_id")], Orders);
        var b = _builder.Build([Correlated("b_id")], Orders);
        var left = CorrelatedKeySetSubstitution.Substitute(a,
            Replace((CorrelatedKeySetSubstitution.FindSites(a).Single().Node, Replacement(0, 2, 2, "a_id"))), TargetSqlDialect.SqlServer);
        var right = CorrelatedKeySetSubstitution.Substitute(b,
            Replace((CorrelatedKeySetSubstitution.FindSites(b).Single().Node, new KeySetReplacement("b_id", [Key(777), Key(888)], 2, 0))), TargetSqlDialect.SqlServer);

        Should.Throw<PolicyConflictException>(() => left.And(right));
    }

    [Fact]
    public void DifferentOrdinals_MergeWithoutConflict()
    {
        var a = _builder.Build([Correlated("a_id")], Orders);
        var b = _builder.Build([Correlated("b_id")], Orders);
        var left = CorrelatedKeySetSubstitution.Substitute(a,
            Replace((CorrelatedKeySetSubstitution.FindSites(a).Single().Node, Replacement(0, 2, 2, "a_id"))), TargetSqlDialect.SqlServer);
        var right = CorrelatedKeySetSubstitution.Substitute(b,
            Replace((CorrelatedKeySetSubstitution.FindSites(b).Single().Node, Replacement(1, 2, 2, "b_id"))), TargetSqlDialect.SqlServer);

        left.And(right).Parameters.Count.ShouldBe(4);
    }

    [Fact]
    public void TwoStructurallyEqualSites_BothSubstituted_DistinctOrdinals()
    {
        // D-WPC-6: two registered nodes that are equal as records (same rule shape) are still two instances; a default-comparer
        // dictionary would collapse them. The list form keeps both.
        var predicate = Build([Allow(Correlated(id: Guid.NewGuid(), group: 0), Correlated(id: Guid.NewGuid(), group: 1))], []);
        var sites = CorrelatedKeySetSubstitution.FindSites(predicate);
        sites.Count.ShouldBe(2);
        sites[0].Node.ShouldNotBeSameAs(sites[1].Node);

        var result = CorrelatedKeySetSubstitution.Substitute(predicate,
            Replace((sites[0].Node, Replacement(0, 2, 2)), (sites[1].Node, Replacement(1, 2, 2))), TargetSqlDialect.SqlServer);

        result.Parameters.Keys.Where(k => k.StartsWith("__rls_ks0_", StringComparison.Ordinal)).Count().ShouldBe(2);
        result.Parameters.Keys.Where(k => k.StartsWith("__rls_ks1_", StringComparison.Ordinal)).Count().ShouldBe(2);
        CorrelatedKeySetSubstitution.FindSites(result).ShouldBeEmpty();
    }

    [Fact]
    public void DuplicateNodeInList_Throws()
    {
        var predicate = _builder.Build([Correlated()], Orders);
        var node = CorrelatedKeySetSubstitution.FindSites(predicate).Single().Node;

        Should.Throw<InvalidOperationException>(() => CorrelatedKeySetSubstitution.Substitute(predicate,
            Replace((node, Replacement(0, 2, 2)), (node, Replacement(1, 2, 2))), TargetSqlDialect.SqlServer));
    }

    // ---- padding (A-10) ----

    [Theory]
    [InlineData(0, 2000, 0)]
    [InlineData(1, 2000, 1)]
    [InlineData(2, 2000, 2)]
    [InlineData(3, 2000, 4)]
    [InlineData(5, 2000, 8)]
    [InlineData(1000, 2000, 1024)]
    [InlineData(1025, 2000, 2000)]
    [InlineData(1025, 1500, 1500)]
    [InlineData(2000, 2000, 2000)]
    [InlineData(7, 7, 7)]
    public void Padding_IsTheNextPowerOfTwoCappedAtMaxKeys(int count, int maxKeys, int expected) =>
        KeySetPadding.Bucket(count, maxKeys).ShouldBe(expected);

    [Fact]
    public void Padding_NeverTruncatesKeys()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => KeySetPadding.Bucket(11, 10));
        Should.Throw<ArgumentOutOfRangeException>(() => KeySetPadding.Bucket(1, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => KeySetPadding.Bucket(-1, 10));
    }

    // ---- SEC-RF107-09 ----

    [Fact]
    public void KeySetReplacement_ToStringPrintsCountsOnly_SEC_RF107_09()
    {
        var replacement = new KeySetReplacement("fk", [new PolicyValue("secret-key-4711", SqlParameterType.String)], 1, 7);

        replacement.ToString().ShouldBe("KeySetReplacement { Keys = 1, PaddedCount = 1, SiteOrdinal = 7 }");
    }
}
