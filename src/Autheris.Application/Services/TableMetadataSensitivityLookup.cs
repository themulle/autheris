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

    public async ValueTask<bool> IsSensitiveAsync(TableIdentifier table, CancellationToken ct = default)
    {
        try
        {
            var repo = _repositoryAccessor();
            if (repo == null) return true;
            var metadata = await repo.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
            if (metadata?.Table != null)
            {
                if (ClassificationMetadata.IsTableSensitive(metadata)) return true;
                if (_classificationRepoAccessor?.Invoke() is { } classRepo && metadata.Columns is { Count: > 0 })
                    foreach (var col in metadata.Columns.Where(static c => c.Id != Guid.Empty))
                        if ((await classRepo.GetColumnClassificationsAsync(col.Id.ToString(), metadata.OwnerTenantId, ct).ConfigureAwait(false)).Any(static r => !string.Equals(r.Code, ClassificationScopes.PublicCode, StringComparison.OrdinalIgnoreCase)))
                            return true;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve table metadata sensitivity for {Table}; treating as highly sensitive (fail-closed).", table);
            return true;
        }
    }
}
