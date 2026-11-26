namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Wraps the outermost reader of an execution and records every row it delivers. Recording stops as soon as the row, byte or
/// reservation budget is exceeded (the rows keep streaming to the client). A result is only available when the end of the data was
/// observed and nothing overflowed.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010", Justification = "A DbDataReader enumerates records as non-generic IEnumerable by contract.")]
internal sealed class ResultCacheRecordingReader : ResultCacheRowReader, IDbColumnSchemaGenerator, IRowLimitProbe
{
    private readonly DbDataReader _inner;
    private readonly ResultCacheRowBudget _budget;
    private readonly List<object?[]> _rows = [];
    private readonly string[] _names;
    private readonly Type[] _types;
    private readonly string[] _typeNames;
    private readonly object[] _buffer;
    private object?[]? _current;
    private string? _overflowReason;

    public ResultCacheRecordingReader(DbDataReader inner, int maxRows, long maxBytes, IResultCacheReservation? reservation = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _budget = new ResultCacheRowBudget(maxRows, maxBytes, reservation);
        var count = inner.FieldCount;
        _buffer = new object[count];
        _names = new string[count];
        _types = new Type[count];
        _typeNames = new string[count];
        for (var i = 0; i < count; i++)
        {
            _names[i] = inner.GetName(i);
            _types[i] = inner.GetFieldType(i);
            _typeNames[i] = inner.GetDataTypeName(i);
        }
    }

    /// <summary>True once the inner reader reported that no more rows exist.</summary>
    public bool EndOfDataObserved { get; private set; }

    /// <summary>True when recording stopped because a cap was exceeded or a value cannot be replayed.</summary>
    public bool Overflowed => _overflowReason is not null;

    /// <summary>The reason code of the overflow; null when recording is intact.</summary>
    public string? OverflowReason => _overflowReason;

    public int RecordedRows => _rows.Count;

    public bool HasMoreRows => (_inner as IRowLimitProbe)?.HasMoreRows ?? false;

    protected override object?[]? CurrentRow => _current;

    public override int FieldCount => _inner.FieldCount;

    public override bool HasRows => _inner.HasRows;

    public override bool IsClosed => _inner.IsClosed;

    public override bool Read() => Advance(_inner.Read());

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken) =>
        Advance(await _inner.ReadAsync(cancellationToken).ConfigureAwait(false));

    public override string GetName(int ordinal) => _inner.GetName(ordinal);

    public override int GetOrdinal(string name) => _inner.GetOrdinal(name);

    public override Type GetFieldType(int ordinal) => _inner.GetFieldType(ordinal);

    public override string GetDataTypeName(int ordinal) => _inner.GetDataTypeName(ordinal);

    public ReadOnlyCollection<DbColumn> GetColumnSchema() =>
        _inner.CanGetColumnSchema() ? _inner.GetColumnSchema() : throw new NotSupportedException();

    /// <summary>Builds the result to store. False (with a reason) unless the data was complete and fully recorded.</summary>
    public bool TryBuildResult(GovernedResultInfo info, out CachedWebSqlResult? result, out string? reason)
    {
        result = null;
        if (_overflowReason is not null)
        {
            reason = _overflowReason;
            return false;
        }

        if (!EndOfDataObserved)
        {
            reason = WebSqlResultCacheReasons.Incomplete;
            return false;
        }

        var size = _budget.Bytes + (64L * _names.Length);
        result = new CachedWebSqlResult(info, _names, _types, _typeNames, [.. _rows], HasMoreRows, size);
        reason = null;
        return true;
    }

    private bool Advance(bool hasRow)
    {
        if (!hasRow)
        {
            _current = null;
            EndOfDataObserved = true;
            return false;
        }

        _inner.GetValues(_buffer);
        _current = _buffer;
        if (_overflowReason is null)
        {
            _overflowReason = _budget.TryAdd(_buffer);
            if (_overflowReason is null)
            {
                _rows.Add((object?[])_buffer.Clone());
            }
            else
            {
                _rows.Clear();
                _rows.TrimExcess();
            }
        }

        return true;
    }
}
