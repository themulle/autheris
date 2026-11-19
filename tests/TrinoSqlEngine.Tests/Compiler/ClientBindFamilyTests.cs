using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// A client value that meets a typed column under a tenant predicate is checked against the column family at bind time, before any
/// command exists (issue 847): a value that does not convert would raise on whichever row is evaluated.
/// </summary>
[Trait("Category", "Security")]
public class ClientBindFamilyTests
{
    private const string BranchShape = "SELECT id FROM orders WHERE CASE WHEN id = 42 THEN id = __param_p ELSE 1 = 0 END";

    private static void Bind(TargetSqlDialect dialect, CompiledSql compiled, Dictionary<string, object?> values) =>
        CompiledSqlBinders.For(dialect).Bind(new NamedBindCommand(), compiled, values);

    public static IEnumerable<object[]> Dialects() => ClientListHarness.DialectData();

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Text_value_for_a_numeric_slot_is_refused_at_bind(TargetSqlDialect dialect)
    {
        var compiled = new ClientListHarness().Compile(dialect, BranchShape);

        var ex = Assert.Throws<SecurityException>(() => Bind(dialect, compiled, new() { ["p"] = "abc" }));
        Assert.Equal(RowPolicyErrorGuard.Message, ex.Message);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Numeric_values_for_a_numeric_slot_are_bound(TargetSqlDialect dialect)
    {
        var compiled = new ClientListHarness().Compile(dialect, BranchShape);

        Bind(dialect, compiled, new() { ["p"] = 42L });
        Bind(dialect, compiled, new() { ["p"] = "42" });
        Bind(dialect, compiled, new() { ["p"] = 4.5m });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Numeric_list_with_a_text_element_is_refused_at_bind(TargetSqlDialect dialect)
    {
        var h = new ClientListHarness();
        var compiled = h.Compile(dialect, "SELECT id FROM orders WHERE id IN (__param_ids)", ClientListHarness.Shapes(("ids", 2)));
        var bad = ClientListValue.Create(new object[] { "1", "abc" }, SqlParameterType.String);
        var good = ClientListValue.Create(new object[] { 1L, 2L }, SqlParameterType.Int64);

        var ex = Assert.Throws<SecurityException>(() => Bind(dialect, compiled, new() { ["ids"] = bad }));
        Assert.Equal(RowPolicyErrorGuard.Message, ex.Message);
        Bind(dialect, compiled, new() { ["ids"] = good });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Temporal_slot_checks_iso_text(TargetSqlDialect dialect)
    {
        var compiled = new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE extract(year FROM __param_d) = 2020 OR id = 1");

        Assert.Throws<SecurityException>(() => Bind(dialect, compiled, new() { ["d"] = "12:30" }));
        Assert.Throws<SecurityException>(() => Bind(dialect, compiled, new() { ["d"] = "2020-13-45" }));
        Bind(dialect, compiled, new() { ["d"] = "2020-01-31" });
        Bind(dialect, compiled, new() { ["d"] = new DateTime(2020, 1, 31) });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void A_bind_that_needs_two_families_is_refused_at_compile(TargetSqlDialect dialect)
    {
        Assert.Throws<SecurityException>(() => new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE id = __param_p AND status = __param_p AND extract(year FROM __param_p) = 1"));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Text_slot_needs_no_family(TargetSqlDialect dialect)
    {
        var compiled = new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE status = __param_p");

        Bind(dialect, compiled, new() { ["p"] = "abc" });
    }
}

/// <summary>A recording command with a settable BindByName: Oracle checks for it, the other binders ignore it.</summary>
internal sealed class NamedBindCommand() : BinderMutationTests.RecCommand
{
    public bool BindByName { get; set; }
}
