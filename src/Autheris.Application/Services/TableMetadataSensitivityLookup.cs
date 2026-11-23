namespace Autheris.Application.Services;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class TableMetadataSensitivityLookup : ITableSensitivityLookup
{
    private readonly Func<ITableMetadataRepository?> _repositoryAccessor;
    private readonly Func<IClassificationRepository?>? _classificationRepoAccessor;
    private readonly ILogger<TableMetadataSensitivityLookup>? _logger;

    public TableMetadataSensitivityLookup(ITableMetadataRepository repository, ILogger<TableMetadataSensitivityLookup>? logger = null, IClassificationRepository? classificationRepository = null)
        : this(() => repository, logger, classificationRepository is not null ? () => classificationRepository : null) { }

    public TableMetadataSensitivityLookup(Func<ITableMetadataRepository?> repositoryAccessor, ILogger<TableMetadataSensitivityLookup>? logger = null, Func<IClassificationRepository?>? classificationRepositoryAccessor = null)
    {
        _repositoryAccessor = repositoryAccessor ?? throw new ArgumentNullException(nameof(repositoryAccessor));
        _classificationRepoAccessor = classificationRepositoryAccessor;
        _logger = logger;
    }

    public async ValueTask<bool> HasSensitiveDataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        try
        {
            var repo = _repositoryAccessor();
            if (repo == null) return true;
            // Issue #1043: sensitivity depends on the table, its columns, masking rules and classifications only, never on the dataset
            // catalog overlay, so the epoch-validated structural metadata is read (one statement on a cache hit instead of four).
            var metadata = await repo.GetStructuralTableMetadataAsync(table, ct).ConfigureAwait(false);
            if (metadata?.Table != null)
            {
                if (ClassificationMetadata.IsTableSensitive(metadata)) return true;
                if (_classificationRepoAccessor?.Invoke() is { } classRepo && metadata.Columns is { Count: > 0 })
                {
                    // Issue #1043: one read for all columns (the owner tenant's rows on top of the hydrated global ones), not one per column.
                    var columnIds = metadata.Columns.Where(static c => c.Id != Guid.Empty).Select(static c => c.Id.ToString()).ToList();
                    if (columnIds.Count > 0)
                    {
                        var rows = await classRepo.GetColumnClassificationsBatchAsync(columnIds, metadata.OwnerTenantId, ct).ConfigureAwait(false);
                        if (rows.Values.Any(static list => list.Any(static r => !string.Equals(r.Code, ClassificationScopes.PublicCode, StringComparison.OrdinalIgnoreCase))))
                            return true;
                    }
                }

                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up: stop instead of continuing with a fail-closed answer nobody reads.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve table metadata sensitivity for {Table}; treating as highly sensitive (fail-closed).", table);
            return true;
        }
    }
}
