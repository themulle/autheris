namespace Autheris.Application.Sql;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;

/// <summary>
/// Filters metric-view result groups whose server-injected count column is below <c>minGroupSize</c>
/// while reading columns strictly in ordinal order so <see cref="CommandBehavior.SequentialAccess"/> works cleanly
/// and hiding the internal count column from metadata and row accessors.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010", Justification = "A DbDataReader enumerates records as non-generic IEnumerable by contract.")]
public sealed class MetricViewGroupSizeDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private readonly DbDataReader _inner;
    private readonly int _hiddenOrdinal;
    private readonly int _minGroupSize;
    private readonly object?[] _currentRow;
    private bool _hasCurrentRow;

    public MetricViewGroupSizeDataReader(DbDataReader inner, int hiddenOrdinal, int minGroupSize)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (hiddenOrdinal < 0 || hiddenOrdinal >= inner.FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(hiddenOrdinal));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minGroupSize, 1);

        _hiddenOrdinal = hiddenOrdinal;
        _minGroupSize = minGroupSize;
        _currentRow = new object?[inner.FieldCount];
    }

    public override int FieldCount => _inner.FieldCount - 1;
    public override int RecordsAffected => _inner.RecordsAffected;
    public override bool HasRows => _inner.HasRows;
    public override bool IsClosed => _inner.IsClosed;
    public override int Depth => _inner.Depth;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        while (_inner.Read())
        {
            for (int i = 0; i < _inner.FieldCount; i++)
            {
                _currentRow[i] = _inner.IsDBNull(i) ? null : _inner.GetValue(i);
            }

            if (MeetsThreshold())
            {
                _hasCurrentRow = true;
                return true;
            }
        }

        ClearCurrentRow();
        return false;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        while (await _inner.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (int i = 0; i < _inner.FieldCount; i++)
            {
                _currentRow[i] = await _inner.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                    ? null
                    : _inner.GetValue(i);
            }

            if (MeetsThreshold())
            {
                _hasCurrentRow = true;
                return true;
            }
        }

        ClearCurrentRow();
        return false;
    }

    private bool MeetsThreshold() =>
        GroupSizeFilterHelper.TryParseGroupCountValue(_currentRow[_hiddenOrdinal], out var count) &&
        GroupSizeFilterHelper.MeetsMinGroupSize(count, _minGroupSize);

    private void ClearCurrentRow()
    {
        _hasCurrentRow = false;
        Array.Clear(_currentRow);
    }

    private int MapOrdinal(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, FieldCount);
        return ordinal < _hiddenOrdinal ? ordinal : ordinal + 1;
    }

    private object GetRequiredCell(int ordinal)
    {
        if (!_hasCurrentRow)
        {
            throw new InvalidOperationException("No current row is available.");
        }

        return _currentRow[MapOrdinal(ordinal)] ?? throw new InvalidCastException("Column value is DBNull.");
    }

    public ReadOnlyCollection<DbColumn> GetColumnSchema()
    {
        if (!_inner.CanGetColumnSchema())
        {
            throw new NotSupportedException();
        }

        var innerSchema = _inner.GetColumnSchema();
        var visible = new List<DbColumn>(Math.Max(0, innerSchema.Count - 1));
        for (int i = 0; i < innerSchema.Count; i++)
        {
            if (i != _hiddenOrdinal)
            {
                visible.Add(innerSchema[i]);
            }
        }

        return visible.AsReadOnly();
    }

    public override DataTable? GetSchemaTable()
    {
        var table = _inner.GetSchemaTable();
        if (table is null)
        {
            return null;
        }

        if (_hiddenOrdinal < table.Rows.Count)
        {
            table.Rows.RemoveAt(_hiddenOrdinal);
            if (table.Columns.Contains("ColumnOrdinal"))
            {
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    table.Rows[i]["ColumnOrdinal"] = i;
                }
            }
        }

        return table;
    }

    public override bool NextResult() => false;
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public override string GetName(int ordinal) => _inner.GetName(MapOrdinal(ordinal));
    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(MapOrdinal(ordinal));
    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(MapOrdinal(ordinal));

    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (int i = 0; i < FieldCount; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (int i = 0; i < FieldCount; i++)
        {
            if (string.Equals(GetName(i), name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), $"Column '{name}' was not found.");
    }

    public override bool IsDBNull(int ordinal)
    {
        if (!_hasCurrentRow)
        {
            throw new InvalidOperationException("No current row is available.");
        }

        var val = _currentRow[MapOrdinal(ordinal)];
        return val is null || val == DBNull.Value;
    }

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(IsDBNull(ordinal));

    public override object GetValue(int ordinal)
    {
        if (!_hasCurrentRow)
        {
            throw new InvalidOperationException("No current row is available.");
        }

        return _currentRow[MapOrdinal(ordinal)] ?? DBNull.Value;
    }

    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var count = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override byte GetByte(int ordinal) => Convert.ToByte(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override char GetChar(int ordinal) => Convert.ToChar(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetRequiredCell(ordinal), CultureInfo.InvariantCulture);
    public override string GetString(int ordinal) => Convert.ToString(GetRequiredCell(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;

    public override Guid GetGuid(int ordinal)
    {
        var value = GetRequiredCell(ordinal);
        return value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        if (GetRequiredCell(ordinal) is not byte[] bytes)
        {
            throw new InvalidCastException("Column value is not a byte array.");
        }

        if (buffer is null)
        {
            return bytes.Length;
        }

        if (dataOffset < 0 || dataOffset >= bytes.Length)
        {
            return 0;
        }

        int count = (int)Math.Min(length, bytes.Length - dataOffset);
        Array.Copy(bytes, (int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var str = GetString(ordinal);
        if (buffer is null)
        {
            return str.Length;
        }

        if (dataOffset < 0 || dataOffset >= str.Length)
        {
            return 0;
        }

        int count = (int)Math.Min(length, str.Length - dataOffset);
        str.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetRequiredCell(ordinal);
        if (value is T typed)
        {
            return typed;
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(GetFieldValue<T>(ordinal));

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);
}
