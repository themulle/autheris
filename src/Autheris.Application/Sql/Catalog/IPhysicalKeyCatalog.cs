using Autheris.Domain.Model;
using TrinoSqlEngine;

namespace Autheris.Application.Sql.Catalog;

/// <summary>State of a physical key read (I403 D-3). The default value is <see cref="Unsupported"/>: no direction is ever assumed.</summary>
public enum PhysicalKeySnapshotState
{
    /// <summary>No reader exists for the dialect or the table is not a SQL table; the direction is unknown (ascending).</summary>
    Unsupported = 0,

    /// <summary>The physical keys were read completely.</summary>
    Known = 1,

    /// <summary>The read failed or is untrustworthy (no connection, dialect mismatch, breaker open, exception, caps, empty result). Fail closed: nothing changes.</summary>
    Unavailable = 2
}

/// <summary>One physical primary key or unique index with its ordered columns and their directions (same length as the columns).</summary>
public sealed record PhysicalKey(bool IsPrimaryKey, bool IsClustered, IReadOnlyList<string> Columns, IReadOnlyList<KeyColumnDirection> Directions);

/// <summary>The physical keys of one table. Only <see cref="PhysicalKeySnapshotState.Known"/> carries keys.</summary>
public readonly record struct PhysicalKeySnapshot(PhysicalKeySnapshotState State, IReadOnlyList<PhysicalKey>? Keys = null, TargetSqlDialect Dialect = default)
{
    public static PhysicalKeySnapshot Unsupported => new(PhysicalKeySnapshotState.Unsupported);

    public static PhysicalKeySnapshot Unavailable => new(PhysicalKeySnapshotState.Unavailable);
}

/// <summary>
/// Reads the physical primary key and unique index directions of a table that the catalog sync is about to persist. Uses only the
/// governed catalog path (I403-SC-03): no second connection path, credential or option.
/// </summary>
public interface IPhysicalKeyCatalog
{
    ValueTask<PhysicalKeySnapshot> ReadAsync(TableMetadata metadata, CancellationToken ct);
}
