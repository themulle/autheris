namespace Autheris.Application.Sql.Paging;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.ResultCache;

/// <summary>
/// Wraps the outermost reader handed to the row writer and remembers the key values of the last row that was delivered. The key values
/// are copied at the compiler-resolved output ordinals; the probe row beyond the page never reaches this reader, so it is never captured.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010", Justification = "A DbDataReader enumerates records as non-generic IEnumerable by contract.")]
internal sealed class KeysetCaptureReader : ResultCacheRowReader, IDbColumnSchemaGenerator, IRowLimitProbe
{
    private readonly DbDataReader _inner;
    private readonly ImmutableArray<int> _keyOrdinals;
    private readonly object[] _buffer;
    private readonly object?[] _keys;
    private readonly List<object?[]>? _perRowKeys;
    private object?[]? _current;

    /// <param name="inner">The reader whose rows are delivered.</param>
    /// <param name="key">The compiler-resolved keyset shape.</param>
    /// <param name="retainEveryRow">True keeps the key values of every delivered row, so a transport that shortens the page afterwards can resume after the last row it kept.</param>
    public KeysetCaptureReader(DbDataReader inner, TrinoSqlEngine.KeysetKeyInfo key, bool retainEveryRow = false, int initialCapacity = 0)
    {
        _perRowKeys = retainEveryRow ? new List<object?[]>(Math.Clamp(initialCapacity, 0, 4096)) : null;
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        _keyOrdinals = key.OutputOrdinals;
        _buffer = new object[inner.FieldCount];
        _keys = new object?[_keyOrdinals.Length];
    }

    /// <summary>The compiler-resolved keyset shape of the page statement the captured values belong to.</summary>
    public TrinoSqlEngine.KeysetKeyInfo Key { get; }

    /// <summary>Rows delivered through this reader.</summary>
    public int RowsRead { get; private set; }

    /// <summary>
    /// The key values of the last delivered row, in key order; null until a row was read. The array is reused for every row, so it is only
    /// meaningful once the rows were delivered.
    /// </summary>
    public object?[]? LastKeyValues => RowsRead > 0 ? _keys : null;

    /// <summary>
    /// The key values of the <paramref name="rowsDelivered"/>-th delivered row (1-based, so the last row a shortened page kept); null when
    /// every-row retention was not requested or fewer rows were delivered.
    /// </summary>
    public object?[]? KeyValuesOfRow(int rowsDelivered) =>
        _perRowKeys is not null && rowsDelivered >= 1 && rowsDelivered <= _perRowKeys.Count ? _perRowKeys[rowsDelivered - 1] : null;

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

    private bool Advance(bool hasRow)
    {
        if (!hasRow)
        {
            _current = null;
            return false;
        }

        _inner.GetValues(_buffer);
        _current = _buffer;
        RowsRead++;
        for (var i = 0; i < _keys.Length; i++)
        {
            _keys[i] = _buffer[_keyOrdinals[i]];
        }

        _perRowKeys?.Add((object?[])_keys.Clone());

        return true;
    }
}
