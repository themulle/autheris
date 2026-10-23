namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data.Common;
using System.Globalization;

/// <summary>The three governance database engines (PERS-16): bool and timestamp columns are typed differently on each.</summary>
internal enum SqlDialect
{
    Sqlite,
    PostgreSql,
    SqlServer
}

/// <summary>
/// PERS-16: one place that maps booleans and timestamps to and from the column types of each engine. SQLite stores integers and ISO
/// strings, SQL Server stores BIT and ISO strings (NVARCHAR), PostgreSQL stores BOOLEAN and TIMESTAMPTZ. Binding an ISO string to a
/// TIMESTAMPTZ parameter or reading a BOOLEAN with GetInt32 fails on PostgreSQL, which broke SCIM and recertification there.
/// </summary>
internal static class DialectValueCodec
{
    internal static object Bool(SqlDialect dialect, bool value) =>
        dialect == SqlDialect.Sqlite ? (object)(value ? 1 : 0) : value;

    /// <summary>UTC-normalised timestamp parameter value; null becomes <see cref="DBNull"/>.</summary>
    internal static object Ts(SqlDialect dialect, DateTimeOffset? value)
    {
        if (value is null)
        {
            return DBNull.Value;
        }

        var utc = value.Value.ToUniversalTime();
        return dialect == SqlDialect.PostgreSql ? (object)utc : utc.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static bool ReadBool(SqlDialect dialect, DbDataReader reader, int ordinal) =>
        dialect == SqlDialect.Sqlite ? reader.GetInt64(ordinal) != 0 : reader.GetBoolean(ordinal);

    internal static DateTimeOffset? ReadTs(SqlDialect dialect, DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return dialect == SqlDialect.PostgreSql
            ? reader.GetFieldValue<DateTimeOffset>(ordinal)
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    /// <summary>Reads a NOT NULL timestamp column.</summary>
    internal static DateTimeOffset ReadRequiredTs(SqlDialect dialect, DbDataReader reader, int ordinal) =>
        ReadTs(dialect, reader, ordinal) ?? throw new InvalidOperationException("A required timestamp column was NULL.");
}
