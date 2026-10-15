using System.Data.Common;

namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>
/// Reads the shape of the governance schema from the engine's system catalog. Read-only. Every table or index name is bound as a
/// parameter (SEC-94-08c). An object the login cannot see is reported as missing (SEC-94-08e).
/// </summary>
internal interface IGovernanceSchemaCatalogReader
{
    /// <summary>Names of all user tables in the governance schema, without the migration bookkeeping table.</summary>
    Task<IReadOnlyList<string>> ListTablesAsync(DbConnection connection, CancellationToken ct);

    /// <summary>Reads the given tables; tables that do not exist or are not visible are left out of the result.</summary>
    Task<GovernanceSchemaShape> ReadAsync(DbConnection connection, IReadOnlyCollection<string> tableNames, CancellationToken ct);
}

internal static class SchemaCatalogReaderSupport
{
    internal const string MigrationTable = "autheris_schema_migrations";

    internal static DbCommand Command(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        return cmd;
    }

    internal static async Task<List<object?[]>> RowsAsync(DbConnection connection, string sql, int columns, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        var rows = new List<object?[]>();
        await using var cmd = Command(connection, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[columns];
            for (var i = 0; i < columns; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    internal static string Str(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    internal static bool Bool(object? value) => value switch
    {
        bool b => b,
        null => false,
        _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0
    };
}
