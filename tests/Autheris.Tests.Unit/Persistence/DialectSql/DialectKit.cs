using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

/// <summary>Per-dialect plumbing of the ARCH-MNT-22 L2 runtime goldens: the provider command, and a reference parameter built with <c>AddWithValue</c>.</summary>
internal sealed class DialectKit(string name, Func<DbCommand> commandFactory, Func<DbCommand, string, object?, DbParameter> addWithValue)
{
    public static readonly DialectKit PostgreSql = new("PostgreSql", () => new NpgsqlCommand(), (c, n, v) => ((NpgsqlCommand)c).Parameters.AddWithValue(n, v!));

    public static readonly DialectKit SqlServer = new("SqlServer", () => new SqlCommand(), (c, n, v) => ((SqlCommand)c).Parameters.AddWithValue(n, v));

    public static readonly DialectKit Sqlite = new("Sqlite", () => new SqliteCommand(), (c, n, v) => ((SqliteCommand)c).Parameters.AddWithValue(n, v));

    public string Name => name;

    public RecordingDbConnection NewConnection() => new(commandFactory);

    public static TheoryData<string> Names() => new() { "PostgreSql", "SqlServer", "Sqlite" };

    public static DialectKit Of(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSql,
        "SqlServer" => SqlServer,
        "Sqlite" => Sqlite,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect))
    };

    public DialectSqlGolden Golden(string area) => DialectSqlGolden.Load(name, area);

    /// <summary>Adds a parameter through the provider's <c>AddWithValue</c> (the pre-refactor binding call).</summary>
    public DbParameter Add(DbCommand command, string parameterName, object? value) => addWithValue(command, parameterName, value);

    /// <summary>Asserts that the recorded parameters equal, in order, a reference built through the provider's <c>AddWithValue</c> (name, value, DbType, provider type).</summary>
    public void ShouldBindExactly(RecordedCommand command, params (string Name, object? Value)[] expected) =>
        ShouldBindLike(command, reference =>
        {
            foreach (var (parameterName, value) in expected)
            {
                addWithValue(reference, parameterName, value);
            }
        });

    /// <summary>Like <see cref="ShouldBindExactly"/> but the reference is built by <paramref name="buildReference"/> (e.g. a typed provider parameter).</summary>
    public void ShouldBindLike(RecordedCommand command, Action<DbCommand> buildReference)
    {
        using var reference = commandFactory();
        buildReference(reference);
        var referenceParameters = reference.Parameters.Cast<DbParameter>().Select(RecordingDbConnection.Snapshot).ToList();
        command.Parameters.Select(p => p.Name).ShouldBe(referenceParameters.Select(p => p.Name), "parameter names and binding order");
        command.Parameters.ShouldBe(referenceParameters, "parameter values, DbType and provider type equal AddWithValue");
    }
}
