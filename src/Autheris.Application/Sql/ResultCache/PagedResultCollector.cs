namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Reads the full statement behind one page. While the rows fit the caps it keeps all of them, so the result can be cached and any
/// page sliced from it. Once a cap is exceeded it keeps only the requested window and one probe row after it, and stops reading as
/// soon as the probe row is read, so memory stays bounded by the window.
/// </summary>
public sealed class PagedResultCollector
{
    private readonly int _offset;
    private readonly int _size;
    private readonly ResultCacheRowBudget _budget;
    private readonly List<object?[]> _rows = [];
    private string[] _names = [];
    private Type[] _types = [];
    private string[] _typeNames = [];
    private long _index;
    private bool _truncated;

    public PagedResultCollector(int offset, int size, int maxRows, long maxBytes, IResultCacheReservation? reservation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        _offset = offset;
        _size = size;
        _budget = new ResultCacheRowBudget(maxRows, maxBytes, reservation);
    }

    /// <summary>True when the end of the data was reached while every row was kept: the full result is available.</summary>
    public bool Complete { get; private set; }

    /// <summary>True when a cap stopped the full collection; only the window (and probe row) is kept.</summary>
    public bool Overflowed => OverflowReason is not null;

    /// <summary>The reason code when <see cref="Overflowed"/>: too large, an unsupported value type or an exhausted reservation.</summary>
    public string? OverflowReason { get; private set; }

    /// <summary>Reads <paramref name="reader"/> until the data ends or, after an overflow, until the probe row after the window.</summary>
    public async Task CollectAsync(DbDataReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var count = reader.FieldCount;
        _names = new string[count];
        _types = new Type[count];
        _typeNames = new string[count];
        for (var i = 0; i < count; i++)
        {
            _names[i] = reader.GetName(i);
            _types[i] = reader.GetFieldType(i);
            _typeNames[i] = reader.GetDataTypeName(i);
        }

        var buffer = new object[count];
        var probeIndex = (long)_offset + _size;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            reader.GetValues(buffer);
            var index = _index++;
            if (OverflowReason is null)
            {
                var row = (object?[])buffer.Clone();
                var rejection = _budget.TryAdd(row);
                if (rejection is null)
                {
                    _rows.Add(row);
                    continue;
                }

                OverflowReason = rejection;
                TrimToWindow();
                KeepIfInWindow(index, row);
            }
            else if (index >= _offset && index <= probeIndex)
            {
                _rows.Add((object?[])buffer.Clone());
            }

            if (index >= probeIndex)
            {
                return;
            }
        }

        _truncated = (reader as IRowLimitProbe)?.HasMoreRows ?? false;
        if (OverflowReason is null)
        {
            Complete = true;
        }
    }

    /// <summary>The full result for the cache. False unless <see cref="Complete"/>.</summary>
    public bool TryBuildFullResult(GovernedResultInfo info, out CachedWebSqlResult? result)
    {
        if (!Complete)
        {
            result = null;
            return false;
        }

        result = Build(info, [.. _rows], _budget.Bytes + (64L * _names.Length));
        return true;
    }

    /// <summary>A reader over the requested window of the collected rows.</summary>
    public DbDataReader CreateWindowReader(GovernedResultInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (Complete)
        {
            return new CachedResultDataReader(Build(info, [.. _rows], _budget.Bytes), _offset, _size);
        }

        return new CachedResultDataReader(Build(info, [.. _rows], 0), 0, _size);
    }

    private void TrimToWindow()
    {
        var keepFrom = Math.Min(_offset, _rows.Count);
        _rows.RemoveRange(0, keepFrom);
        var keepCount = Math.Min(_rows.Count, _size + 1);
        if (_rows.Count > keepCount)
        {
            _rows.RemoveRange(keepCount, _rows.Count - keepCount);
        }

        _rows.TrimExcess();
    }

    private void KeepIfInWindow(long index, object?[] row)
    {
        if (index >= _offset && index <= (long)_offset + _size)
        {
            _rows.Add(row);
        }
    }

    private CachedWebSqlResult Build(GovernedResultInfo info, object?[][] rows, long size) =>
        new(info, _names, _types, _typeNames, rows, _truncated, size);
}
