namespace Autheris.Application.Sql.Synthetic;

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Lightweight synthetic reader for developer / unit test environments where no physical DB is attached.
/// </summary>
internal sealed class SyntheticDataTableReader : DbDataReader
{
    private readonly string[] _columns = ["status", "query", "governed"];
    private readonly object?[] _values;
    private bool _readDone;

    public SyntheticDataTableReader(string rewrittenSql)
    {
        _values = ["ok", rewrittenSql, true];
    }

    public override int FieldCount => _columns.Length;
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int RecordsAffected => 0;
    public override int Depth => 0;

    public override bool Read()
    {
        if (!_readDone)
        {
            _readDone = true;
            return true;
        }
        return false;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
    public override string GetName(int ordinal) => _columns[ordinal];
    public override int GetOrdinal(string name) => Array.FindIndex(_columns, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
    public override object GetValue(int ordinal) => _values[ordinal] ?? DBNull.Value;
    public override int GetValues(object[] values)
    {
        int count = Math.Min(values.Length, _values.Length);
        for (int i = 0; i < count; i++) values[i] = _values[i] ?? DBNull.Value;
        return count;
    }
    public override bool IsDBNull(int ordinal) => _values[ordinal] == null;
    public override Type GetFieldType(int ordinal) => typeof(string);
    public override string GetDataTypeName(int ordinal) => "varchar";
    public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(_values[ordinal]);
    public override byte GetByte(int ordinal) => Convert.ToByte(_values[ordinal]);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => Convert.ToChar(_values[ordinal]!);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(_values[ordinal]);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(_values[ordinal]);
    public override double GetDouble(int ordinal) => Convert.ToDouble(_values[ordinal]);
    public override float GetFloat(int ordinal) => Convert.ToSingle(_values[ordinal]);
    public override Guid GetGuid(int ordinal) => Guid.Parse(_values[ordinal]!.ToString()!);
    public override short GetInt16(int ordinal) => Convert.ToInt16(_values[ordinal]);
    public override int GetInt32(int ordinal) => Convert.ToInt32(_values[ordinal]);
    public override long GetInt64(int ordinal) => Convert.ToInt64(_values[ordinal]);
    public override string GetString(int ordinal) => _values[ordinal]?.ToString() ?? string.Empty;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
}
