namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>
/// Issue #390: the key a bulk keyset stream seeks on and its declared directions, resolved once for the issuing endpoint, the planner
/// and the cursor context (primary key, else the first unique index). <see cref="Directions"/> is empty when every column is ascending.
/// </summary>
public sealed record BulkStreamKey(IReadOnlyList<string> Columns, IReadOnlyList<KeyColumnDirection> Directions)
{
    /// <summary>The stream key of the table, or null when it has neither a primary key nor a unique index.</summary>
    public static BulkStreamKey? TryResolve(TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.PrimaryKeyColumns.Count > 0)
        {
            return new BulkStreamKey(metadata.PrimaryKeyColumns, KeyDirections.Canonical(metadata.PrimaryKeyDirections) ?? Array.Empty<KeyColumnDirection>());
        }

        var unique = metadata.Indexes.FirstOrDefault(i => i.IsUnique && i.Columns.Count > 0);
        return unique is null
            ? null
            : new BulkStreamKey(unique.Columns, KeyDirections.Canonical(unique.Directions) ?? Array.Empty<KeyColumnDirection>());
    }

    /// <summary>True when key column <paramref name="index"/> seeks and sorts descending; the one source of both the seek operator and the ORDER BY direction.</summary>
    public bool IsDescending(int index) => KeyDirections.IsDescending(Directions, index);
}
