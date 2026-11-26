namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

/// <summary>A reader that serves every accessor from one materialized row.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010", Justification = "A DbDataReader enumerates records as non-generic IEnumerable by contract.")]
internal abstract class ResultCacheRowReader : DbDataReader
{
    /// <summary>The row the reader is positioned on; null before the first and after the last row.</summary>
    protected abstract object?[]? CurrentRow { get; }

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override int RecordsAffected => -1;

    public override int Depth => 0;

    public override bool NextResult() => false;

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public override bool IsDBNull(int ordinal) => Value(ordinal) is null or DBNull;

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => Task.FromResult(IsDBNull(ordinal));

    public override object GetValue(int ordinal) => Value(ordinal) ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var row = CurrentRow ?? throw new InvalidOperationException("No current row.");
        var count = Math.Min(values.Length, row.Length);
        for (var i = 0; i < count; i++)
        {
            values[i] = row[i] ?? DBNull.Value;
        }

        return count;
    }

    public override bool GetBoolean(int ordinal) => Convert(ordinal) is bool b ? b : System.Convert.ToBoolean(Convert(ordinal), CultureInfo.InvariantCulture);

    public override byte GetByte(int ordinal) => System.Convert.ToByte(Convert(ordinal), CultureInfo.InvariantCulture);

    public override char GetChar(int ordinal) => Convert(ordinal) is char c ? c : System.Convert.ToChar(Convert(ordinal), CultureInfo.InvariantCulture);

    public override short GetInt16(int ordinal) => System.Convert.ToInt16(Convert(ordinal), CultureInfo.InvariantCulture);

    public override int GetInt32(int ordinal) => System.Convert.ToInt32(Convert(ordinal), CultureInfo.InvariantCulture);

    public override long GetInt64(int ordinal) => System.Convert.ToInt64(Convert(ordinal), CultureInfo.InvariantCulture);

    public override float GetFloat(int ordinal) => System.Convert.ToSingle(Convert(ordinal), CultureInfo.InvariantCulture);

    public override double GetDouble(int ordinal) => System.Convert.ToDouble(Convert(ordinal), CultureInfo.InvariantCulture);

    public override decimal GetDecimal(int ordinal) => System.Convert.ToDecimal(Convert(ordinal), CultureInfo.InvariantCulture);

    public override DateTime GetDateTime(int ordinal) =>
        Convert(ordinal) switch
        {
            DateTime d => d,
            DateTimeOffset o => o.UtcDateTime,
            var other => System.Convert.ToDateTime(other, CultureInfo.InvariantCulture),
        };

    public override Guid GetGuid(int ordinal) => Convert(ordinal) is Guid g ? g : Guid.Parse(GetString(ordinal));

    public override string GetString(int ordinal) =>
        Convert(ordinal) is string s ? s : System.Convert.ToString(Convert(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = Convert(ordinal) as byte[] ?? throw new InvalidCastException();
        if (buffer is null)
        {
            return bytes.Length;
        }

        if (dataOffset >= bytes.Length)
        {
            return 0;
        }

        var count = (int)Math.Min(length, bytes.Length - dataOffset);
        Buffer.BlockCopy(bytes, (int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = GetString(ordinal);
        if (buffer is null)
        {
            return text.Length;
        }

        if (dataOffset >= text.Length)
        {
            return 0;
        }

        var count = (int)Math.Min(length, text.Length - dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = Value(ordinal);
        if (value is T typed)
        {
            return typed;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (value is null or DBNull)
        {
            if (!typeof(T).IsValueType || target != typeof(T))
            {
                return default!;
            }

            throw new InvalidCastException();
        }

        return (T)System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => Task.FromResult(GetFieldValue<T>(ordinal));

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    private object? Value(int ordinal)
    {
        var row = CurrentRow ?? throw new InvalidOperationException("No current row.");
        return row[ordinal];
    }

    private object Convert(int ordinal) =>
        Value(ordinal) is { } value and not DBNull ? value : throw new InvalidCastException("The value is null.");
}
