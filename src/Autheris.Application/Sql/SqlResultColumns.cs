namespace Autheris.Application.Sql;

using System;
using System.Collections.Generic;
using System.Data.Common;

/// <summary>
/// A result column as reported by the database reader: name plus the type information needed to announce the column
/// in typed protocols (Trino). Precision and scale are only known when the provider exposes a column schema.
/// </summary>
public sealed record SqlResultColumn(
    string Name,
    Type? ClrType,
    int? NumericPrecision = null,
    int? NumericScale = null,
    string? DataTypeName = null);

/// <summary>
/// Result column names and types read from a <see cref="DbDataReader"/>.
/// </summary>
public static class SqlResultColumns
{
    /// <summary>
    /// WebSQL findings 2.3: like Trino, a column without a name (SQL Server <c>COUNT(*)</c> without alias) is named
    /// <c>_col{position}</c>. A name that repeats an earlier one (case-insensitive, the rows are keyed that way) is
    /// renamed the same way, so no value is lost; a suffix keeps the result unique if that name is already taken.
    /// </summary>
    public static string[] MakeUnique(IReadOnlyList<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var result = new string[names.Count];
        var seen = new HashSet<string>(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
        {
            string? name = names[i];
            if (string.IsNullOrWhiteSpace(name) || seen.Contains(name))
            {
                string baseName = "_col" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                name = baseName;
                for (int suffix = 1; seen.Contains(name); suffix++)
                {
                    name = baseName + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            seen.Add(name);
            result[i] = name;
        }

        return result;
    }

    /// <summary>Unique column names of <paramref name="reader"/> (see <see cref="MakeUnique"/>).</summary>
    public static string[] UniqueNames(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var names = new string?[reader.FieldCount];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = reader.GetName(i);
        }

        return MakeUnique(names);
    }

    /// <summary>Unique names plus CLR type, database type name, precision and scale of every column of <paramref name="reader"/>.</summary>
    public static IReadOnlyList<SqlResultColumn> Describe(DbDataReader reader)
    {
        string[] names = UniqueNames(reader);
        IReadOnlyList<DbColumn>? schema = TryGetColumnSchema(reader);

        var columns = new SqlResultColumn[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            DbColumn? column = schema != null && i < schema.Count ? schema[i] : null;
            columns[i] = new SqlResultColumn(
                names[i],
                TryGet(() => reader.GetFieldType(i)) ?? column?.DataType,
                column?.NumericPrecision,
                column?.NumericScale,
                TryGet(() => reader.GetDataTypeName(i)) ?? column?.DataTypeName);
        }

        return columns;
    }

    private static IReadOnlyList<DbColumn>? TryGetColumnSchema(DbDataReader reader)
    {
        try
        {
            return reader.CanGetColumnSchema() ? reader.GetColumnSchema() : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static T? TryGet<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
