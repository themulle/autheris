namespace Autheris.Modules.OpenMetadata;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Model;

public sealed partial class OpenMetadataSyncService
{
    private async Task SyncColumnClassificationsForTablesAsync(IReadOnlyList<(OpenMetadataTable Source, TableMetadata Target)> targets, List<string> warnings, CancellationToken ct)
    {
        if (_classificationRepo is null || targets.Count == 0) return;
        var activeCodes = await IngestionClassificationMapper.LoadActiveCodesAsync(_classificationRepo, ct).ConfigureAwait(false);
        var catOpts = _options.Value.Catalog;
        var now = _timeProvider.GetUtcNow();
        foreach (var (source, target) in targets)
        {
            var storedCols = target.Columns.ToDictionary(static c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
            foreach (var omCol in source.Columns)
            {
                if (!storedCols.TryGetValue(omCol.Name, out var targetCol) || omCol.Tags.Count == 0) continue;
                var (codes, unknown) = IngestionClassificationMapper.MapOpenMetadataTags(omCol.Tags.Select(static t => t.TagFQN), catOpts.GdprArticle9Tags, catOpts.PiiTags, activeCodes);
                await IngestionClassificationMapper.SyncColumnAsync(_classificationRepo, "OpenMetadata table", $"{target.Identifier}.{omCol.Name}", targetCol.Id, codes, unknown, SyncActorSid.Value, now, warnings, _logger, ct).ConfigureAwait(false);
            }
        }
    }
}
