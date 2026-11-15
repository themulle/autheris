namespace Autheris.Application.Catalog.Semantic;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Resolves the physical base table of a semantic model (#112 M-3: the base table is selected only by the model-to-table mapping).
/// Shared by the metric query path and the import validator. A semantic virtual table (the catalog view a manifest import creates) is
/// never a base table.
/// </summary>
public sealed class SemanticBaseTableResolver
{
    private readonly ITableMetadataRepository _repository;

    public SemanticBaseTableResolver(ITableMetadataRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>The identifiers tried, in order, for <paramref name="model"/>.</summary>
    public static IReadOnlyList<TableIdentifier> Candidates(DbtSemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var candidates = new List<TableIdentifier>();
        if (!string.IsNullOrWhiteSpace(model.NodeRelation))
        {
            var cleaned = model.NodeRelation.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal).Trim();
            if (TableIdentifier.TryParse(cleaned, out var parsed))
            {
                candidates.Add(parsed);
            }

            var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                candidates.Add(new TableIdentifier("default", parts[0], parts[1]));
            }
            else if (parts.Length == 1)
            {
                candidates.Add(new TableIdentifier("default", "public", parts[0]));
                candidates.Add(new TableIdentifier("default", "dbo", parts[0]));
            }
        }

        if (TableIdentifier.TryParse(model.Name, out var modelId))
        {
            candidates.Add(modelId);
        }

        try
        {
            candidates.Add(new TableIdentifier("default", "public", model.Name));
        }
        catch (ArgumentException)
        {
            // A model name that cannot be a table name is never a candidate (the manifest validator rejects it at import).
        }

        return candidates;
    }

    /// <summary>The active base table of the model, or <c>null</c> when none resolves.</summary>
    /// <param name="isVisible">Optional scope filter: a table it rejects is treated as absent (the caller cannot see it).</param>
    public async Task<TableMetadata?> TryResolveAsync(DbtSemanticModel model, Func<TableMetadata, bool>? isVisible = null, CancellationToken ct = default)
    {
        var candidates = Candidates(model);
        foreach (var candidate in candidates)
        {
            var meta = await _repository.GetTableMetadataAsync(candidate, ct).ConfigureAwait(false)
                ?? Autheris.Application.DataSources.VirtualSystemTables.Get(candidate);
            if (meta != null && meta.Table.IsActive && !SemanticVirtualTables.IsVirtualTable(meta) && (isVisible is null || isVisible(meta)))
            {
                return meta;
            }
        }

        var relFull = model.NodeRelation?.Replace("\"", string.Empty, StringComparison.Ordinal);
        var relLeaf = (relFull ?? model.Name).Split('.').LastOrDefault()?.Trim();
        bool MatchesModel(TableMetadata t) =>
            t.Table.IsActive &&
            !SemanticVirtualTables.IsVirtualTable(t) &&
            (isVisible is null || isVisible(t)) &&
            (string.Equals(t.Identifier.TableName, relLeaf, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t.Identifier.TableName, model.Name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t.Identifier.ToString(), relFull, StringComparison.OrdinalIgnoreCase));

        if (NormalizePrefix(relLeaf) is { } leafPrefix)
        {
            var foundByLeaf = await CatalogTablePageReader.FindFirstAsync(
                _repository,
                new CatalogTableQuery(NamePrefix: leafPrefix, Limit: CatalogTableQuery.MaxLimit),
                MatchesModel,
                ct: ct).ConfigureAwait(false);
            if (foundByLeaf is not null)
            {
                return foundByLeaf;
            }
        }

        if (NormalizePrefix(model.Name) is { } namePrefix &&
            !string.Equals(model.Name, relLeaf, StringComparison.OrdinalIgnoreCase))
        {
            return await CatalogTablePageReader.FindFirstAsync(
                _repository,
                new CatalogTableQuery(NamePrefix: namePrefix, Limit: CatalogTableQuery.MaxLimit),
                MatchesModel,
                ct: ct).ConfigureAwait(false);
        }

        return null;
    }

    private static string? NormalizePrefix(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
        {
            return null;
        }

        return candidate.Length <= CatalogTableQuery.MaxFilterLength
            ? candidate
            : candidate[..CatalogTableQuery.MaxFilterLength];
    }
}
