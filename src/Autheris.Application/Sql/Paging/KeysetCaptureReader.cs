namespace Autheris.Application.Sql.Paging;

using System;
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
    private object?[]? _current;

    public KeysetCaptureReader(DbDataReader inner, TrinoSqlEngine.KeysetKeyInfo key)
    {
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

        return true;
    }
}
