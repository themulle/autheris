namespace Autheris.Application.Services;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Microsoft.Extensions.Logging;

public sealed class TableMetadataSensitivityLookup : ITableSensitivityLookup
{
    private readonly Func<ITableMetadataRepository?> _repositoryAccessor;
    private readonly ILogger<TableMetadataSensitivityLookup>? _logger;

    public TableMetadataSensitivityLookup(
        ITableMetadataRepository repository,
        ILogger<TableMetadataSensitivityLookup>? logger = null)
        : this(() => repository, logger)
    {
    }

    public TableMetadataSensitivityLookup(
        Func<ITableMetadataRepository?> repositoryAccessor,
        ILogger<TableMetadataSensitivityLookup>? logger = null)
    {
        _repositoryAccessor = repositoryAccessor ?? throw new ArgumentNullException(nameof(repositoryAccessor));
        _logger = logger;
    }

    public async ValueTask<bool> IsSensitiveAsync(TableIdentifier table, CancellationToken ct = default)
    {
        try
        {
            var repo = _repositoryAccessor();
            if (repo == null)
            {
                return true; // Fail closed if repository is not available
            }

            var metadata = await repo.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
            if (metadata?.Table != null)
            {
                return metadata.Table.IsHighlySensitive
                    || (metadata.Columns != null && metadata.Columns.Any(c => c.IsSensitive))
                    || (metadata.ColumnMaskingRules != null && metadata.ColumnMaskingRules.Count > 0);
            }

            return true; // Fail closed if table metadata is missing
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve table metadata sensitivity for {Table}; treating as highly sensitive (fail-closed).", table);
            return true; // Fail closed on error
        }
    }
}
