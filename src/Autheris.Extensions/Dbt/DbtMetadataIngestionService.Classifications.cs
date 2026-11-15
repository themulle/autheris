namespace Autheris.Extensions.Dbt;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Model;

public sealed partial class DbtMetadataIngestionService
{
    private readonly IClassificationRepository? _classificationRepository;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    private async Task SyncColumnClassificationsAsync(IReadOnlyList<(TableMetadata Metadata, IReadOnlyList<string> Tags)> pendingModelUpdates, List<string> warnings, CancellationToken ct)
    {
        if (_classificationRepository is null || pendingModelUpdates.Count == 0) return;
        var activeCodes = await IngestionClassificationMapper.LoadActiveCodesAsync(_classificationRepository, ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        foreach (var (metadata, _) in pendingModelUpdates)
        {
            foreach (var col in metadata.Columns)
            {
                var (codes, unknown) = IngestionClassificationMapper.MapDbtMeta(col.Meta, activeCodes);
                await IngestionClassificationMapper.SyncColumnAsync(_classificationRepository, "dbt table", $"{metadata.Identifier}.{col.ColumnName}", col.Id, codes, unknown, "system:dbt-sync", now, warnings, _logger, ct).ConfigureAwait(false);
            }
        }
    }
}
