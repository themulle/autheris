namespace Autheris.Application.Sql;

using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// WebSQL findings 2.4: delivers at most <c>limit</c> rows of a reader whose statement was limited to <c>limit + 1</c>
/// rows. The extra (probe) row is never handed out; it only sets <see cref="HasMoreRows"/>, so "truncated" means that
/// rows were actually cut, not that the limit was reached. A limit of 0 or less delivers every row.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010", Justification = "A DbDataReader enumerates records as non-generic IEnumerable by contract.")]
public sealed class RowLimitedDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private readonly DbDataReader _inner;
    private readonly long _limit;
    private long _delivered;
    private bool _exhausted;

    public RowLimitedDataReader(DbDataReader inner, long limit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _limit = limit;
    }

    /// <summary>True when the result had more rows than the limit (the probe row existed).</summary>
    public bool HasMoreRows { get; private set; }

    public override bool Read()
    {
        if (!CanRead())
        {
            return false;
        }

        return Advance(_inner.Read());
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (!CanRead())
        {
            return false;
        }

        return Advance(await _inner.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    private bool CanRead() => !_exhausted;

    private bool Advance(bool hasRow)
    {
        if (!hasRow)
        {
            _exhausted = true;
            return false;
        }

        if (_limit > 0 && _delivered >= _limit)
        {
            HasMoreRows = true;
            _exhausted = true;
            return false;
        }

        _delivered++;
        return true;
    }

    public ReadOnlyCollection<DbColumn> GetColumnSchema() =>
        _inner.CanGetColumnSchema() ? _inner.GetColumnSchema() : throw new NotSupportedException();

    public override int FieldCount => _inner.FieldCount;
    public override object this[int ordinal] => _inner[ordinal];
    public override object this[string name] => _inner[name];
    public override int RecordsAffected => _inner.RecordsAffected;
    public override bool HasRows => _inner.HasRows;
    public override bool IsClosed => _inner.IsClosed;
    public override int Depth => _inner.Depth;
    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => _inner.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);
    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);
    public override float GetFloat(int ordinal) => _inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => _inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => _inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => _inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => _inner.GetInt64(ordinal);
    public override string GetName(int ordinal) => _inner.GetName(ordinal);
    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);
    public override string GetString(int ordinal) => _inner.GetString(ordinal);
    public override object GetValue(int ordinal) => _inner.GetValue(ordinal);
    public override int GetValues(object[] values) => _inner.GetValues(values);
    public override bool IsDBNull(int ordinal) => _inner.IsDBNull(ordinal);
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => _inner.IsDBNullAsync(ordinal, cancellationToken);
    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => _inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    // The inner reader belongs to the command that created it and is disposed there.
}
