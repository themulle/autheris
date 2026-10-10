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
}
