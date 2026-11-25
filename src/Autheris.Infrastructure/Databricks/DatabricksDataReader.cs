namespace Autheris.Infrastructure.Databricks;

using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Wraps the typed <see cref="DataTableReader"/> returned by <see cref="DatabricksDbCommand"/> so
/// <see cref="GetDataTypeName"/> and <see cref="GetColumnSchema"/> expose Databricks type metadata
/// (<c>type_name</c>, <c>type_precision</c>, <c>type_scale</c>).
/// </summary>
[SuppressMessage("Design", "CA1010", Justification = "A DbDataReader enumerates records as non-generic IEnumerable by contract.")]
internal sealed class DatabricksDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private readonly DataTable _table;
    private readonly DataTableReader _inner;
    private readonly ReadOnlyCollection<DbColumn> _schema;

    public DatabricksDataReader(DataTable table, IReadOnlyList<(string Name, DatabricksColumn Column, Type ClrType)> columns)
    {
        _table = table;
        _inner = table.CreateDataReader();
        var list = new DbColumn[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var (name, col, clrType) = columns[i];
            list[i] = new DatabricksDbColumn(name, i, clrType, col.TypeName, col.Precision, col.Scale);
        }

        _schema = new ReadOnlyCollection<DbColumn>(list);
    }

    public ReadOnlyCollection<DbColumn> GetColumnSchema() => _schema;

    public override int FieldCount => _inner.FieldCount;
    public override int Depth => _inner.Depth;
    public override bool HasRows => _inner.HasRows;
    public override bool IsClosed => _inner.IsClosed;
    public override int RecordsAffected => _inner.RecordsAffected;
    public override object this[int ordinal] => _inner[ordinal];
    public override object this[string name] => _inner[name];
    public override bool GetBoolean(int ordinal) => _inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => _inner.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => _inner.GetChar(ordinal);
    /// <summary>
    /// Streams a string cell. <see cref="DataTableReader.GetChars"/> only supports <c>char[]</c> cells and throws
    /// <see cref="InvalidCastException"/> for the string cells this reader stores (#1140), so the copy is done here following
    /// the ADO.NET contract: a null buffer returns the value length, otherwise up to <paramref name="length"/> characters from
    /// <paramref name="dataOffset"/> are copied and their count is returned (0 at the end of the value).
    /// </summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        if (_inner.GetValue(ordinal) is not string text)
        {
            return _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        }

        if (buffer is null)
        {
            return text.Length;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(dataOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(bufferOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (bufferOffset + length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The buffer is too small for the requested length.");
        }

        if (dataOffset >= text.Length)
        {
            return 0;
        }

        var count = (int)Math.Min(length, text.Length - dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }
    public override string GetDataTypeName(int ordinal) => _schema[ordinal].DataTypeName ?? _inner.GetDataTypeName(ordinal);
    public override DateTime GetDateTime(int ordinal) => _inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => _inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => _inner.GetDouble(ordinal);
    public override IEnumerator GetEnumerator() => _inner.GetEnumerator();
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
    public override bool NextResult() => _inner.NextResult();
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _inner.NextResultAsync(cancellationToken);
    public override bool Read() => _inner.Read();
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => _inner.ReadAsync(cancellationToken);
    public override T GetFieldValue<T>(int ordinal) => _inner.GetFieldValue<T>(ordinal);
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => _inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
    public override void Close() => _inner.Close();
    public override DataTable? GetSchemaTable() => _inner.GetSchemaTable();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _table.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        _table.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class DatabricksDbColumn : DbColumn
    {
        public DatabricksDbColumn(string name, int ordinal, Type dataType, string dataTypeName, int? precision, int? scale)
        {
            ColumnName = name;
            ColumnOrdinal = ordinal;
            DataType = dataType;
            DataTypeName = dataTypeName;
            NumericPrecision = precision;
            NumericScale = scale;
            AllowDBNull = true;
        }
    }
}
