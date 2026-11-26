namespace Autheris.Application.Sql.ResultCache;

using System;

/// <summary>
/// The final rows of one result, as they were handed to the client: after metric-view filtering, masking and the row limit.
/// <see cref="Truncated"/> is true when the row limit cut the result. The rows are shared and must never be modified.
/// </summary>
public sealed class CachedWebSqlResult
{
    public CachedWebSqlResult(
        GovernedResultInfo info,
        string[] names,
        Type[] fieldTypes,
        string[] dataTypeNames,
        object?[][] rows,
        bool truncated,
        long sizeBytes)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Names = names ?? throw new ArgumentNullException(nameof(names));
        FieldTypes = fieldTypes ?? throw new ArgumentNullException(nameof(fieldTypes));
        DataTypeNames = dataTypeNames ?? throw new ArgumentNullException(nameof(dataTypeNames));
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        Truncated = truncated;
        SizeBytes = sizeBytes;
    }

    public GovernedResultInfo Info { get; }

    public string[] Names { get; }

    public Type[] FieldTypes { get; }

    public string[] DataTypeNames { get; }

    public object?[][] Rows { get; }

    public bool Truncated { get; }

    public long SizeBytes { get; }
}
