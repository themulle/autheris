namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Issue #94 (D-8): the one place where ingestion paths (data catalog sync, OpenMetadata, dbt, OpenAPI) turn a declared
/// primary key / unique constraint of an untrusted source into a table definition. The definition is validated with the
/// <see cref="TableKeyDefinitionValidator"/> and requested for replacement only when the source actually declares keys; an invalid
/// definition is dropped as a whole while the table itself is still ingested (stored definition kept, flag false).
/// </summary>
public static class CatalogKeyDefinitionImport
{
    /// <summary>
    /// Collects the primary key and unique constraints of one untrusted source object. The size caps are applied while adding, before
    /// anything is copied: a violation (empty or oversized column list, second primary key, too many unique constraints) marks the whole
    /// declaration invalid.
    /// </summary>
    public sealed class Collector
    {
        private readonly List<TableIndexDefinition> _indexes = new();
        private readonly HashSet<string> _indexNames = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<string>? _primaryKey;
        private IReadOnlyList<KeyColumnDirection>? _primaryKeyDirections;

        public bool Invalid { get; private set; }

        public string? InvalidRule { get; private set; }

        public void MarkInvalid(string ruleCode)
        {
            Invalid = true;
            InvalidRule ??= ruleCode;
        }

        public IReadOnlyList<string>? PrimaryKey => Invalid ? null : _primaryKey;

        /// <summary>Directions of <see cref="PrimaryKey"/> (canonical: null when all ascending or undeclared).</summary>
        public IReadOnlyList<KeyColumnDirection>? PrimaryKeyDirections => Invalid ? null : _primaryKeyDirections;

        public IReadOnlyList<TableIndexDefinition>? Indexes => Invalid ? null : _indexes;

        public bool HasDeclaration => !Invalid && (_primaryKey is { Count: > 0 } || _indexes.Count > 0);

        public void AddPrimaryKey(IReadOnlyList<string>? columns, IReadOnlyList<KeyColumnDirection>? directions = null)
        {
            if (_primaryKey is not null)
            {
                MarkInvalid("DUPLICATE_PRIMARY_KEY");
                return;
            }

            if (!CapsHold(columns) || !HasDistinctColumns(columns!) || !DirectionsMatch(columns!, directions))
            {
                MarkInvalid("INVALID_PRIMARY_KEY");
                return;
            }

            _primaryKey = columns!.ToArray();
            _primaryKeyDirections = KeyDirections.Canonical(directions?.ToArray());
        }

        public void AddUnique(IReadOnlyList<string>? columns, IReadOnlyList<KeyColumnDirection>? directions = null) =>
            AddIndex(columns, isUnique: true, name: null, directions: directions);

        public void AddIndex(IReadOnlyList<string>? columns, bool isUnique, string? name = null, IReadOnlyList<KeyColumnDirection>? directions = null)
        {
            if (_indexes.Count >= TableKeyDefinitionValidator.MaxIndexesPerTable)
            {
                MarkInvalid("INDEXES_TOO_MANY");
                return;
            }

            if (!CapsHold(columns) || !HasDistinctColumns(columns!) || !DirectionsMatch(columns!, directions))
            {
                MarkInvalid("INVALID_INDEX_COLUMNS");
                return;
            }

            if (name is not null)
            {
                if (!TableKeyDefinitionValidator.IsValidIndexName(name))
                {
                    MarkInvalid("INVALID_INDEX_NAME");
                    return;
                }

                if (_indexNames.Contains(name))
                {
                    MarkInvalid("DUPLICATE_INDEX_NAME");
                    return;
                }
            }

            var canonicalDirections = KeyDirections.Canonical(directions?.ToArray());
            if (name is null)
            {
                if (_indexes.Any(i => i.IsUnique == isUnique &&
                                      i.Columns.SequenceEqual(columns!, StringComparer.OrdinalIgnoreCase) &&
                                      KeyDirectionsEqual(i.Directions, canonicalDirections)))
                {
                    return;
                }

                if (isUnique && _primaryKey is not null && _primaryKey.SequenceEqual(columns!, StringComparer.OrdinalIgnoreCase) &&
                    KeyDirectionsEqual(_primaryKeyDirections, canonicalDirections))
                {
                    return;
                }
            }

            var effectiveName = name ?? NextGeneratedName(isUnique);
            _indexNames.Add(effectiveName);
            _indexes.Add(new TableIndexDefinition(effectiveName, columns!.ToArray(), isUnique, canonicalDirections));
        }

        private string NextGeneratedName(bool isUnique)
        {
            var pos = _indexes.Count + 1;
            var candidate = isUnique ? UniqueIndexName(pos) : NonUniqueIndexName(pos);
            while (_indexNames.Contains(candidate))
            {
                pos++;
                candidate = isUnique ? UniqueIndexName(pos) : NonUniqueIndexName(pos);
            }

            return candidate;
        }

        private static bool HasDistinctColumns(IReadOnlyList<string> columns)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in columns)
            {
                if (string.IsNullOrWhiteSpace(col) || !seen.Add(col.Trim()))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool KeyDirectionsEqual(IReadOnlyList<KeyColumnDirection>? left, IReadOnlyList<KeyColumnDirection>? right)
        {
            if (left is null && right is null) return true;
            if (left is null || right is null) return false;
            return left.SequenceEqual(right);
        }

        private static bool DirectionsMatch(IReadOnlyList<string> columns, IReadOnlyList<KeyColumnDirection>? directions) =>
            directions is null || directions.Count == columns.Count;

        private static bool CapsHold(IReadOnlyList<string>? columns) =>
            columns is { Count: > 0 and <= TableKeyDefinitionValidator.MaxColumnsPerKey };
    }

    /// <summary>Applies a <see cref="Collector"/>: nothing declared leaves the table unchanged; an invalid declaration is dropped with a warning.</summary>
    public static TableMetadata Apply(TableMetadata metadata, Collector collector, ILogger logger, string source)
    {
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(logger);
        if (collector.Invalid)
        {
            logger.LogWarning(
                "{Source} declared an invalid key definition for table {Table} ({Rule}); the definition was dropped and the stored one kept.",
                source, metadata.Identifier, collector.InvalidRule ?? "INVALID_KEY_DEFINITION");
            return metadata;
        }

        return Apply(metadata, collector.PrimaryKey, collector.Indexes, logger, source, collector.PrimaryKeyDirections);
    }

    /// <summary>Name of the n-th (1-based) unnamed unique constraint of a source, e.g. OpenMetadata <c>UNIQUE</c> or dbt <c>unique</c>.</summary>
    public static string UniqueIndexName(int oneBasedPosition) => $"uq_{oneBasedPosition}";

    /// <summary>Name of the n-th (1-based) unnamed non-unique index of a source.</summary>
    public static string NonUniqueIndexName(int oneBasedPosition) => $"ix_{oneBasedPosition}";

    /// <summary>
    /// Returns <paramref name="metadata"/> carrying the declared definition and <c>ReplaceKeyDefinition = true</c>; or unchanged when the
    /// source declares nothing or the declaration is invalid (Warning with table id and rule, never values).
    /// </summary>
    public static TableMetadata Apply(
        TableMetadata metadata,
        IReadOnlyList<string>? primaryKey,
        IReadOnlyList<TableIndexDefinition>? indexes,
        ILogger logger,
        string source,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(logger);

        var hasKey = primaryKey is { Count: > 0 };
        var hasIndexes = indexes is { Count: > 0 };
        if (!hasKey && !hasIndexes)
        {
            return metadata;
        }

        try
        {
            var validated = TableKeyDefinitionValidator.Validate(metadata with
            {
                PrimaryKeyColumns = primaryKey ?? Array.Empty<string>(),
                PrimaryKeyDirections = primaryKeyDirections ?? Array.Empty<KeyColumnDirection>(),
                Indexes = indexes ?? Array.Empty<TableIndexDefinition>(),
                ReplaceKeyDefinition = true
            });

            logger.LogInformation(
                "{Source} replaces the declared key definition of table {Table} (primary key columns: {PrimaryKeyColumns}, indexes: {Indexes}).",
                source, metadata.Identifier, validated.PrimaryKeyColumns.Count, validated.Indexes.Count);
            return validated;
        }
        catch (TableDefinitionException ex)
        {
            logger.LogWarning(
                "{Source} declared an invalid key definition for table {Table}; the definition was dropped and the stored one kept: {Rule}",
                source, metadata.Identifier, ex.RuleCode);
            return metadata;
        }
    }
}
