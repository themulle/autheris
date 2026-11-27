using System.Globalization;
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

        var ex = Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["p"] = "abc" }));
        Assert.Equal("SQL_DATA_ERROR", ex.Code);
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

        var ex = Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["ids"] = bad }));
        Assert.Equal("SQL_DATA_ERROR", ex.Code);
        Bind(dialect, compiled, new() { ["ids"] = good });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Temporal_slot_checks_iso_text(TargetSqlDialect dialect)
    {
        var compiled = new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE extract(year FROM __param_d) = 2020 OR id = 1");

        Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["d"] = "12:30" }));
        Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["d"] = "2020-13-45" }));
        Bind(dialect, compiled, new() { ["d"] = "2020-01-31" });
        Bind(dialect, compiled, new() { ["d"] = new DateTime(2020, 1, 31) });
    }

    private const string TemporalShape = "SELECT id FROM orders WHERE extract(year FROM __param_d) = 2020 OR id = 1";

    private static CompiledSql WithColumnType(CompiledSql compiled, string columnType) =>
        compiled with { Parameters = [.. compiled.Parameters.Select(p => p.RequiredFamily is null ? p : p with { ColumnType = columnType })] };

    private static object? BoundTemporal(TargetSqlDialect dialect, CompiledSql compiled, object? input)
    {
        var command = new NamedBindCommand();
        CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?> { ["d"] = input });
        int slot = compiled.Parameters.IndexOf(compiled.Parameters.Single(p => p.SourceName == "d"));
        return command.P(slot).Value;
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Temporal_slot_accepts_utc_z_and_offset_forms(TargetSqlDialect dialect)
    {
        var compiled = new ClientListHarness().Compile(dialect, TemporalShape);

        foreach (string text in new[] { "2026-01-01T00:00:00Z", "2026-01-01 00:00:00Z", "2026-01-01T00:00:00.123Z", "2026-01-01T00:00:00+02:00", "2026-01-01T00:00:00-14:00", "2026-01-01 00:00:00.5+00:00" })
        {
            Bind(dialect, compiled, new() { ["d"] = text });
        }
    }

    [Fact]
    public void Temporal_offset_value_is_bound_as_utc_timestamp_for_a_column_without_zone()
    {
        var dialect = TargetSqlDialect.PostgreSql;
        var compiled = WithColumnType(new ClientListHarness().Compile(dialect, TemporalShape), "timestamp");

        Assert.Equal(ColumnTemporalKind.WithoutZone, ColumnBindAdapter.TemporalKind(dialect, Assert.Single(compiled.Parameters, p => p.RequiredFamily is not null).ColumnType));
        var bound = Assert.IsType<DateTime>(BoundTemporal(dialect, compiled, "2026-01-01T00:00:00+02:00"));
        Assert.Equal(new DateTime(2025, 12, 31, 22, 0, 0), bound);
        Assert.Equal(DateTimeKind.Unspecified, bound.Kind);
    }

    [Theory]
    [InlineData("timestamptz")]
    [InlineData(null)]
    public void Temporal_offset_value_is_bound_as_utc_datetimeoffset_for_a_zoned_or_unknown_column(string? columnType)
    {
        var dialect = TargetSqlDialect.PostgreSql;
        var compiled = new ClientListHarness().Compile(dialect, TemporalShape);
        if (columnType is not null) compiled = WithColumnType(compiled, columnType);

        var bound = Assert.IsType<DateTimeOffset>(BoundTemporal(dialect, compiled, "2026-01-01T00:00:00+02:00"));
        Assert.Equal(TimeSpan.Zero, bound.Offset);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero), bound);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Assert.IsType<DateTimeOffset>(BoundTemporal(dialect, compiled, "2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void Temporal_date_only_is_bound_as_date()
    {
        var dialect = TargetSqlDialect.PostgreSql;
        var compiled = new ClientListHarness().Compile(dialect, TemporalShape);

        var command = new NamedBindCommand();
        CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?> { ["d"] = "2026-01-02" });
        int slot = compiled.Parameters.IndexOf(compiled.Parameters.Single(p => p.SourceName == "d"));
        Assert.Equal(System.Data.DbType.Date, command.P(slot).DbType);
        Assert.Equal(new DateTime(2026, 1, 2), command.P(slot).Value);
    }

    [Fact]
    public void Temporal_wrong_form_is_sql_data_error_not_row_policy_code()
    {
        var dialect = TargetSqlDialect.PostgreSql;
        var compiled = new ClientListHarness().Compile(dialect, TemporalShape);

        var ex = Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["d"] = "01/02/2026" }));
        Assert.Equal("SQL_DATA_ERROR", ex.Code);
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage("SQL_DATA_ERROR"), ex.Message);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Temporal_wrong_form_is_sql_data_error_without_value_echo()
    {
        var dialect = TargetSqlDialect.PostgreSql;
        var compiled = new ClientListHarness().Compile(dialect, TemporalShape);
        const string input = "secret-value-01/02/2026";

        var ex = Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["d"] = input }));
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
        Assert.Empty(ex.Data);
    }

    [Theory]
    [InlineData("01/02/2026")]
    [InlineData("2026-1-2")]
    [InlineData(" 2026-01-02")]
    [InlineData("2026-01-02 ")]
    [InlineData("2026-01-02T00:00:00 ")]
    [InlineData("2026-01-02T00:00:00+0200")]
    [InlineData("2026-01-02T00:00:00+25:00")]
    [InlineData("2026-01-02T00:00:00+14:30")]
    [InlineData("2026-01-02T00:00:00z")]
    [InlineData("2026-01-02T00:00:00 Z")]
    [InlineData("2026-01-02T00:00:00.12345678901234567890123456789Z")]
    [InlineData("0001-01-01T00:00:00+02:00")]
    [InlineData("9999-12-31T23:59:59-02:00")]
    [Trait("Category", "Security")]
    public void Temporal_ambiguous_formats_are_refused(string text)
    {
        Assert.False(ClientBindFamilyCheck.TryConform(BindFamily.Temporal, text, ColumnTemporalKind.Unknown, out _));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [Trait("Category", "Security")]
    public void Temporal_parse_is_culture_invariant(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.True(ClientBindFamilyCheck.TryConform(BindFamily.Temporal, "2026-01-01T10:20:30+02:00", ColumnTemporalKind.Unknown, out var conformed));
            Assert.Equal(new DateTimeOffset(2026, 1, 1, 8, 20, 30, TimeSpan.Zero), Assert.IsType<DateTimeOffset>(conformed));
            Assert.True(ClientBindFamilyCheck.TryConform(BindFamily.Temporal, "2026-01-01 10:20:30.5", ColumnTemporalKind.Unknown, out conformed));
            Assert.Equal(new DateTime(2026, 1, 1, 10, 20, 30, 500), Assert.IsType<DateTime>(conformed));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Temporal_value_without_offset_never_assumes_local_time()
    {
        Assert.True(ClientBindFamilyCheck.TryConform(BindFamily.Temporal, "2026-06-01T12:00:00", ColumnTemporalKind.WithZone, out var conformed));
        var value = Assert.IsType<DateTime>(conformed);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0), value);
    }

    [Theory]
    [InlineData(BindFamily.Numeric, "42", true)]
    [InlineData(BindFamily.Numeric, "abc", false)]
    [InlineData(BindFamily.Numeric, 5L, true)]
    [InlineData(BindFamily.Numeric, true, false)]
    [InlineData(BindFamily.Temporal, "12:30", false)]
    [InlineData(BindFamily.Temporal, 5L, false)]
    [InlineData(BindFamily.Boolean, true, true)]
    [InlineData(BindFamily.Boolean, "true", false)]
    [InlineData(BindFamily.Text, "x", true)]
    [InlineData(BindFamily.Text, 5L, false)]
    [InlineData(BindFamily.Text, null, true)]
    [Trait("Category", "Security")]
    public void TryConform_matches_Conforms_for_non_temporal_families(BindFamily family, object? value, bool expected)
    {
        Assert.Equal(expected, ClientBindFamilyCheck.Conforms(family, value));
        Assert.Equal(expected, ClientBindFamilyCheck.TryConform(family, value, ColumnTemporalKind.WithoutZone, out var conformed));
        if (family != BindFamily.Temporal) Assert.Same(value, conformed);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Temporal_list_elements_are_bound_typed()
    {
        var dialect = TargetSqlDialect.PostgreSql;
        var plain = new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE id IN (__param_ids)", ClientListHarness.Shapes(("ids", 2)));
        var compiled = plain with { Parameters = [.. plain.Parameters.Select(p => p.ListIndex is null ? p : p with { RequiredFamily = BindFamily.Temporal })] };
        var good = ClientListValue.Create(new object[] { "2026-01-01T00:00:00+02:00", "2026-01-02" }, SqlParameterType.String);
        var command = new NamedBindCommand();

        CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?> { ["ids"] = good });

        var typed = Enumerable.Range(0, compiled.Parameters.Length).Select(i => command.P(i).Value).Where(v => v is DateTime or DateTimeOffset or DateOnly).ToList();
        Assert.Equal(2, typed.Count);
        Assert.Contains(new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero), typed);
        Assert.Contains(new DateTime(2026, 1, 2), typed);

        var bad = ClientListValue.Create(new object[] { "2026-01-01T00:00:00Z", "nope" }, SqlParameterType.String);
        var ex = Assert.Throws<GovernedSqlException>(() => CompiledSqlBinders.For(dialect).Bind(new NamedBindCommand(), compiled, new Dictionary<string, object?> { ["ids"] = bad }));
        Assert.Equal("SQL_DATA_ERROR", ex.Code);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Embedded_nul_in_conformed_value_is_refused()
    {
        // A temporal text never carries a NUL (the parse refuses it); a text slot value does and is refused by the NUL check after conformance.
        Assert.False(ClientBindFamilyCheck.TryConform(BindFamily.Temporal, "2026-01-01\0", ColumnTemporalKind.Unknown, out _));
        var dialect = TargetSqlDialect.PostgreSql;
        var compiled = new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE CASE WHEN id = 42 THEN status = __param_p ELSE 1 = 0 END");
        Assert.Throws<SecurityException>(() => Bind(dialect, compiled, new() { ["p"] = "a\0b" }));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Row_policy_allowlist_disabled_mutation_removes_bind_refusal()
    {
        var dialect = TargetSqlDialect.PostgreSql;
        var compiled = new ClientListHarness().Compile(dialect, BranchShape);
        Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["p"] = "abc" }));

        using var seam = CompilerHooks.Use(new MutantHooks { DisabledControls = new HashSet<SecurityControl> { SecurityControl.RowPolicyExpressionAllowlist } });
        Bind(dialect, compiled, new() { ["p"] = "abc" });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void A_bind_that_needs_two_families_is_refused_at_compile(TargetSqlDialect dialect)
    {
        Assert.Throws<RowPolicyExpressionRejectedException>(() => new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE id = __param_p AND status = __param_p AND extract(year FROM __param_p) = 1"));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Text_slot_requires_a_string_value(TargetSqlDialect dialect)
    {
        var compiled = new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE CASE WHEN id = 42 THEN status = __param_p ELSE 1 = 0 END");

        Bind(dialect, compiled, new() { ["p"] = "abc" });
        var ex = Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["p"] = 5L }));
        Assert.Equal("SQL_DATA_ERROR", ex.Code);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Text_literal_against_a_bind_makes_the_slot_text(TargetSqlDialect dialect)
    {
        var compiled = new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE __param_p = 'x'");

        Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["p"] = 1L }));
        Bind(dialect, compiled, new() { ["p"] = "x" });
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Comparison_of_binds_only_is_refused_at_compile(TargetSqlDialect dialect)
    {
        Assert.Throws<RowPolicyExpressionRejectedException>(() => new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE __param_a = __param_b"));
        Assert.Throws<RowPolicyExpressionRejectedException>(() => new ClientListHarness().Compile(dialect, "SELECT id FROM orders WHERE __param_a = NULL"));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Bare_bind_operand_of_and_or_not_must_be_boolean(TargetSqlDialect dialect)
    {
        foreach (string shape in new[]
        {
            "SELECT id FROM orders WHERE CASE WHEN id = 42 THEN __param_p AND id = 1 ELSE 1 = 0 END",
            "SELECT id FROM orders WHERE id = 1 OR __param_p",
            "SELECT id FROM orders WHERE NOT __param_p"
        })
        {
            var compiled = new ClientListHarness().Compile(dialect, shape);
            Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["p"] = "abc" }));
            Bind(dialect, compiled, new() { ["p"] = true });
        }
    }

    [Fact]
    public void Databricks_binder_applies_the_same_checks()
    {
        const TargetSqlDialect dialect = TargetSqlDialect.Databricks;
        var compiled = new ClientListHarness().Compile(dialect, BranchShape);

        Assert.Throws<GovernedSqlException>(() => Bind(dialect, compiled, new() { ["p"] = "abc" }));
        Bind(dialect, compiled, new() { ["p"] = "42" });
    }
}

/// <summary>A recording command with a settable BindByName: Oracle checks for it, the other binders ignore it.</summary>
internal sealed class NamedBindCommand() : BinderMutationTests.RecCommand
{
    public bool BindByName { get; set; }
}
