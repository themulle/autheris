using System.Collections.Immutable;
using System.Collections.Frozen;
using System.Data;
using DuckDB.NET.Data;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

[Trait("Category", "SqlGate")]
public class CompiledSqlBinderTests
{
    private static CompiledSql Compiled(string sql, params BoundParameter[] ps) =>
        new(sql, ps.ToImmutableArray(), TargetSqlDialect.SqlServer, SqlStatementClass.Select,
            ImmutableArray<TrinoSqlEngine.Ast.Nodes.SecurityPredicateId>.Empty, "test");

    private static BoundParameter Param(int ord, object? value, SqlParameterType type, ParameterOrigin origin, string? source = null) =>
        new($"@p{ord}", $"@p{ord}", ord, value, type, origin, source);

    private readonly SqlServerCompiledSqlBinder _binder = new();

    [Fact]
    public void CanBind_OnlySqlServer()
    {
        Assert.True(_binder.CanBind(TargetSqlDialect.SqlServer));
        Assert.False(_binder.CanBind(TargetSqlDialect.PostgreSql));
        Assert.False(_binder.CanBind(TargetSqlDialect.Oracle));
    }

    [Fact]
    public void Bind_SetsCommandTextAndTypedParameters()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled("SELECT 1",
            Param(0, "acme", SqlParameterType.String, ParameterOrigin.Tenant, "__t"),
            Param(1, 42, SqlParameterType.Int32, ParameterOrigin.QueryLiteral),
            Param(2, 12.50m, SqlParameterType.Decimal, ParameterOrigin.QueryLiteral));

        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());

        Assert.Equal("SELECT 1", cmd.CommandText);
        Assert.Equal(3, cmd.Parameters.Count);
        Assert.Equal("@p0", cmd.Parameters[0].ParameterName);
        Assert.Equal(DbType.String, cmd.Parameters[0].DbType);
        Assert.Equal("acme", cmd.Parameters[0].Value);
        Assert.Equal(DbType.Int32, cmd.Parameters[1].DbType);
        Assert.Equal(DbType.Decimal, cmd.Parameters[2].DbType);
    }

    [Fact]
    public void Bind_ClientNamed_ResolvesFromClientValues()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled("SELECT 1", Param(0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo"));

        _binder.Bind(cmd, compiled, new Dictionary<string, object?> { ["foo"] = "bar" });

        Assert.Equal("bar", cmd.Parameters[0].Value);
    }

    [Fact]
    public void Bind_ClientNamed_Missing_FailsClosed()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled("SELECT 1", Param(0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo"));

        Assert.ThrowsAny<System.Security.SecurityException>(() => _binder.Bind(cmd, compiled, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Bind_ClientNamedValue_CannotAliasInternalParameter()
    {
        // SEC-ADG-19: a client key such as "@p0" or "__autheris_tenant" never reaches a tenant/policy slot.
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled("SELECT 1",
            Param(0, "acme", SqlParameterType.String, ParameterOrigin.Tenant, "__autheris_tenant"),
            Param(1, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo"));

        _binder.Bind(cmd, compiled, new Dictionary<string, object?>
        {
            ["@p0"] = "evil", ["p0"] = "evil", ["__autheris_tenant"] = "evil", ["foo"] = "bar"
        });

        Assert.Equal("acme", cmd.Parameters[0].Value);
        Assert.Equal("bar", cmd.Parameters[1].Value);
    }

    // ---- a client parameter named p0 is an ordinary name; it never reaches an internal slot ----

    public static IEnumerable<object[]> InternalSlotDialects() =>
        ClientListHarness.DialectData().Append(new object[] { TargetSqlDialect.Databricks });

    /// <summary>SqlServer, DuckDb and PostgreSql binders work on the recording command; Oracle needs BindByName on a provider command.</summary>
    private static DbCommandCompiledSqlBinder? RecordingBinderOf(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => new SqlServerCompiledSqlBinder(),
        TargetSqlDialect.DuckDb => new DuckDbCompiledSqlBinder(),
        TargetSqlDialect.PostgreSql => new PostgreSqlCompiledSqlBinder(),
        TargetSqlDialect.Databricks => new DatabricksCompiledSqlBinder(),
        _ => null
    };

    /// <summary>Compiles a statement with a row filter, a tenant guard and a column mask around the caller parameter <paramref name="callerName"/>.</summary>
    private static CompiledSql CompileWithInternalSlots(TargetSqlDialect dialect, string callerName = "p0")
    {
        var h = new ClientListHarness();
        h.RowFilters.NoPolicy.Remove(PolicyFixtures.Orders);
        h.RowFilters.Predicates[PolicyFixtures.Orders] = PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier("Region", true)])),
                BinaryOperator.Equal, new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) });
        h.Masks.Masks[(PolicyFixtures.Orders, "email")] = new MaskSpec(
            MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("m_redact", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["m_redact"] = new("[REDACTED]", SqlParameterType.String) }.ToFrozenDictionary());
        return h.Compile(dialect, $"SELECT id, email FROM orders WHERE id = __param_{callerName}");
    }

    private static List<object?> BoundValues(DbCommandCompiledSqlBinder binder, CompiledSql compiled, IReadOnlyDictionary<string, object?> client)
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        binder.Bind(cmd, compiled, client);
        return cmd.Parameters.Cast<System.Data.Common.DbParameter>().Select(p => p.Value).ToList();
    }

    private static Dictionary<string, object?> CallerP0(object? value) => new(StringComparer.OrdinalIgnoreCase) { ["p0"] = value };

    private static void AssertCallerValueOnlyInItsOwnSlot(TargetSqlDialect dialect, ParameterOrigin internalOrigin)
    {
        // The caller p0 is compared with the numeric id column, so the bind-time type check requires a numeric value.
        // A distinctive number still proves the value lands only in the caller's own slot.
        const long evil = 987_654_321L;
        var compiled = CompileWithInternalSlots(dialect);

        var client = Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.ClientNamed);
        Assert.Equal("p0", client.SourceName);
        Assert.Null(client.Value);   // a client slot never carries a value at compile time
        var internalSlots = compiled.Parameters.Where(p => p.Origin == internalOrigin).ToList();
        Assert.NotEmpty(internalSlots);
        Assert.All(internalSlots, s => Assert.NotEqual(client.Marker, s.Marker));
        Assert.Equal(compiled.Parameters.Length, compiled.Parameters.Select(p => p.Ordinal).Distinct().Count());

        if (RecordingBinderOf(dialect) is not { } binder)
        {
            return;
        }

        var values = BoundValues(binder, compiled, CallerP0(evil));
        Assert.Equal(compiled.Parameters.Length, values.Count);
        for (int i = 0; i < compiled.Parameters.Length; i++)
        {
            if (compiled.Parameters[i].Origin == ParameterOrigin.ClientNamed)
            {
                Assert.Equal(evil, values[i]);
            }
            else
            {
                Assert.NotEqual(evil, values[i]);
                Assert.Equal(compiled.Parameters[i].Value, values[i]);
            }
        }
    }

    [Fact]
    public void ClientParameterNamedP0_BindsCallerValue_ToItsOwnSlotOnly()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled("SELECT 1",
            Param(0, "EU", SqlParameterType.String, ParameterOrigin.Policy, "__pol_region"),
            Param(1, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "p0"),
            Param(2, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "p1"));

        _binder.Bind(cmd, compiled, new Dictionary<string, object?> { ["p0"] = "caller0", ["p1"] = "caller1" });

        Assert.Equal("EU", cmd.Parameters[0].Value);
        Assert.Equal("caller0", cmd.Parameters[1].Value);
        Assert.Equal("caller1", cmd.Parameters[2].Value);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ClientParameterNamedP0_NextToPolicySlotP0_PolicyValueUnchanged()
    {
        // The policy slot has ordinal 0 (marker p0) and the caller key is p0: the policy value stays.
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled("SELECT 1",
            Param(0, "EU", SqlParameterType.String, ParameterOrigin.Policy, "__pol_region"),
            Param(1, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "p0"));

        _binder.Bind(cmd, compiled, new Dictionary<string, object?> { ["p0"] = "evil", ["@p0"] = "evil" });

        Assert.Equal("EU", cmd.Parameters[0].Value);
        Assert.Equal("evil", cmd.Parameters[1].Value);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ClientParameterNamedUpperP0_BindsToOneClientSlot_NeverToAnInternalSlot()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled("SELECT 1",
            Param(0, "acme", SqlParameterType.String, ParameterOrigin.Tenant, "__autheris_tenant"),
            Param(1, "EU", SqlParameterType.String, ParameterOrigin.Policy, "__pol_region"),
            Param(2, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "P0"));

        _binder.Bind(cmd, compiled, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["p0"] = "evil" });

        Assert.Equal("acme", cmd.Parameters[0].Value);
        Assert.Equal("EU", cmd.Parameters[1].Value);
        Assert.Equal("evil", cmd.Parameters[2].Value);
        Assert.Single(cmd.Parameters.Cast<System.Data.Common.DbParameter>(), p => Equals(p.Value, "evil"));
    }

    [Theory]
    [MemberData(nameof(InternalSlotDialects))]
    [Trait("Category", "Security")]
    public void ClientParameterNamedP0_DoesNotSatisfyRowFilterBind_PerDialect(TargetSqlDialect dialect) =>
        AssertCallerValueOnlyInItsOwnSlot(dialect, ParameterOrigin.Policy);

    [Theory]
    [MemberData(nameof(InternalSlotDialects))]
    [Trait("Category", "Security")]
    public void ClientParameterNamedP0_DoesNotAlterTenantBind_PerDialect(TargetSqlDialect dialect) =>
        AssertCallerValueOnlyInItsOwnSlot(dialect, ParameterOrigin.Tenant);

    [Theory]
    [MemberData(nameof(InternalSlotDialects))]
    [Trait("Category", "Security")]
    public void ClientParameterNamedP0_DoesNotAlterMaskBind_PerDialect(TargetSqlDialect dialect) =>
        AssertCallerValueOnlyInItsOwnSlot(dialect, ParameterOrigin.Mask);

    [Theory]
    [MemberData(nameof(InternalSlotDialects))]
    [Trait("Category", "Security")]
    public void ClientParameterNamedP0_DoesNotAlterPageOffsetBind_PerDialect(TargetSqlDialect dialect)
    {
        // The page offset slot is created outside the statement compile; model it next to a caller p0 with the same shape.
        var compiled = Compiled("SELECT 1",
            Param(0, 20L, SqlParameterType.Int64, ParameterOrigin.PageOffset),
            Param(1, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "p0")) with { Dialect = dialect };
        if (RecordingBinderOf(dialect) is not { } binder)
        {
            Assert.Equal(20L, compiled.Parameters[0].Value);
            Assert.Null(compiled.Parameters[1].Value);
            return;
        }

        var values = BoundValues(binder, compiled, CallerP0("evil"));
        Assert.Equal(20L, Convert.ToInt64(values[0]));
        Assert.Equal("evil", values[1]);
    }

    [Theory]
    [MemberData(nameof(InternalSlotDialects))]
    [Trait("Category", "Security")]
    public void ClientParameterNamedP0_MissingValue_FailsClosed_NoFallbackToInternalSlot(TargetSqlDialect dialect)
    {
        var compiled = CompileWithInternalSlots(dialect);
        if (RecordingBinderOf(dialect) is not { } binder)
        {
            Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.ClientNamed);
            return;
        }

        // Only unrelated keys (including the marker spellings of the internal slots) are supplied.
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var client = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["p1"] = "x", ["p2"] = "y", ["__autheris_tenant"] = "z" };
        Assert.ThrowsAny<System.Security.SecurityException>(() => binder.Bind(cmd, compiled, client));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [Trait("Category", "Security")]
    public void ClientParameterNamedP0_InUnboundEmitter_NeverPrintsCallerName(TargetSqlDialect dialect)
    {
        // The unbound emitter is diagnostic only: a synthetic caller reference becomes the next ordinal marker, never its own name.
        var statement = CompilerTestHelpers.Build("SELECT 1 WHERE 1 = __param_callerzz AND 2 = __param_p7");
        var sql = SqlDialectGeneratorFactory.GetGenerator(dialect).GenerateSql(statement);

        Assert.DoesNotContain("callerzz", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("p7", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__param_", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_OnCommandWithExistingParameters_Throws()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        cmd.Parameters.Add(new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecParameter { ParameterName = "@x", Value = 1 });
        Assert.Throws<InvalidOperationException>(() => _binder.Bind(cmd, Compiled("SELECT 1"), new Dictionary<string, object?>()));
    }

    [Fact]
    public void Bind_NullValue_BindsDbNull()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        _binder.Bind(cmd, Compiled("SELECT 1", Param(0, null, SqlParameterType.Null, ParameterOrigin.QueryLiteral)), new Dictionary<string, object?>());
        Assert.Equal(DBNull.Value, cmd.Parameters[0].Value);
    }

    [Fact]
    public void Bind_EveryParameter_IsDistinctlyNamed_OneToOneWithMarkers()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var compiled = Compiled("SELECT 1",
            Param(0, 1, SqlParameterType.Int32, ParameterOrigin.QueryLiteral),
            Param(1, 2, SqlParameterType.Int32, ParameterOrigin.QueryLiteral));
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        Assert.Equal(new[] { "@p0", "@p1" }, cmd.Parameters.Cast<System.Data.Common.DbParameter>().Select(p => p.ParameterName));
    }

    // ---- CR-ADG-43: a statement with a row-count check runs only through the checked executor ----

    private static CompiledSql Checked(string sql, int expected, params BoundParameter[] ps) =>
        new(sql, ps.ToImmutableArray(), TargetSqlDialect.SqlServer, SqlStatementClass.Insert,
            ImmutableArray<TrinoSqlEngine.Ast.Nodes.SecurityPredicateId>.Empty, "test") { ExpectedAffectedRows = expected };

    public static IEnumerable<object[]> AllBinders() => new[]
    {
        new object[] { TargetSqlDialect.SqlServer }, new object[] { TargetSqlDialect.DuckDb }, new object[] { TargetSqlDialect.PostgreSql },
        new object[] { TargetSqlDialect.Oracle }, new object[] { TargetSqlDialect.Databricks }
    };

    private static DbCommandCompiledSqlBinder BinderOf(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => new SqlServerCompiledSqlBinder(),
        TargetSqlDialect.DuckDb => new DuckDbCompiledSqlBinder(),
        TargetSqlDialect.PostgreSql => new PostgreSqlCompiledSqlBinder(),
        TargetSqlDialect.Oracle => new OracleCompiledSqlBinder(),
        _ => new DatabricksCompiledSqlBinder()
    };

    [Theory]
    [MemberData(nameof(AllBinders))]
    public void Bind_OfAStatementWithARowCountCheck_IsRefused_WithATypedError_AndTouchesNothing(TargetSqlDialect dialect)
    {
        var compiled = Checked("INSERT INTO t SELECT 1", 1) with { Dialect = dialect };
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        var ex = Assert.Throws<CheckedExecutionRequiredException>(() => BinderOf(dialect).Bind(cmd, compiled, new Dictionary<string, object?>()));
        Assert.Equal(GovernedSqlErrorCodes.CheckedExecutionRequired, ex.Code);
        Assert.Equal("DML_CHECKED_EXECUTION_REQUIRED", ex.Code);
        Assert.Equal(dialect, ex.Dialect);
        Assert.Null(ex.InnerException);
        Assert.Equal(string.Empty, cmd.CommandText);   // nothing reached the command
        Assert.Empty(cmd.Parameters);
    }

    [Fact]
    public void ExpectedAffectedRows_CannotBeSetFromOutsideTheAssembly()
    {
        // CR-ADG-45: a public init accessor would let `compiled with { ExpectedAffectedRows = null }` strip the requirement.
        var property = typeof(CompiledSql).GetProperty(nameof(CompiledSql.ExpectedAffectedRows))!;
        Assert.True(property.GetMethod!.IsPublic);
        var setter = property.SetMethod!;
        Assert.False(setter.IsPublic);
        Assert.False(setter.IsFamily);
        Assert.True(setter.IsAssembly || setter.IsFamilyAndAssembly);
        Assert.DoesNotContain(typeof(CompiledSql).GetConstructors(), c => c.GetParameters().Any(p => p.Name == "ExpectedAffectedRows"));
    }

    [Theory]
    [MemberData(nameof(AllBinders))]
    public void Bind_OfAStatementWithTheReservedCheckAlias_IsRefusedEvenWithoutTheFlag(TargetSqlDialect dialect)
    {
        // CR-ADG-45 defense in depth: the binder derives the requirement from the SQL text as well.
        foreach (var sql in new[]
                 {
                     "INSERT INTO t (a) SELECT \"autheris_ins\".\"a\" FROM (VALUES (1)) AS \"autheris_ins\" (\"a\")",
                     "INSERT INTO t (a) SELECT [autheris_ins].[a] FROM (VALUES (1)) AS [autheris_ins] ([a])",
                     "INSERT INTO t (a) SELECT `autheris_ins`.`a` FROM (VALUES (1)) AS `autheris_ins` (`a`)",
                     "INSERT INTO t (a) SELECT AUTHERIS_INS.a FROM x AS AUTHERIS_INS"
                 })
        {
            var compiled = Compiled(sql) with { Dialect = dialect, StatementClass = SqlStatementClass.Insert };
            Assert.False(compiled.RequiresRowCountCheck);
            using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
            var ex = Assert.Throws<CheckedExecutionRequiredException>(() => BinderOf(dialect).Bind(cmd, compiled, new Dictionary<string, object?>()));
            Assert.Equal(dialect, ex.Dialect);
            Assert.Equal(string.Empty, cmd.CommandText);
            Assert.Empty(cmd.Parameters);
        }
    }

    [Fact]
    public void Bind_OfAStatementWithoutARowCountCheck_IsUnchanged()
    {
        using var cmd = new TrinoSqlEngine.Tests.Compiler.BinderMutationTests.RecCommand();
        _binder.Bind(cmd, Compiled("DELETE FROM t"), new Dictionary<string, object?>());
        Assert.Equal("DELETE FROM t", cmd.CommandText);
    }

    [Theory]
    [InlineData(null, "1,2,3,4")]
    [InlineData("EU", "1,2,3")]
    [InlineData("US", "4")]
    [InlineData("ZZ", "")]
    public async Task Optional_filter_executes_on_duckdb_with_and_without_a_value(string? region, string expected)
    {
        await using var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE SCHEMA dbo; CREATE TABLE dbo.\"Orders\" (\"Id\" INTEGER, \"TenantId\" VARCHAR, \"Region\" VARCHAR, \"Status\" VARCHAR, \"Amount\" DECIMAL(18,2), \"Email\" VARCHAR);" +
                "INSERT INTO dbo.\"Orders\" VALUES (1,'acme','EU','b',10.0,'a'),(2,'acme','EU','c',30.0,'b'),(3,'acme','EU','a',20.0,'c'),(4,'acme','US','d',5.0,'d'),(5,'other','EU','z',99.0,'e');";
            await create.ExecuteNonQueryAsync();
        }

        const string sql = "SELECT id FROM orders WHERE (__param_region IS NULL OR region = __param_region) ORDER BY id";
        var nulls = region is null ? ClientNullShapes.Create(["region"]) : ClientNullShapes.Empty;
        var compiled = new ClientListHarness().Compile(TargetSqlDialect.DuckDb, sql, nulls: nulls);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["region"] = region };

        await using var command = connection.CreateCommand();
        new DuckDbCompiledSqlBinder().Bind(command, compiled, values);
        var ids = new List<long>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ids.Add(Convert.ToInt64(reader.GetValue(0)));
            }
        }

        // the tenant predicate is injected whatever the filter: the other tenant's row 5 never appears
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(',').Select(long.Parse).ToArray(), ids);
    }

    private static async Task<DuckDBConnection> OpenTableAsync()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE t (a INTEGER)";
        await create.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<long> CountAsync(DuckDBConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static BoundParameter DuckParam(int ord, object? value) =>
        new($"$p{ord}", $"p{ord}", ord, value, SqlParameterType.Int32, ParameterOrigin.QueryLiteral, null);

    private static CompiledSql DuckChecked(string sql, int expected, params BoundParameter[] ps) =>
        Checked(sql, expected, ps) with { Dialect = TargetSqlDialect.DuckDb };

    [Fact]
    public async Task CheckedExecutor_CommitsWhenTheAffectedCountMatches()
    {
        await using var connection = await OpenTableAsync();
        var compiled = DuckChecked("INSERT INTO t (a) SELECT 1 WHERE $p0 = 1", 1, DuckParam(0, 1));
        int affected = await new CheckedDmlExecutor(new DuckDbCompiledSqlBinder()).ExecuteAsync(connection, compiled, new Dictionary<string, object?>(), IsolationLevel.Unspecified);
        Assert.Equal(1, affected);
        Assert.Equal(1, await CountAsync(connection));
    }

    [Fact]
    public async Task CheckedExecutor_RollsBackAndThrowsTheTypedError_WhenTheCountDiffers()
    {
        await using var connection = await OpenTableAsync();
        // two rows are written but only one is expected: the transaction is rolled back, nothing stays
        var compiled = DuckChecked("INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2", 1);
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() => new CheckedDmlExecutor(new DuckDbCompiledSqlBinder()).ExecuteAsync(connection, compiled, new Dictionary<string, object?>(), IsolationLevel.Unspecified));
        Assert.Equal(0, await CountAsync(connection));

        // the policy filtered a row out: zero rows written, one expected
        var filtered = DuckChecked("INSERT INTO t (a) SELECT 1 WHERE $p0 = 2", 1, DuckParam(0, 1));
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() => new CheckedDmlExecutor(new DuckDbCompiledSqlBinder()).ExecuteAsync(connection, filtered, new Dictionary<string, object?>(), IsolationLevel.Unspecified));
        Assert.Equal(0, await CountAsync(connection));
    }

    [Fact]
    public async Task CheckedExecutor_RollsBackOnAnExecutionError_AndRunsStatementsWithoutACheckToo()
    {
        await using var connection = await OpenTableAsync();
        var broken = Checked("INSERT INTO nope (a) SELECT 1", 1);
        await Assert.ThrowsAnyAsync<Exception>(() => new CheckedDmlExecutor(_binder).ExecuteAsync(connection, broken, new Dictionary<string, object?>(), IsolationLevel.Unspecified));
        Assert.Equal(0, await CountAsync(connection));

        int affected = await new CheckedDmlExecutor(_binder).ExecuteAsync(connection, Compiled("INSERT INTO t (a) SELECT 5"), new Dictionary<string, object?>(), IsolationLevel.Unspecified);
        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task CheckedExecutor_RefusesACompiledStatementOfAnotherDialect()
    {
        await using var connection = await OpenTableAsync();
        var compiled = Checked("INSERT INTO t (a) SELECT 1", 1) with { Dialect = TargetSqlDialect.PostgreSql };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CheckedDmlExecutor(_binder).ExecuteAsync(connection, compiled, new Dictionary<string, object?>(), IsolationLevel.Unspecified));
        Assert.Equal(0, await CountAsync(connection));
    }
}
