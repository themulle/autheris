using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using System.Security;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.ClientListHarness;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>REQ-SQL-128 WP-2: indexed element binding, exact count, element type from the list (PLAN-SQL-128 A-3).</summary>
public class ClientListBinderTests
{
    private readonly ClientListHarness _h = new();

    private sealed class ListCommand() : BinderMutationTests.RecCommand
    {
        // Oracle checks for a settable BindByName; the other binders ignore it.
        public bool BindByName { get; set; }
    }

    private static ClientListValue Strings(params string[] values) => ClientListValue.Create(values.Cast<object>().ToList(), SqlParameterType.String);

    private static ClientListValue Longs(params long[] values) => ClientListValue.Create(values.Select(v => (object)v).ToList(), SqlParameterType.Int64);

    private CompiledSql CompileIds(TargetSqlDialect dialect, int count) =>
        _h.Compile(dialect, "SELECT id FROM orders WHERE id IN (__param_ids)", Shapes(("ids", count)));

    private static ListCommand Bind(TargetSqlDialect dialect, CompiledSql compiled, object? ids)
    {
        var command = new ListCommand();
        CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["ids"] = ids });
        return command;
    }

    [Theory]
    [MemberData(nameof(ClientListHarness.DialectData), MemberType = typeof(ClientListHarness))]
    [InlineData(TargetSqlDialect.Databricks)]
    public void Binder_ReadsIndexedElement_WithListElementType(TargetSqlDialect dialect)
    {
        var compiled = CompileIds(dialect, 3);
        var command = Bind(dialect, compiled, Longs(10, 20, 30));

        var slots = ListSlots(compiled, "ids");
        for (int i = 0; i < slots.Count; i++)
        {
            var parameter = command.P(slots[i].Ordinal);
            Assert.Equal(DbType.Int64, parameter.DbType);
            Assert.Equal(10L * (i + 1), Convert.ToInt64(parameter.Value));
        }
    }

    [Fact]
    public void Binder_StringList_BindsEachElementAsString()
    {
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE status IN (__param_ids)", Shapes(("ids", 2)));
        var command = Bind(TargetSqlDialect.PostgreSql, compiled, Strings("a", "b"));
        var slots = ListSlots(compiled, "ids");
        Assert.Equal("a", command.P(slots[0].Ordinal).Value);
        Assert.Equal("b", command.P(slots[1].Ordinal).Value);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Binder_RefusesScalarForListSlot_ListForScalarSlot_AndCountMismatch()
    {
        var compiled = CompileIds(TargetSqlDialect.PostgreSql, 3);
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, Assert.Throws<ClientListException>(() => Bind(TargetSqlDialect.PostgreSql, compiled, "scalar")).Code);
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, Assert.Throws<ClientListException>(() => Bind(TargetSqlDialect.PostgreSql, compiled, null)).Code);
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, Assert.Throws<ClientListException>(() => Bind(TargetSqlDialect.PostgreSql, compiled, Longs(1, 2))).Code);
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, Assert.Throws<ClientListException>(() => Bind(TargetSqlDialect.PostgreSql, compiled, Longs(1, 2, 3, 4))).Code);

        var scalarSlot = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE id = __param_ids");
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, Assert.Throws<ClientListException>(() => Bind(TargetSqlDialect.PostgreSql, scalarSlot, Longs(1))).Code);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Binder_AbsentList_IsAMissingParameter()
    {
        var compiled = CompileIds(TargetSqlDialect.PostgreSql, 2);
        Assert.Throws<MissingClientParameterException>(() =>
            CompiledSqlBinders.For(TargetSqlDialect.PostgreSql).Bind(new ListCommand(), compiled, new Dictionary<string, object?>()));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Mutation_CountCheckOff_BindsALongerListSilentlyTruncated()
    {
        var compiled = CompileIds(TargetSqlDialect.PostgreSql, 2);
        using var _ = CompilerHooks.Use(new MutantHooks { DisabledControls = new HashSet<SecurityControl> { SecurityControl.ClientListCount } });

        var command = Bind(TargetSqlDialect.PostgreSql, compiled, Longs(1, 2, 3));   // control off: the third value is dropped without a word

        Assert.Equal(1L, Convert.ToInt64(command.P(ListSlots(compiled, "ids")[0].Ordinal).Value));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ScalarSlot_ReceivesClientListValue_IsRefusedBeforeInferType()
    {
        var compiled = new CompiledSql("SELECT 1", [new BoundParameter("$1", string.Empty, 0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "x")],
            TargetSqlDialect.PostgreSql, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "test");

        var ex = Assert.Throws<ClientListException>(() =>
            CompiledSqlBinders.For(TargetSqlDialect.PostgreSql).Bind(new ListCommand(), compiled, new Dictionary<string, object?> { ["x"] = Longs(1) }));
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, ex.Code);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ListSlot_WithoutCount_OrIndexBeyondValue_IsRefused()
    {
        var withoutCount = new CompiledSql("SELECT 1", [new BoundParameter("$1", string.Empty, 0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "x", null, 0, null)],
            TargetSqlDialect.PostgreSql, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "test");
        Assert.Throws<ClientListException>(() =>
            CompiledSqlBinders.For(TargetSqlDialect.PostgreSql).Bind(new ListCommand(), withoutCount, new Dictionary<string, object?> { ["x"] = Longs(1) }));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CheckedDmlBinding_ListCountMismatch_IsRefused_M12()
    {
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "DELETE FROM orders WHERE id IN (__param_ids)", Shapes(("ids", 2)), StatementPermissions.Delete);
        var binder = CompiledSqlBinders.For(TargetSqlDialect.PostgreSql);
        var values = new Dictionary<string, object?> { ["ids"] = Longs(1, 2, 3) };
        Assert.Throws<ClientListException>(() => binder.BindForCheckedExecution(new ListCommand(), compiled, values));
        values["ids"] = Longs(1, 2);
        binder.BindForCheckedExecution(new ListCommand(), compiled, values);
    }

    [Fact]
    public void Binder_Guid_BindsCanonicalText()
    {
        var guid = Guid.Parse("0F8FAD5B-D9CB-469F-A165-70867728950E");
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE status = __param_g");
        var command = new ListCommand();
        CompiledSqlBinders.For(TargetSqlDialect.PostgreSql).Bind(command, compiled, new Dictionary<string, object?> { ["g"] = guid });
        var slot = compiled.Parameters.Single(p => p.Origin == ParameterOrigin.ClientNamed);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", command.P(slot.Ordinal).Value);
    }

    // SEC-128-06 / M-6
    [Fact]
    [Trait("Category", "Security")]
    public void Oracle_EmptyStringElement_IsRejected()
    {
        var compiled = _h.Compile(TargetSqlDialect.Oracle, "SELECT id FROM orders WHERE status NOT IN (__param_ids)", Shapes(("ids", 2)));
        var ex = Assert.Throws<ClientListException>(() => Bind(TargetSqlDialect.Oracle, compiled, Strings("a", string.Empty)));
        Assert.Equal(GovernedSqlErrorCodes.ListParamNull, ex.Code);

        // the other dialects keep '' as an ordinary value
        var pg = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE status NOT IN (__param_ids)", Shapes(("ids", 2)));
        Bind(TargetSqlDialect.PostgreSql, pg, Strings("a", string.Empty));
        // a scalar client '' is not a list element and stays as it was on Oracle
        var scalar = _h.Compile(TargetSqlDialect.Oracle, "SELECT id FROM orders WHERE status = __param_s");
        var command = new ListCommand();
        CompiledSqlBinders.For(TargetSqlDialect.Oracle).Bind(command, scalar, new Dictionary<string, object?> { ["s"] = string.Empty });
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NulElement_IsRejectedOnPostgreSql_WithoutTheValue()
    {
        var compiled = _h.Compile(TargetSqlDialect.PostgreSql, "SELECT id FROM orders WHERE status IN (__param_ids)", Shapes(("ids", 2)));
        var ex = Assert.Throws<ClientListException>(() => Bind(TargetSqlDialect.PostgreSql, compiled, Strings("secret\0", "b")));
        Assert.DoesNotContain("secret", ex.Message);
    }

    // SEC-128-10 c
    [Fact]
    [Trait("Category", "Security")]
    public void BindClient_RefusesListIndexWithoutCount_OrIndexOutsideTheCount()
    {
        var context = new SqlEmitterContext(TargetSqlDialect.PostgreSql, DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql),
            new ParameterSource(FrozenDictionary<string, PolicyValue>.Empty, new Dictionary<string, object?>()), CancellationToken.None);
        Assert.Throws<ClientListException>(() => context.BindClient(new ParameterReference("l", null, true, 0, null)));
        Assert.Throws<ClientListException>(() => context.BindClient(new ParameterReference("l", null, true, null, 3)));
        Assert.Throws<ClientListException>(() => context.BindClient(new ParameterReference("l", null, true, 3, 3)));
        Assert.Throws<ClientListException>(() => context.BindClient(new ParameterReference("l", null, true, -1, 3)));
        context.BindClient(new ParameterReference("l", null, true, 2, 3));
    }

    // Oracle ordering and PostgreSQL positional ordering with tenant + policy + list (M-18)
    [Theory]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Ordinals_AreStable_WithTenantPolicyAndMultipleLists(TargetSqlDialect dialect)
    {
        var compiled = _h.Compile(dialect,
            "SELECT id FROM orders WHERE id IN (__param_a) AND status IN (__param_b) AND amount IN (__param_a)", Shapes(("a", 3), ("b", 2)));
        Assert.Equal(compiled.Parameters.Length, compiled.Parameters.Select(p => p.Ordinal).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, compiled.Parameters.Length), compiled.Parameters.Select(p => p.Ordinal));
        Assert.Equal(3, ListSlots(compiled, "a").Count);
        Assert.Equal(2, ListSlots(compiled, "b").Count);

        var command = new ListCommand();
        CompiledSqlBinders.For(dialect).Bind(command, compiled, new Dictionary<string, object?>
        {
            ["a"] = Longs(1, 2, 3),
            ["b"] = Strings("x", "y")
        });
        Assert.Equal(compiled.Parameters.Length, command.Parameters.Count);
        foreach (var slot in ListSlots(compiled, "a"))
        {
            Assert.Equal(slot.ListIndex!.Value + 1L, Convert.ToInt64(command.P(slot.Ordinal).Value));
        }

        foreach (var slot in ListSlots(compiled, "b"))
        {
            Assert.Equal(new[] { "x", "y" }[slot.ListIndex!.Value], command.P(slot.Ordinal).Value);
        }

        var tenant = compiled.Parameters.Single(p => p.Origin == ParameterOrigin.Tenant);
        Assert.Equal("acme", command.P(tenant.Ordinal).Value);
    }

    // Test 11: the total budget of tenant, literal and list binds is enforced at emission (SEC-128 plan test 11)
    [Fact]
    [Trait("Category", "Security")]
    public void Budget_TotalOfTenantLiteralsAndList_IsEnforcedAtEmission()
    {
        // SQL Server: 2,100 binds. 1,999 list items stay under the expansion cap (2,000); 150 distinct literals push the total past 2,100.
        string literals = string.Join(", ", Enumerable.Range(1, 150));
        string sql = $"SELECT id FROM orders WHERE id IN (__param_ids) AND amount IN ({literals})";
        var ex = Assert.Throws<SqlLimitExceededException>(() => _h.Compile(TargetSqlDialect.SqlServer, sql, Shapes(("ids", 1999))));
        Assert.Equal(SqlLimitKind.BindParameters, ex.Kind);
    }

    // ---- ClientListValue -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ClientListValue_ToString_ContainsNoValues()
    {
        string text = Strings("super-secret", "other").ToString();
        Assert.DoesNotContain("secret", text);
        Assert.Contains("Count = 2", text);
        Assert.Contains("String", text);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ClientListValue_Create_RefusesNullWrongTypeAndUnsupportedElementType()
    {
        Assert.Equal(GovernedSqlErrorCodes.ListParamNull, Assert.Throws<ClientListException>(() =>
            ClientListValue.Create(new object[] { "a", null! }, SqlParameterType.String)).Code);
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, Assert.Throws<ClientListException>(() =>
            ClientListValue.Create(new object[] { "a", 1L }, SqlParameterType.String)).Code);
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, Assert.Throws<ClientListException>(() =>
            ClientListValue.Create(new object[] { 1 }, SqlParameterType.Int64)).Code);
        Assert.Equal(GovernedSqlErrorCodes.ListParamType, Assert.Throws<ClientListException>(() =>
            ClientListValue.Create(new object[] { new byte[] { 1 } }, SqlParameterType.Binary)).Code);
        var value = Longs(5, 6);
        Assert.Equal(2, value.Count);
        Assert.Equal(6L, value[1]);
        Assert.Equal(SqlParameterType.Int64, value.ElementType);
    }

    [Fact]
    public void ClientListException_CarriesOnlyTheCode_AndAFixedMessage()
    {
        foreach (string code in new[]
                 {
                     GovernedSqlErrorCodes.ListParamPosition, GovernedSqlErrorCodes.ListParamType, GovernedSqlErrorCodes.ListParamNull,
                     GovernedSqlErrorCodes.ListParamTooLarge, GovernedSqlErrorCodes.ListParamUnsupported
                 })
        {
            var ex = new ClientListException(code);
            Assert.Equal(GovernedSqlErrorCodes.FixedMessage(code), ex.Message);
            Assert.NotEqual(GovernedSqlErrorCodes.FixedMessage("unknown"), ex.Message);
            Assert.DoesNotContain(ex.Message, c => char.IsDigit(c));
        }
    }
}
