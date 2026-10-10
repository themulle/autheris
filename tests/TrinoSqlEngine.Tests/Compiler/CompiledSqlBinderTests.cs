using System.Collections.Immutable;
using System.Data;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

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
        using var cmd = new SqliteCommand();
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
        using var cmd = new SqliteCommand();
        var compiled = Compiled("SELECT 1", Param(0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo"));

        _binder.Bind(cmd, compiled, new Dictionary<string, object?> { ["foo"] = "bar" });

        Assert.Equal("bar", cmd.Parameters[0].Value);
    }

    [Fact]
    public void Bind_ClientNamed_Missing_FailsClosed()
    {
        using var cmd = new SqliteCommand();
        var compiled = Compiled("SELECT 1", Param(0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "foo"));

        Assert.ThrowsAny<System.Security.SecurityException>(() => _binder.Bind(cmd, compiled, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Bind_ClientNamedValue_CannotAliasInternalParameter()
    {
        // SEC-ADG-19: a client key such as "@p0" or "__autheris_tenant" never reaches a tenant/policy slot.
        using var cmd = new SqliteCommand();
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

    [Fact]
    public void Bind_OnCommandWithExistingParameters_Throws()
    {
        using var cmd = new SqliteCommand();
        cmd.Parameters.AddWithValue("@x", 1);
        Assert.Throws<InvalidOperationException>(() => _binder.Bind(cmd, Compiled("SELECT 1"), new Dictionary<string, object?>()));
    }

    [Fact]
    public void Bind_NullValue_BindsDbNull()
    {
        using var cmd = new SqliteCommand();
        _binder.Bind(cmd, Compiled("SELECT 1", Param(0, null, SqlParameterType.Null, ParameterOrigin.QueryLiteral)), new Dictionary<string, object?>());
        Assert.Equal(DBNull.Value, cmd.Parameters[0].Value);
    }

    [Fact]
    public void Bind_EveryParameter_IsDistinctlyNamed_OneToOneWithMarkers()
    {
        using var cmd = new SqliteCommand();
        var compiled = Compiled("SELECT 1",
            Param(0, 1, SqlParameterType.Int32, ParameterOrigin.QueryLiteral),
            Param(1, 2, SqlParameterType.Int32, ParameterOrigin.QueryLiteral));
        _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
        Assert.Equal(new[] { "@p0", "@p1" }, cmd.Parameters.Cast<SqliteParameter>().Select(p => p.ParameterName));
    }

    // ---- CR-ADG-43: a statement with a row-count check runs only through the checked executor ----

    private static CompiledSql Checked(string sql, int expected, params BoundParameter[] ps) =>
        new(sql, ps.ToImmutableArray(), TargetSqlDialect.SqlServer, SqlStatementClass.Insert,
            ImmutableArray<TrinoSqlEngine.Ast.Nodes.SecurityPredicateId>.Empty, "test", expected);

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
        using var cmd = new SqliteCommand();
        var ex = Assert.Throws<CheckedExecutionRequiredException>(() => BinderOf(dialect).Bind(cmd, compiled, new Dictionary<string, object?>()));
        Assert.Equal(GovernedSqlErrorCodes.CheckedExecutionRequired, ex.Code);
        Assert.Equal("DML_CHECKED_EXECUTION_REQUIRED", ex.Code);
        Assert.Equal(dialect, ex.Dialect);
        Assert.Null(ex.InnerException);
        Assert.Equal(string.Empty, cmd.CommandText);   // nothing reached the command
        Assert.Empty(cmd.Parameters);
    }

    [Fact]
    public void Bind_OfAStatementWithoutARowCountCheck_IsUnchanged()
    {
        using var cmd = new SqliteCommand();
        _binder.Bind(cmd, Compiled("DELETE FROM t"), new Dictionary<string, object?>());
        Assert.Equal("DELETE FROM t", cmd.CommandText);
    }

    private static async Task<SqliteConnection> OpenTableAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE t (a INTEGER)";
        await create.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<long> CountAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task CheckedExecutor_CommitsWhenTheAffectedCountMatches()
    {
        await using var connection = await OpenTableAsync();
        var compiled = Checked("INSERT INTO t (a) SELECT 1 WHERE @p0 = 1", 1, Param(0, 1, SqlParameterType.Int32, ParameterOrigin.QueryLiteral));
        int affected = await new CheckedDmlExecutor(_binder).ExecuteAsync(connection, compiled, new Dictionary<string, object?>());
        Assert.Equal(1, affected);
        Assert.Equal(1, await CountAsync(connection));
    }

    [Fact]
    public async Task CheckedExecutor_RollsBackAndThrowsTheTypedError_WhenTheCountDiffers()
    {
        await using var connection = await OpenTableAsync();
        // two rows are written but only one is expected: the transaction is rolled back, nothing stays
        var compiled = Checked("INSERT INTO t (a) SELECT 1 UNION ALL SELECT 2", 1);
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() => new CheckedDmlExecutor(_binder).ExecuteAsync(connection, compiled, new Dictionary<string, object?>()));
        Assert.Equal(0, await CountAsync(connection));

        // the policy filtered a row out: zero rows written, one expected
        var filtered = Checked("INSERT INTO t (a) SELECT 1 WHERE @p0 = 2", 1, Param(0, 1, SqlParameterType.Int32, ParameterOrigin.QueryLiteral));
        await Assert.ThrowsAsync<DmlCheckOptionViolationException>(() => new CheckedDmlExecutor(_binder).ExecuteAsync(connection, filtered, new Dictionary<string, object?>()));
        Assert.Equal(0, await CountAsync(connection));
    }

    [Fact]
    public async Task CheckedExecutor_RollsBackOnAnExecutionError_AndRunsStatementsWithoutACheckToo()
    {
        await using var connection = await OpenTableAsync();
        var broken = Checked("INSERT INTO nope (a) SELECT 1", 1);
        await Assert.ThrowsAnyAsync<Exception>(() => new CheckedDmlExecutor(_binder).ExecuteAsync(connection, broken, new Dictionary<string, object?>()));
        Assert.Equal(0, await CountAsync(connection));

        int affected = await new CheckedDmlExecutor(_binder).ExecuteAsync(connection, Compiled("INSERT INTO t (a) SELECT 5"), new Dictionary<string, object?>());
        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task CheckedExecutor_RefusesACompiledStatementOfAnotherDialect()
    {
        await using var connection = await OpenTableAsync();
        var compiled = Checked("INSERT INTO t (a) SELECT 1", 1) with { Dialect = TargetSqlDialect.PostgreSql };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CheckedDmlExecutor(_binder).ExecuteAsync(connection, compiled, new Dictionary<string, object?>()));
        Assert.Equal(0, await CountAsync(connection));
    }
}
