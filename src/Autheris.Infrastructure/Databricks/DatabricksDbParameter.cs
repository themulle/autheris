namespace Autheris.Infrastructure.Databricks;

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

/// <summary>Plain parameter of the Databricks facade: the binder sets name, <see cref="DbType"/> and value; the command maps them to a typed REST parameter.</summary>
public sealed class DatabricksDbParameter : DbParameter
{
    private string _name = string.Empty;
    private string _sourceColumn = string.Empty;

    public override DbType DbType { get; set; } = DbType.String;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => _name;
        set => _name = value ?? string.Empty;
    }

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? string.Empty;
    }

    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.String;
}

/// <summary>Minimal ordered parameter collection of the Databricks facade.</summary>
internal sealed class DatabricksDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _items = [];

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    public override int Add(object value)
    {
        _items.Add(Cast(value));
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value);
        }
    }

    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => _items.Contains(Cast(value));
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => _items.IndexOf(Cast(value));
    public override int IndexOf(string parameterName) => _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));
    public override void Insert(int index, object value) => _items.Insert(index, Cast(value));
    public override void Remove(object value) => _items.Remove(Cast(value));
    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;

    internal IReadOnlyList<DbParameter> Items => _items;

    private static DbParameter Cast(object value) =>
        value as DbParameter ?? throw new ArgumentException("Only DbParameter instances can be added.", nameof(value));
}
