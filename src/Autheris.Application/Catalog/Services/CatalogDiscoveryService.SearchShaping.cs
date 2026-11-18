namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>Search-hit text shaping: the visible-token set of a restricted table and the per-caller hidden-text column resolution.</summary>
public sealed partial class CatalogDiscoveryService
{
    // Whole tokens of the visible text (identifier, description, visible columns with type and description), split like the query terms
    // and additionally on underscores, so a short term never matches inside an unrelated hidden-column word.
    private static HashSet<string> VisibleTokens(TableMetadata table, IReadOnlySet<string>? hiddenTextColumns = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(table.Identifier.Domain).Append(' ')
          .Append(table.Identifier.Schema).Append(' ')
          .Append(table.Identifier.TableName).Append(' ')
          .Append(table.Table.DisplayName).Append(' ')
          .Append(table.Table.Description).Append(' ');

        if (table.Table.Texts is not null)
        {
            foreach (var (_, entry) in table.Table.Texts.Entries)
            {
                sb.Append(entry.DisplayName).Append(' ')
                  .Append(entry.Description).Append(' ')
                  .Append(entry.LongDescription).Append(' ');
            }
        }

        foreach (var c in table.Columns)
        {
            sb.Append(c.ColumnName).Append(' ')
              .Append(c.DataType).Append(' ');
            if (hiddenTextColumns?.Contains(c.ColumnName) == true)
            {
                continue;
            }

            sb.Append(c.DisplayName).Append(' ')
              .Append(c.Description).Append(' ')
              .Append(c.LongDescription).Append(' ');

            if (c.Texts is not null)
            {
                foreach (var (_, entry) in c.Texts.Entries)
                {
                    sb.Append(entry.DisplayName).Append(' ')
                      .Append(entry.Description).Append(' ');
                }
            }
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in sb.ToString().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            tokens.Add(t);
            var folded = DiacriticNormalizer.FoldGermanUmlauts(t);
            if (!string.Equals(t, folded, StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(folded);
            }
            var stripped = DiacriticNormalizer.StripAccents(t);
            if (!string.Equals(t, stripped, StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(stripped);
            }
        }

        return tokens;
    }

    // Columns whose catalog text is HIDDEN for this caller, per hit table; only restricted tables are shaped by visible text, so only they are resolved.
    private async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>>> ResolveHiddenTextForHitsAsync(
        IEnumerable<TableIdentifier> hitTables,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> permitted,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> all,
        RequestContext context,
        CancellationToken ct)
    {
        var result = new Dictionary<TableIdentifier, IReadOnlySet<string>>();
        var hiddenResolver = new Autheris.Application.Policy.Classification.ClassificationAccessResolver.IndexingHiddenTextResolver(_classificationRepository);
        foreach (var id in hitTables.Distinct())
        {
            if (!permitted.TryGetValue(id, out var visible) || !IsColumnRestricted(id, permitted, all))
            {
                continue;
            }

            var hidden = await hiddenResolver.ResolveAsync(visible, ct).ConfigureAwait(false);
            if (hidden.Count == 0)
            {
                continue;
            }

            var callerClear = await ResolveCallerClearColumnsAsync(visible, context, ct).ConfigureAwait(false);
            result[id] = hidden.Where(c => !callerClear.Contains(c)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private static readonly char[] TokenSeparators = [' ', '\t', '\r', '\n', ',', ';', '.', '(', ')', '"', '\'', '/', '\\', '-', '_', ':'];

    /// <summary>
    /// Issue #804, path without the discovery service: the caller's status from the visible row, in the vocabulary of
    /// <see cref="CatalogVisibility.ComputeAccessStatus"/>. Without the tier model a visible row is either a consent row
    /// (<see cref="CatalogView.Granted"/>) or an administrator row, so it is always readable (<c>GRANTED</c>).
    /// </summary>
    private static string LegacyAccessStatusOf(TableMetadata row) =>
        row.CatalogView is CatalogView.Granted or CatalogView.Unmarked
            ? Autheris.Application.Catalog.Discovery.CatalogAccessStatus.Granted
            : Autheris.Application.Catalog.Discovery.CatalogAccessStatus.None;
}
