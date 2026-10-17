using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.ClientListHarness;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>REQ-SQL-128 Phase 3: order, position, per-statement cap and output allowance, each pinned by a mutation control.</summary>
[Trait("Category", "Security")]
public class ClientListMutationTests
{
    private const StatementPermissions Dml = StatementPermissions.Update | StatementPermissions.Delete;

    private readonly ClientListHarness _h = new();

    private static IDisposable Mutant(SecurityControl control) =>
        CompilerHooks.Use(new MutantHooks { DisabledControls = new HashSet<SecurityControl> { control } });

    // SEC-128-01 / M-1
    [Theory]
    [InlineData("DELETE FROM orders WHERE id NOT IN (__param_e)")]
    [InlineData("UPDATE orders SET status = 'x' WHERE id NOT IN (__param_e)")]
    [InlineData("DELETE FROM orders WHERE amount = 1 OR id NOT IN (__param_e)")]
    public void Dml_NotInEmptyList_IsRejectedAsUnfiltered_SEC_128_01(string sql)
    {
        Assert.Throws<UnfilteredDmlException>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql, Shapes(("e", 0)), Dml));
    }

    [Theory]
    [InlineData("DELETE FROM orders WHERE id IN (__param_e)")]
    [InlineData("UPDATE orders SET status = 'x' WHERE id IN (__param_e)")]
    public void Dml_InEmptyList_IsNeverTriviallyTrue_SEC_128_01(string sql)
    {
        // IN (@empty) becomes 1 = 0. The DML guard requires a column reference, so the statement is refused (fail closed, zero rows).
        Assert.Throws<UnfilteredDmlException>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql, Shapes(("e", 0)), Dml));
    }

    [Fact]
    public void Dml_InFilledList_IsAccepted()
    {
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "DELETE FROM orders WHERE id IN (__param_e)", Shapes(("e", 2)), Dml);
        Assert.Equal(2, ListSlots(compiled, "e").Count);
    }

    // M-2
    [Fact]
    public void Mutation_ExpanderAfterSecurityVisitor_FailsM1()
    {
        using var _ = Mutant(SecurityControl.ClientListExpandsBeforeGuards);
        // Control off: the guard sees the operand column and the list item, so the full-table delete is no longer refused.
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "DELETE FROM orders WHERE id NOT IN (__param_e)", Shapes(("e", 0)), Dml);
        Assert.Contains("1 = 1", compiled.Sql);
    }

    // SEC-128-10
    [Fact]
    public void Mutation_PositionCheckOff_LetsAListIntoAScalarSlot()
    {
        using var _ = Mutant(SecurityControl.ClientListPosition);
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id = __param_l", Shapes(("l", 3)));
        var slot = Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.ClientNamed);
        Assert.Null(slot.ListIndex);   // the binder backstop is the only remaining guard (see ClientListBinderTests)
    }

    // SEC-128-02 / M-3
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, 1000, 3)]      // cap 2000
    [InlineData(TargetSqlDialect.PostgreSql, 1000, 11)]    // cap 10000
    public void Expansion_RepeatedUse_IsCappedPerStatement_BeforeEmission(TargetSqlDialect dialect, int count, int uses)
    {
        string sql = "SELECT id FROM orders WHERE " + string.Join(" OR ", Enumerable.Repeat("id IN (__param_l)", uses));
        var passes = new List<string>();
        using var _ = CompilerHooks.Use(new MutantHooks { OnPass = passes.Add });

        var ex = Assert.Throws<ClientListException>(() => _h.Compile(dialect, sql, Shapes(("l", count))));

        Assert.Equal(GovernedSqlErrorCodes.ListParamTooLarge, ex.Code);
        Assert.DoesNotContain("emit", passes);
        Assert.DoesNotContain(ex.Message, new[] { count.ToString(), uses.ToString() });
    }

    [Fact]
    public void Expansion_AtTheCap_IsAccepted()
    {
        // SQL Server: min(2100 - 100, 10000) = 2000 expanded items over all uses.
        var compiled = _h.Compile(TargetSqlDialect.SqlServer, "SELECT id FROM orders WHERE id IN (__param_l) OR amount IN (__param_l)", Shapes(("l", 1000)));
        Assert.Equal(1000, ListSlots(compiled, "l").Count);
    }

    [Fact]
    public void Mutation_MaxExpandedOff_ExpansionContinuesPastTheCap()
    {
        string sql = "SELECT id FROM orders WHERE " + string.Join(" OR ", Enumerable.Repeat("id IN (__param_l)", 11));
        var passes = new List<string>();
        using var _ = CompilerHooks.Use(new MutantHooks { OnPass = passes.Add, DisabledControls = new HashSet<SecurityControl> { SecurityControl.ClientListMaxExpanded } });
        try
        {
            _h.Compile(TargetSqlDialect.PostgreSql, sql, Shapes(("l", 1000)));
        }
        catch (Exception ex) when (ex is not ClientListException)
        {
            // a later size limit may still stop the statement; the point is that the cap did not stop it earlier
        }

        Assert.Contains("emit", passes);
    }

    // SEC-128-03 / M-4
    [Fact]
    public void ExpansionFactor_ListMarkersAreAllowed_AStatementOf60CharsWith1000Values()
    {
        const string sql = "SELECT id FROM orders WHERE id IN (__param_l)";
        Assert.True(sql.Length < 64);
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, sql, Shapes(("l", 1000)));
        Assert.True(compiled.Sql.Length > 64 * 64);
    }

    [Fact]
    public void OutputAllowance_OnlyForExpandedLists_UnusedListsGiveNoSlack()
    {
        var request = _h.Request(TargetSqlDialect.PostgreSql, Shapes(("unused", 1000))) with { MaxExpansionFactor = 1 };
        var ex = Assert.Throws<SqlLimitExceededException>(() =>
            _h.Engine.Compile("SELECT id FROM orders".AsMemory(), request, CancellationToken.None));
        Assert.Equal(SqlLimitKind.PolicyExpansionFactor, ex.Kind);
    }

    [Fact]
    public void ExpansionFactor_PolicyExpansionStillBounded_WithAList()
    {
        var request = _h.Request(TargetSqlDialect.PostgreSql, Shapes(("l", 3))) with { MaxExpansionFactor = 1 };
        var ex = Assert.Throws<SqlLimitExceededException>(() =>
            _h.Engine.Compile("SELECT id FROM orders WHERE id IN (__param_l)".AsMemory(), request, CancellationToken.None));
        Assert.Equal(SqlLimitKind.PolicyExpansionFactor, ex.Kind);
    }

    [Fact]
    public void MarkerAllowance_CoversTheLongestMarkerOfEveryDialect()
    {
        Assert.True(ClientListExpander.MarkerAllowance >= 12);
        foreach (var dialect in Dialects)
        {
            var caps = DialectCapabilityTable.Default.Get(dialect);
            int count = Math.Min(Math.Min(ClientListExpander.MaxExpandedItems(caps), caps.MaxInListItems ?? int.MaxValue), 2000);
            var compiled = _h.Compile(dialect, "SELECT id FROM orders WHERE id IN (__param_l)", Shapes(("l", count)));
            var longest = ListSlots(compiled, "l").Max(s => s.Marker.Length);
            Assert.True(longest + 2 <= ClientListExpander.MarkerAllowance, $"{dialect} marker length {longest}");
        }
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, 2000)]
    [InlineData(TargetSqlDialect.PostgreSql, 10000)]
    [InlineData(TargetSqlDialect.Oracle, 10000)]
    public void MaxExpandedItems_IsMinOfBudgetMinus100And10000(TargetSqlDialect dialect, int expected)
    {
        Assert.Equal(expected, ClientListExpander.MaxExpandedItems(DialectCapabilityTable.Default.Get(dialect)));
    }

    // SEC-128-11 / M-9
    [Fact]
    public void PolicyBinds_IdenticalWithAndWithoutAList_AndAListNamedLikeAPolicyParameterIsIsolated()
    {
        var predicate = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier("Region", true)])),
                BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) });
        _h.RowFilters.NoPolicy.Remove(PolicyFixtures.Orders);
        _h.RowFilters.Predicates[PolicyFixtures.Orders] = predicate;

        var without = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id = __param_one");
        // the list is named like the policy parameter without its reserved prefix
        var with = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id IN (__param_pol_region)", Shapes(("pol_region", 3)));

        static List<(ParameterOrigin, string?, object?)> Security(CompiledSql c) =>
            c.Parameters.Where(p => p.Origin is ParameterOrigin.Tenant or ParameterOrigin.Policy)
                .Select(p => (p.Origin, p.SourceName, p.Value)).OrderBy(x => x.Item2, StringComparer.Ordinal).ToList();

        Assert.Equal(Security(without), Security(with));
        Assert.All(ListSlots(with, "pol_region"), s => Assert.Null(s.Value));
        Assert.DoesNotContain(with.Parameters, p => p.Origin == ParameterOrigin.Policy && p.SourceName == "pol_region");
    }

    [Theory]
    [InlineData("__pol_region")]
    [InlineData("__autheris_tenant")]
    [InlineData("__rls_ks1")]
    public void AListWithAReservedGatewayName_IsRefused(string name)
    {
        var ex = Record.Exception(() => _h.Compile(TargetSqlDialect.PostgreSql, $"SELECT id FROM orders WHERE id IN (__param_{name})", Shapes((name, 2))));
        Assert.NotNull(ex);
    }
}
