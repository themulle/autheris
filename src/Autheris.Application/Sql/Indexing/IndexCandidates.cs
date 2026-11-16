using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

namespace Autheris.Application.Sql.Indexing;

public readonly record struct CandidateIndex(
    IReadOnlyList<string> Columns,
    IReadOnlyList<KeyColumnDirection>? Directions);

public static class IndexCandidates
{
    public static List<CandidateIndex> GetCandidateIndexes(TableMetadata metadata)
    {
        var candidates = new List<CandidateIndex>(2 + metadata.Indexes.Count);

        if (metadata.PrimaryKeyColumns.Count > 0)
        {
            candidates.Add(new CandidateIndex(
                metadata.PrimaryKeyColumns,
                KeyDirections.Canonical(metadata.PrimaryKeyDirections) ?? Array.Empty<KeyColumnDirection>()));
        }

        foreach (var idx in metadata.Indexes)
        {
            if (idx.Columns.Count > 0)
            {
                candidates.Add(new CandidateIndex(
                    idx.Columns,
                    KeyDirections.Canonical(idx.Directions) ?? Array.Empty<KeyColumnDirection>()));
            }
        }

        return candidates;
    }
}
