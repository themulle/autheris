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

/// <summary>Issue #404: one physical non-unique index (ordered columns, directions of the same length). It carries no name (I404-SC-01).</summary>
public sealed record PhysicalNonUniqueIndex(IReadOnlyList<string> Columns, IReadOnlyList<KeyColumnDirection> Directions);

/// <summary>Issue #404: the physical non-unique indexes of one table. Only <see cref="PhysicalKeySnapshotState.Known"/> carries indexes.</summary>
public readonly record struct PhysicalNonUniqueSnapshot(PhysicalKeySnapshotState State, IReadOnlyList<PhysicalNonUniqueIndex>? Indexes = null);

/// <summary>
/// The physical keys of one table. Only <see cref="PhysicalKeySnapshotState.Known"/> carries keys. <see cref="NonUnique"/> follows the
/// key state unless the keys are known (I404-SC-05): a key read that is not known always means the non-unique read is not known either,
/// and a failed non-unique read never downgrades known keys.
/// </summary>
public readonly record struct PhysicalKeySnapshot(
    PhysicalKeySnapshotState State,
    IReadOnlyList<PhysicalKey>? Keys = null,
    TargetSqlDialect Dialect = default,
    PhysicalNonUniqueSnapshot NonUniqueRead = default)
{
    public static PhysicalKeySnapshot Unsupported => new(PhysicalKeySnapshotState.Unsupported);

    public static PhysicalKeySnapshot Unavailable => new(PhysicalKeySnapshotState.Unavailable);

    /// <summary>The non-unique index read; the key state decides everything but a known key snapshot.</summary>
    public PhysicalNonUniqueSnapshot NonUnique => State == PhysicalKeySnapshotState.Known ? NonUniqueRead : new PhysicalNonUniqueSnapshot(State);
}

/// <summary>
/// Reads the physical primary key and unique index directions of a table that the catalog sync is about to persist. Uses only the
/// governed catalog path (I403-SC-03): no second connection path, credential or option.
/// </summary>
public interface IPhysicalKeyCatalog
{
    ValueTask<PhysicalKeySnapshot> ReadAsync(TableMetadata metadata, CancellationToken ct);
}
