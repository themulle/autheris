using System.Collections.Immutable;
using System.Security;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Governance;

/// <summary>Issue #107 SEC-RF107-20 / D-WPC-8: the engine binder of a key query binds every parameter by ordinal and refuses client origins.</summary>
[Trait("Category", "Security")]
public sealed class BindKeyQueryTests
{
    private static CompiledKeyQuery Query(params BoundParameter[] parameters) => new(
        "SELECT DISTINCT dep.pk FROM dep WHERE tenant = $p1 AND kind = $p2 LIMIT 5",
        [.. parameters],
        "int", "int", "fk", "v1");

    private static BoundParameter Tenant() => new("$p1", "p1", 1, "acme", SqlParameterType.String, ParameterOrigin.Tenant);

    private static BoundParameter Policy() => new("$p2", "p2", 2, 7L, SqlParameterType.Int64, ParameterOrigin.Policy);

    [Fact]
    public void BindsTextAndEveryParameterInOrdinalOrder_SEC_RF107_20()
    {
        using var command = new SqliteCommand();

        CompiledSqlBinders.BindKeyQuery(command, Query(Tenant(), Policy()), TargetSqlDialect.Sqlite);

        Assert.Equal("SELECT DISTINCT dep.pk FROM dep WHERE tenant = $p1 AND kind = $p2 LIMIT 5", command.CommandText);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal("acme", command.Parameters[0].Value);
        Assert.Equal(7L, Convert.ToInt64(command.Parameters[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NoParameters_BindsTheTextOnly()
    {
        using var command = new SqliteCommand();

        CompiledSqlBinders.BindKeyQuery(command, Query(), TargetSqlDialect.Sqlite);

        Assert.Empty(command.Parameters);
    }

    [Fact]
    public void ClientNamedOrigin_IsRefused_NoClientValuesExist_SEC_RF107_20()
    {
        using var command = new SqliteCommand();
        var client = new BoundParameter("$p1", "p1", 1, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "who");

        Assert.Throws<MissingClientParameterException>(() => CompiledSqlBinders.BindKeyQuery(command, Query(client), TargetSqlDialect.Sqlite));
    }

    [Fact]
    public void ClientPositionalOrigin_IsRefused_SEC_RF107_20()
    {
        using var command = new SqliteCommand();
        var client = new BoundParameter("$p1", "p1", 1, "x", SqlParameterType.String, ParameterOrigin.ClientPositional);

        Assert.Throws<SecurityException>(() => CompiledSqlBinders.BindKeyQuery(command, Query(client), TargetSqlDialect.Sqlite));
    }
}
