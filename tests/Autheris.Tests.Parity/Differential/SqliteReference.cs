using Microsoft.Data.Sqlite;

namespace Autheris.Tests.Parity.Differential;

/// <summary>
/// Reference executor (D-16): runs plain SQLite SQL directly on the seed database file with Microsoft.Data.Sqlite, bypassing the product
/// (no governance, no row filters, no masking, no k-anonymity). The parity host principal has no row filter or masking on
/// <c>parity_sales</c> and the host lifts the k-anonymity minimum group size, so the product answers the same unrestricted question.
/// </summary>
public sealed class SqliteReference(string dataPath)
{
    public ResultSet Execute(string sql, object? parameters = null)
    {
        using var conn = new SqliteConnection($"Data Source={dataPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var p in parameters.GetType().GetProperties())
            {
                cmd.Parameters.AddWithValue("@" + p.Name, p.GetValue(parameters) ?? DBNull.Value);
            }
        }

        using var reader = cmd.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[columns.Length];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return new ResultSet(columns, rows);
    }
}
