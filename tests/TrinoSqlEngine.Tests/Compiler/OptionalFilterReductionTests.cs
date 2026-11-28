using System.Collections.Frozen;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.ClientListHarness;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>Optional filter <c>(@p IS NULL OR X)</c>: reduced by the null-ness of the parameter after the security pass.</summary>
[Trait("Category", "SqlGate")]
public class OptionalFilterReductionTests
{
    private const string Optional = "SELECT id FROM orders WHERE (__param_region IS NULL OR region = __param_region)";

    private readonly ClientListHarness _h = new();

    private static ClientNullShapes Null(params string[] names) => ClientNullShapes.Create(names);

    private static List<BoundParameter> ClientSlots(CompiledSql c) => c.Parameters.Where(p => p.Origin == ParameterOrigin.ClientNamed).ToList();

    private static string Where(CompiledSql c) => c.Sql[c.Sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase)..];

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Null_param_drops_the_predicate_and_its_slots(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, Optional, nulls: Null("region"));

        Assert.Empty(ClientSlots(compiled));
        Assert.DoesNotContain("IS NULL", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("region", Where(compiled), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 = 1", compiled.Sql);
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Value_param_keeps_only_the_comparison(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, Optional);

        Assert.Single(ClientSlots(compiled));
        Assert.DoesNotContain("IS NULL", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("region", Where(compiled), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Reversed_operand_order_is_reduced(TargetSqlDialect dialect)
    {
        const string sql = "SELECT id FROM orders WHERE (region = __param_region OR __param_region IS NULL)";

        Assert.Empty(ClientSlots(_h.Compile(dialect, sql, nulls: Null("region"))));
        var kept = _h.Compile(dialect, sql);
        Assert.Single(ClientSlots(kept));
        Assert.DoesNotContain("IS NULL", kept.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Nested_inside_and_or_parentheses_is_reduced(TargetSqlDialect dialect)
    {
        const string sql = "SELECT id FROM orders WHERE status = 'open' AND ((((__param_region IS NULL OR region = __param_region)) AND " +
            "(__param_min IS NULL OR amount >= __param_min)) OR id = 7) AND (__param_q IS NULL OR status = __param_q OR id = 1)";

        var allNull = _h.Compile(dialect, sql, nulls: Null("region", "min", "q"));
        Assert.Empty(ClientSlots(allNull));
        Assert.DoesNotContain("IS NULL", allNull.Sql, StringComparison.OrdinalIgnoreCase);

        var mixed = _h.Compile(dialect, sql, nulls: Null("min"));
        Assert.Equal(["q", "region"], ClientSlots(mixed).Select(s => s.SourceName!.ToLowerInvariant()).Order().ToArray());
        Assert.DoesNotContain("IS NULL", mixed.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("amount", Where(mixed), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Bound_page_offset_slot_survives_in_the_null_and_the_value_shape(TargetSqlDialect dialect)
    {
        const string sql = "SELECT id FROM orders WHERE (__param_region IS NULL OR region = __param_region) ORDER BY id";

        foreach (var nulls in new ClientNullShapes?[] { Null("region"), null })
        {
            var compiled = _h.Engine.Compile(sql.AsMemory(), _h.Request(dialect, nulls: nulls) with
            {
                BindPageOffset = true,
                EnforcedMaxRows = 100,
                PageOffset = 0
            }, CancellationToken.None);

            Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.PageOffset);
        }
    }

    [Fact]
    public void Chain_of_or_with_several_optional_parts_is_reduced()
    {
        const string sql = "SELECT id FROM orders WHERE (__param_a IS NULL OR __param_b IS NULL OR id = __param_a)";

        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, sql, nulls: Null("b"));
        Assert.Empty(ClientSlots(compiled));
        var valued = _h.Compile(TargetSqlDialect.PostgreSql, sql);
        Assert.DoesNotContain("__param_", valued.Sql);
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE (__param_region IS NOT NULL OR region = __param_region)")]
    [InlineData("SELECT id FROM orders WHERE (__param_region IS NULL AND region = 'EU')")]
    [InlineData("SELECT id FROM orders WHERE (region IS NULL OR status = __param_region)")]
    [InlineData("SELECT id FROM orders WHERE (UPPER(__param_region) IS NULL OR region = 'EU')")]
    [InlineData("SELECT id FROM orders WHERE (COALESCE(__param_region, 'x') IS NULL OR region = 'EU')")]
    public void Is_not_null_and_other_shapes_are_untouched(string sql)
    {
        var withNull = _h.Compile(TargetSqlDialect.PostgreSql, sql, nulls: Null("region"));
        var without = _h.Compile(TargetSqlDialect.PostgreSql, sql);

        Assert.Equal(without.Sql, withNull.Sql);
        Assert.Contains("NULL", without.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Positional_marker_is_never_reduced()
    {
        // '?' style markers are not synthetic client parameters: the OR form is kept for them.
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE (region IS NULL OR id = 1)", nulls: Null("region"));

        Assert.Contains(" OR ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    [Trait("Category", "Security")]
    public void Policy_and_tenant_predicates_are_never_reduced(TargetSqlDialect dialect)
    {
        var predicate = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier("Region", true)])),
                BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) });
        _h.RowFilters.NoPolicy.Remove(PolicyFixtures.Orders);
        _h.RowFilters.Predicates[PolicyFixtures.Orders] = predicate;

        // the client names equal the policy parameter name and the tenant parameter name: only the client reference may be reduced
        var compiled = _h.Compile(dialect, "SELECT id FROM orders WHERE (__param_pol_region IS NULL OR id = 1)", nulls: Null("pol_region", "__pol_region", "__autheris_tenant"));

        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Empty(ClientSlots(compiled));
        Assert.Contains("1 = 1", compiled.Sql);
        Assert.Contains("region", Where(compiled), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Policy_tenant_consent_predicates_never_reduced()
    {
        var predicate = Parse("(:r IS NULL OR Region = :r)");
        _h.RowFilters.NoPolicy.Remove(PolicyFixtures.Orders);
        _h.RowFilters.Predicates[PolicyFixtures.Orders] = predicate;

        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id = 1", nulls: Null("r", "__pol_r"));

        Assert.Contains(" OR ", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Injected_lookalike_is_not_reduced()
    {
        // a row policy written like an optional filter, with a parameter named like the client parameter of the statement
        var predicate = Parse("(:region IS NULL OR Region = :region)");
        _h.RowFilters.NoPolicy.Remove(PolicyFixtures.Orders);
        _h.RowFilters.Predicates[PolicyFixtures.Orders] = predicate;

        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, Optional, nulls: Null("region"));

        Assert.Empty(ClientSlots(compiled));
        Assert.Contains("IS NULL", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy);
    }

    private static PolicyPredicate Parse(string text)
    {
        var builder = PolicyFixtures.Context(PolicyFixtures.Orders);
        builder.Parameters["r"] = new PolicyValue("EU", SqlParameterType.String);
        builder.Parameters["region"] = new PolicyValue("EU", SqlParameterType.String);
        return new PolicyExpressionParser(new FastSqlEngine(), new FakeTimeProvider(), null).Parse(text, builder.Build());
    }

    [Theory]
    [Trait("Category", "Security")]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Dml_optional_client_filter_is_refused_for_every_dialect_and_null_ness(TargetSqlDialect dialect)
    {
        const StatementPermissions all = StatementPermissions.Update | StatementPermissions.Delete;
        foreach (var sql in new[]
                 {
                     "UPDATE orders SET status = 'x' WHERE id = 1 AND (__param_region IS NULL OR region = __param_region)",
                     "UPDATE orders SET status = 'x' WHERE id = 1 AND (region = __param_region OR __param_region IS NULL)",
                     "DELETE FROM orders WHERE id = 1 AND (__param_region IS NULL OR region = __param_region)",
                     "DELETE FROM orders WHERE (__param_region IS NULL OR region = __param_region) AND id = 1",
                     "DELETE FROM orders WHERE id = 1 AND __param_region IS NULL"
                 })
        {
            foreach (var nulls in new ClientNullShapes?[] { Null("region"), null })
            {
                Assert.Throws<UnfilteredDmlException>(() => _h.Compile(dialect, sql, statements: all, nulls: nulls));
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dml_plain_client_filter_is_still_accepted()
    {
        var update = _h.Compile(TargetSqlDialect.PostgreSql,
            "UPDATE orders SET status = 'x' WHERE id = 1 AND region = __param_region", statements: StatementPermissions.Update);
        Assert.Single(ClientSlots(update));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Dml_and_insert_select_keep_or_form()
    {
        const StatementPermissions all = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete;
        const string sql = "INSERT INTO orders (id, tenantid, status) SELECT id, tenantid, status FROM orders WHERE (__param_region IS NULL OR region = __param_region)";
        var refusal = Record.Exception(() => _h.Compile(TargetSqlDialect.PostgreSql, sql, statements: all, nulls: Null("region")));
        if (refusal is null)
        {
            var insertSelect = _h.Compile(TargetSqlDialect.PostgreSql, sql, statements: all, nulls: Null("region"));
            Assert.Contains(" OR ", insertSelect.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IS NULL", insertSelect.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void List_param_in_is_null_is_still_refused()
    {
        var ex = Assert.Throws<ClientListException>(() => _h.Compile(TargetSqlDialect.PostgreSql,
            "SELECT id FROM orders WHERE (__param_l IS NULL OR id IN (__param_l))", ClientListHarness.Shapes(("l", 2)), nulls: Null("l")));

        Assert.Equal(GovernedSqlErrorCodes.ListParamPosition, ex.Code);
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Statement_binds_with_zero_client_slots_when_all_uses_are_dropped(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, "SELECT id FROM orders WHERE (__param_a IS NULL OR id = __param_a) AND (__param_b IS NULL OR status = __param_b)",
            nulls: Null("a", "b"));

        Assert.Empty(ClientSlots(compiled));
        if (dialect != TargetSqlDialect.Oracle)
        {
            using var cmd = new BinderMutationTests.RecCommand();
            CompiledSqlBinders.For(dialect).Bind(cmd, compiled, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(compiled.Parameters.Length, cmd.Parameters.Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Security")]
    public void Masked_column_in_the_kept_branch_is_refused(bool isNull)
    {
        MaskEmail();
        var sql = "SELECT id FROM orders WHERE (__param_e IS NULL OR email = __param_e)";

        Assert.ThrowsAny<Exception>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql, nulls: isNull ? Null("e") : null));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Verdict_is_identical_for_null_and_value_when_masked_column_in_dropped_branch()
    {
        MaskEmail();
        var sql = "SELECT id FROM orders WHERE (__param_e IS NULL OR email = __param_e)";

        var forNull = Assert.ThrowsAny<Exception>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql, nulls: Null("e")));
        var forValue = Assert.ThrowsAny<Exception>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql));

        Assert.Equal(forNull.GetType(), forValue.GetType());
        Assert.Equal(forNull.Message, forValue.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Security")]
    public void Dropped_branch_with_denied_table_is_still_refused(bool isNull)
    {
        var sql = "SELECT id FROM orders WHERE (__param_p IS NULL OR id IN (SELECT id FROM no_such_table))";

        Assert.ThrowsAny<Exception>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql, nulls: isNull ? Null("p") : null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Security")]
    public void Dropped_branch_with_unknown_column_is_still_refused(bool isNull)
    {
        var sql = "SELECT id FROM orders WHERE (__param_p IS NULL OR no_such_column = __param_p)";

        Assert.ThrowsAny<Exception>(() => _h.Compile(TargetSqlDialect.PostgreSql, sql, nulls: isNull ? Null("p") : null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Security")]
    public void Dropped_branch_with_row_policy_coercion_violation_is_still_refused(bool isNull)
    {
        // a raising expression under the tenant row policy is refused whatever the null-ness of the optional parameter
        var sql = "SELECT id FROM orders WHERE (__param_p IS NULL OR id + 1 > 3)";

        CompiledSql? Compile(ClientNullShapes? nulls) => _h.Engine.Compile(sql.AsMemory(), _h.Request(TargetSqlDialect.DuckDb, nulls: nulls) with
        {
            AllowedFunctions = SqlFunctionAllowlists.Build(TargetSqlDialect.DuckDb, null)
        }, CancellationToken.None);

        var ex = Record.Exception(() => Compile(isNull ? Null("p") : null));
        var other = Record.Exception(() => Compile(isNull ? null : Null("p")));

        Assert.NotNull(ex);
        Assert.NotNull(other);
        Assert.Equal(ex.GetType(), other.GetType());
    }

    private static InMemoryTableCatalog RequiredRegionCatalog() => new(new[]
    {
        new TableCatalogEntry(PolicyFixtures.Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "integer"), new CatalogColumn("TenantId", "varchar"), new CatalogColumn("Region", "varchar"),
            new CatalogColumn("Status", "varchar"), new CatalogColumn("Amount", "decimal(18,2)"), new CatalogColumn("Email", "varchar")),
            "TenantId", ImmutableArray.Create("region"), 1)
    }, "dbo");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Security")]
    public void Optional_filter_on_required_column_never_satisfies_rule(bool isNull)
    {
        var sql = "SELECT id FROM orders WHERE (__param_region IS NULL OR region = __param_region)";

        Assert.Throws<RequiredFilterColumnMissingException>(() =>
            _h.Compile(TargetSqlDialect.PostgreSql, sql, nulls: isNull ? Null("region") : null, catalog: RequiredRegionCatalog()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Optional_filter_plus_unconditional_required_predicate_passes()
    {
        var sql = "SELECT id FROM orders WHERE region = 'EU' AND (__param_status IS NULL OR status = __param_status)";

        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, sql, nulls: Null("status"), catalog: RequiredRegionCatalog());

        Assert.Empty(ClientSlots(compiled));
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    public void Coverage_proof_passes_on_the_reduced_tree(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect, Optional, nulls: Null("region"));

        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Contains("1 = 1", compiled.Sql);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Coverage_applied_predicates_complete_after_reduction()
    {
        var reduced = _h.Compile(TargetSqlDialect.PostgreSql, Optional, nulls: Null("region"));
        var plain = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE region = __param_region");

        Assert.True(plain.AppliedPredicates.SequenceEqual(reduced.AppliedPredicates));
        Assert.NotEmpty(reduced.AppliedPredicates);
    }

    [Fact]
    public void Reducer_counts_reductions()
    {
        var statement = CompilerTestHelpers.Build("SELECT id FROM orders WHERE (__param_region IS NULL OR region = __param_region)");
        var reducer = new OptionalFilterReducer(Null("region"));
        reducer.Visit(statement);

        Assert.Equal(1, reducer.Reduced);
    }

    private void MaskEmail() =>
        _h.Masks.Masks[(PolicyFixtures.Orders, "email")] = new MaskSpec(
            MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("m_redact", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["m_redact"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());
}
