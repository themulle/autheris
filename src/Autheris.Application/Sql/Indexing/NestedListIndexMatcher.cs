using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

namespace Autheris.Application.Sql.Indexing;

public static class NestedListIndexMatcher
{
    public static bool Matches(
        TableMetadata metadata,
        IReadOnlyList<string> joinColumns,
        IReadOnlyCollection<string> equalityColumns,
        IReadOnlyList<(string Column, bool Descending)> order)
    {
        if (metadata is null) return false;
        var candidates = IndexCandidates.GetCandidateIndexes(metadata);
        if (candidates.Count == 0) return false;

        foreach (var candidate in candidates)
        {
            if (CandidateMatches(candidate, joinColumns, equalityColumns, order))
            {
                return true;
            }
        }

        return false;
    }

    public static bool CandidateMatches(
        CandidateIndex candidate,
        IReadOnlyList<string> joinColumns,
        IReadOnlyCollection<string> equalityColumns,
        IReadOnlyList<(string Column, bool Descending)> order)
    {
        var e = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (joinColumns != null)
        {
            for (int i = 0; i < joinColumns.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(joinColumns[i]))
                {
                    e.Add(joinColumns[i]);
                }
            }
        }
        if (equalityColumns != null)
        {
            foreach (var col in equalityColumns)
            {
                if (!string.IsNullOrWhiteSpace(col))
                {
                    e.Add(col);
                }
            }
        }

        // (a) Maximal leading run of columns in candidate that are in E
        int runEnd = 0;
        while (runEnd < candidate.Columns.Count && e.Contains(candidate.Columns[runEnd]))
        {
            runEnd++;
        }

        // Maximal leading run must contain every join column
        if (joinColumns != null && joinColumns.Count > 0)
        {
            for (int i = 0; i < joinColumns.Count; i++)
            {
                string jc = joinColumns[i];
                bool found = false;
                for (int r = 0; r < runEnd; r++)
                {
                    if (string.Equals(candidate.Columns[r], jc, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
            }
        }

        // (b) Order entries, after dropping columns in E
        var remainingOrder = new List<(string Column, bool Descending)>();
        if (order != null)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (!e.Contains(order[i].Column))
                {
                    remainingOrder.Add(order[i]);
                }
            }
        }

        // Empty remaining order matches after (a)
        if (remainingOrder.Count == 0)
        {
            return true;
        }

        // Must equal next candidate columns in sequence
        if (candidate.Columns.Count < runEnd + remainingOrder.Count)
        {
            return false;
        }

        bool allEqual = true;
        bool allInverted = true;

        for (int j = 0; j < remainingOrder.Count; j++)
        {
            int candidateIdx = runEnd + j;
            string candidateCol = candidate.Columns[candidateIdx];
            if (!string.Equals(candidateCol, remainingOrder[j].Column, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            KeyColumnDirection candDir = (candidate.Directions != null && candidateIdx < candidate.Directions.Count)
                ? candidate.Directions[candidateIdx]
                : KeyColumnDirection.Ascending;

            bool candDesc = (candDir == KeyColumnDirection.Descending);
            bool orderDesc = remainingOrder[j].Descending;

            if (candDesc != orderDesc)
            {
                allEqual = false;
            }
            if (candDesc == orderDesc)
            {
                allInverted = false;
            }
        }

        return allEqual || allInverted;
    }
}
