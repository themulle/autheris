namespace Autheris.Application.DataCatalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Model;
using TrinoSqlEngine;

/// <summary>
/// I403 D-4 and D-5: decides the key and index column directions of a table the catalog sync is about to persist. Pure: no I/O, no
/// clock. Precedence per key column list (I403-SC-06): asset-declared, then stored non-empty (operator-owned), then physical,
/// then unknown (ascending). Physical directions are only ever used for a column list that equals the physical one exactly
/// (I403-SC-04); they never add, remove or reorder a column and never create a key definition.
/// </summary>
public static class PhysicalKeyDirectionReconciler
{
    public const string DriftCode = "KEY_DIRECTION_DRIFT";
    public const string ConflictCode = "KEY_DIRECTION_CONFLICT";

    /// <summary>
    /// The outcome. <see cref="Metadata"/> is the metadata to persist; <see cref="WithoutPhysical"/> is the same with the physical
    /// directions left out (post <c>Apply</c>, with the stored directions retained, I403-SC-05), used when the audit fails.
    /// </summary>
    public sealed record Result(TableMetadata Metadata, TableMetadata WithoutPhysical, IReadOnlyList<string> Findings, bool PhysicalApplied);

    private enum MatchKind { None, Match, Mismatch, Ambiguous }

    private readonly record struct KeyMatch(MatchKind Kind, IReadOnlyList<KeyColumnDirection>? Directions);

    private readonly record struct Resolved(IReadOnlyList<KeyColumnDirection>? Retained, IReadOnlyList<KeyColumnDirection>? Final, string? Finding, bool Applied);

    public static Result Reconcile(TableMetadata incoming, TableMetadata? existing, PhysicalKeySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var findings = new List<string>();
        var known = snapshot is { State: PhysicalKeySnapshotState.Known, Keys: not null };
        var comparer = snapshot.Dialect == TargetSqlDialect.SqlServer ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // The asset declares no key: the stored definition stays untouched; a differing physical direction is only reported.
        if (!incoming.ReplaceKeyDefinition)
        {
            if (known && existing is not null)
            {
                ReportDrift(existing, snapshot.Keys!, comparer, findings);
            }

            return new Result(incoming, incoming, findings, false);
        }

        var applied = false;
        var retainedPrimaryKey = incoming.PrimaryKeyDirections;
        var finalPrimaryKey = incoming.PrimaryKeyDirections;
        if (incoming.PrimaryKeyColumns.Count > 0)
        {
            var match = known ? MatchPrimaryKey(incoming.PrimaryKeyColumns, snapshot.Keys!, comparer) : default;
            var hasStored = existing is { PrimaryKeyColumns.Count: > 0 };
            var r = Resolve(incoming.PrimaryKeyColumns, incoming.PrimaryKeyDirections,
                hasStored ? existing!.PrimaryKeyColumns : null, hasStored ? existing!.PrimaryKeyDirections : null, match);
            retainedPrimaryKey = r.Retained ?? [];
            finalPrimaryKey = r.Final ?? [];
            applied |= r.Applied;
            if (r.Finding is not null)
            {
                findings.Add(r.Finding);
            }
        }

        var retainedIndexes = new List<TableIndexDefinition>(incoming.Indexes.Count);
        var finalIndexes = new List<TableIndexDefinition>(incoming.Indexes.Count);
        foreach (var index in incoming.Indexes)
        {
            if (!index.IsUnique)
            {
                retainedIndexes.Add(index);
                finalIndexes.Add(index);
                continue;
            }

            var match = known ? MatchUnique(index.Columns, snapshot.Keys!, comparer) : default;
            var storedIndex = existing?.Indexes.FirstOrDefault(i => i.IsUnique && SameColumns(i.Columns, index.Columns, StringComparer.OrdinalIgnoreCase));
            var r = Resolve(index.Columns, index.Directions, storedIndex?.Columns, storedIndex?.Directions, match);
            retainedIndexes.Add(index with { Directions = r.Retained });
            finalIndexes.Add(index with { Directions = r.Final });
            applied |= r.Applied;
            if (r.Finding is not null)
            {
                findings.Add(r.Finding);
            }
        }

        var withoutPhysical = incoming with { PrimaryKeyDirections = retainedPrimaryKey, Indexes = retainedIndexes };
        var metadata = applied ? incoming with { PrimaryKeyDirections = finalPrimaryKey, Indexes = finalIndexes } : withoutPhysical;
        return new Result(metadata, withoutPhysical, findings, applied);
    }

    // D-5 steps 1 to 4 for one column list.
    private static Resolved Resolve(
        IReadOnlyList<string> columns,
        IReadOnlyList<KeyColumnDirection>? incomingDirections,
        IReadOnlyList<string>? storedColumns,
        IReadOnlyList<KeyColumnDirection>? storedDirections,
        KeyMatch match)
    {
        var declared = KeyDirections.Canonical(incomingDirections);
        var storedCanonical = storedColumns is not null && SameColumns(storedColumns, columns, StringComparer.OrdinalIgnoreCase)
            ? KeyDirections.Canonical(storedDirections)
            : null;
        var physical = match.Kind == MatchKind.Match ? match.Directions : null;

        if (declared is not null)
        {
            // 1. asset-declared wins; a differing physical direction is reported, not applied
            return new Resolved(declared, declared, physical is not null && !SameDirections(physical, declared, columns.Count) ? DriftCode : null, false);
        }

        if (storedCanonical is not null)
        {
            // 2. stored non-empty directions of the identical column list are operator-owned (also closes the 390a gap, I403-SC-05)
            return new Resolved(storedCanonical, storedCanonical,
                physical is not null && !SameDirections(physical, storedCanonical, columns.Count) ? ConflictCode : null, false);
        }

        // 3. a Known exact match applies the physical directions to the same column list; 4. otherwise unknown (ascending)
        var canonicalPhysical = KeyDirections.Canonical(physical);
        return canonicalPhysical is not null
            ? new Resolved(null, canonicalPhysical, null, true)
            : new Resolved(null, null, null, false);
    }

    private static void ReportDrift(TableMetadata existing, IReadOnlyList<PhysicalKey> keys, StringComparer comparer, List<string> findings)
    {
        if (existing.PrimaryKeyColumns.Count > 0 &&
            MatchPrimaryKey(existing.PrimaryKeyColumns, keys, comparer) is { Kind: MatchKind.Match } pk &&
            !SameDirections(pk.Directions, existing.PrimaryKeyDirections, existing.PrimaryKeyColumns.Count))
        {
            findings.Add(DriftCode);
        }

        foreach (var index in existing.Indexes.Where(static i => i.IsUnique))
        {
            if (MatchUnique(index.Columns, keys, comparer) is { Kind: MatchKind.Match } m &&
                !SameDirections(m.Directions, index.Directions, index.Columns.Count))
            {
                findings.Add(DriftCode);
            }
        }
    }

    // D-4 for the primary key: the physical primary key; without one the clustered unique index; otherwise a unique index.
    // A physical primary key that differs in columns or order is a Mismatch without fallback.
    private static KeyMatch MatchPrimaryKey(IReadOnlyList<string> columns, IReadOnlyList<PhysicalKey> keys, StringComparer comparer)
    {
        var physicalPrimaryKey = keys.FirstOrDefault(static k => k.IsPrimaryKey);
        if (physicalPrimaryKey is not null)
        {
            return SameColumns(physicalPrimaryKey.Columns, columns, comparer)
                ? new KeyMatch(MatchKind.Match, physicalPrimaryKey.Directions)
                : new KeyMatch(MatchKind.Mismatch, null);
        }

        return MatchAmongUnique(columns, keys, comparer);
    }

    // A unique index of the definition may be backed by the primary key (exact columns), then the clustered unique index, then any unique index.
    private static KeyMatch MatchUnique(IReadOnlyList<string> columns, IReadOnlyList<PhysicalKey> keys, StringComparer comparer)
    {
        var physicalPrimaryKey = keys.FirstOrDefault(static k => k.IsPrimaryKey);
        if (physicalPrimaryKey is not null && SameColumns(physicalPrimaryKey.Columns, columns, comparer))
        {
            return new KeyMatch(MatchKind.Match, physicalPrimaryKey.Directions);
        }

        return MatchAmongUnique(columns, keys, comparer);
    }

    private static KeyMatch MatchAmongUnique(IReadOnlyList<string> columns, IReadOnlyList<PhysicalKey> keys, StringComparer comparer)
    {
        var candidates = keys.Where(k => !k.IsPrimaryKey && SameColumns(k.Columns, columns, comparer)).ToArray();
        if (candidates.Length == 0)
        {
            return new KeyMatch(MatchKind.None, null);
        }

        var clustered = candidates.FirstOrDefault(static k => k.IsClustered);
        if (clustered is not null)
        {
            return new KeyMatch(MatchKind.Match, clustered.Directions);
        }

        var first = candidates[0];
        return candidates.All(c => SameDirections(c.Directions, first.Directions, columns.Count))
            ? new KeyMatch(MatchKind.Match, first.Directions)
            : new KeyMatch(MatchKind.Ambiguous, null);
    }

    private static bool SameColumns(IReadOnlyList<string> left, IReadOnlyList<string> right, StringComparer comparer)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!comparer.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    // Missing entries read as ascending (canonical form).
    private static bool SameDirections(IReadOnlyList<KeyColumnDirection>? left, IReadOnlyList<KeyColumnDirection>? right, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (KeyDirections.IsDescending(left, i) != KeyDirections.IsDescending(right, i))
            {
                return false;
            }
        }

        return true;
    }
}
