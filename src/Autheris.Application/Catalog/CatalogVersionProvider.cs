namespace Autheris.Application.Catalog;

using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Computes a lightweight catalog version stamp from active table metadata count, max UpdatedAt ticks (when present in metadata),
/// and deterministic structural digest.
/// </summary>
public sealed class CatalogVersionProvider(ITableMetadataRepository metadataRepository) : ICatalogVersionProvider
{
    private readonly ITableMetadataRepository _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var tables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        if (tables.Count == 0)
        {
            return "0:0:empty";
        }

        long maxUpdatedTicks = 0;
        var sb = new StringBuilder(tables.Count * 64);

        foreach (var meta in tables
                     .OrderBy(static t => t.Identifier.Domain, StringComparer.Ordinal)
                     .ThenBy(static t => t.Identifier.Schema, StringComparer.Ordinal)
                     .ThenBy(static t => t.Identifier.TableName, StringComparer.Ordinal))
        {
            long tableTicks = ExtractUpdatedAtTicks(meta);
            if (tableTicks > maxUpdatedTicks)
            {
                maxUpdatedTicks = tableTicks;
            }

            sb.Append(meta.Identifier.Domain).Append('.')
              .Append(meta.Identifier.Schema).Append('.')
              .Append(meta.Identifier.TableName).Append(':')
              .Append(meta.Table.IsActive ? '1' : '0').Append(':')
              .Append(meta.Table.SourceType).Append(':')
              .Append(meta.Columns.Count.ToString(CultureInfo.InvariantCulture)).Append(';');

            foreach (var col in meta.Columns)
            {
                sb.Append(col.ColumnName).Append('=').Append(col.DataType).Append(',');
            }
        }

        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        return string.Create(CultureInfo.InvariantCulture, $"{tables.Count}:{maxUpdatedTicks}:{digest}");
    }

    private static long ExtractUpdatedAtTicks(TableMetadata metadata)
    {
        long max = 0;
        foreach (var col in metadata.Columns)
        {
            if (col.Meta != null &&
                (col.Meta.TryGetValue("updated_at", out var raw) || col.Meta.TryGetValue("updatedAt", out raw)) &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) &&
                parsed.UtcTicks > max)
            {
                max = parsed.UtcTicks;
            }
        }

        return max;
    }
}
