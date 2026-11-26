namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Replays rows <c>[start, start + count)</c> of a cached result. <see cref="HasMoreRows"/> is true when rows exist after the
/// window or the cached result itself was cut by the row limit.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010", Justification = "A DbDataReader enumerates records as non-generic IEnumerable by contract.")]
internal sealed class CachedResultDataReader : ResultCacheRowReader, IRowLimitProbe
{
    private readonly CachedWebSqlResult _result;
    private readonly int _start;
    private readonly int _end;
    private int _next;
    private object?[]? _current;
    private bool _closed;

    public CachedResultDataReader(CachedWebSqlResult result, int start = 0, int? count = null)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        _start = Math.Clamp(start, 0, result.Rows.Length);
        var available = result.Rows.Length - _start;
        _end = _start + (count is { } c ? Math.Clamp(c, 0, available) : available);
        _next = _start;
    }

    public bool HasMoreRows => _end < _result.Rows.Length || _result.Truncated;

    /// <summary>Rows in the window.</summary>
    public int WindowRows => _end - _start;

    protected override object?[]? CurrentRow => _current;

    public override int FieldCount => _result.Names.Length;

    public override bool HasRows => _end > _start;

    public override bool IsClosed => _closed;

    public override bool Read()
    {
        if (_closed || _next >= _end)
        {
            _current = null;
            return false;
        }

        _current = _result.Rows[_next++];
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());

    public override string GetName(int ordinal) => _result.Names[ordinal];

    public override Type GetFieldType(int ordinal) => _result.FieldTypes[ordinal];

    public override string GetDataTypeName(int ordinal) => _result.DataTypeNames[ordinal];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201", Justification = "The DbDataReader contract for an unknown column name is IndexOutOfRangeException.")]
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _result.Names.Length; i++)
        {
            if (string.Equals(_result.Names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException(name);
    }

    public override void Close() => _closed = true;
}
