namespace Autheris.Application.Governance.Services;

using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Issue #111 WP-6 (A-2, A-3): see <see cref="IDatasetTagImportService"/>.</summary>
public sealed class DatasetTagImportService : IDatasetTagImportService
{
    private static readonly Sid ImportActor = new("system:dbt-import");

    private readonly ITableMetadataRepository _tables;
    private readonly IAuditWriter _audit;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ILogger<DatasetTagImportService> _logger;

    public DatasetTagImportService(
        ITableMetadataRepository tables, IAuditWriter audit, IOptionsMonitor<GatewayOptions> options, ILogger<DatasetTagImportService> logger)
    {
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DatasetTagImportResult> ImportDbtTagsAsync(TableIdentifier table, IEnumerable<string?> rawTags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rawTags);

        var valid = new SortedSet<string>(StringComparer.Ordinal);
        var invalid = 0;
        foreach (var raw in rawTags)
        {
            if (DatasetTagRules.TryNormalize(raw, out var tag))
            {
                valid.Add(tag);
            }
            else
            {
                invalid++;
            }
        }

        DatasetCatalogSettings? before;
        try
        {
            before = await _tables.GetDatasetCatalogSettingsAsync(table, strict: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The dataset tags of a table could not be read; the dbt tags were not imported.");
            return new DatasetTagImportResult(false, invalid, 0, Failed: true);
        }

        if (before is null)
        {
            return DatasetTagImportResult.Unchanged;
        }

        // The total across sources is capped; a dbt tag that already exists as admin tag is stored once and costs nothing.
        var admin = new HashSet<string>(before.Tags.Where(t => t.Source == DatasetTagSource.Admin).Select(t => t.Tag), StringComparer.Ordinal);
        var budget = _options.CurrentValue.Catalog.MaxTagsPerDataset - admin.Count;
        var kept = new List<string>();
        var overflow = 0;
        foreach (var tag in valid)
        {
            if (admin.Contains(tag))
            {
                kept.Add(tag);
            }
            else if (budget > 0)
            {
                kept.Add(tag);
                budget--;
            }
            else
            {
                overflow++;
            }
        }

        if (invalid > 0 || overflow > 0)
        {
            _logger.LogWarning("dbt tags of a table were reduced: {Invalid} invalid and {Overflow} over the cap dropped.", invalid, overflow);
        }

        DatasetCatalogChange? change;
        try
        {
            change = await _tables.ReplaceDatasetTagsAsync(table, kept, DatasetTagSource.Dbt, ImportActor, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AuditUnavailableException)
        {
            _logger.LogWarning(ex, "The dbt tags of a table could not be written.");
            return new DatasetTagImportResult(false, invalid, overflow, Failed: true);
        }

        if (change is null)
        {
            return DatasetTagImportResult.Unchanged;
        }

        if (!change.Changed)
        {
            return new DatasetTagImportResult(false, invalid, overflow, false);
        }

        await _audit.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = TenantId.Platform, // the dbt import is tenant-less platform work (DT-A-6); a default(TenantId) audit row is refused
            EventType = AuditEventTypes.CatalogTagsChanged,
            ActorSid = ImportActor,
            TargetTable = table.ToString(),
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                before = View(change.Before),
                after = View(change.After),
                source = "dbt"
            })
        }, CancellationToken.None).ConfigureAwait(false);
        return new DatasetTagImportResult(true, invalid, overflow, false);
    }

    private static object View(DatasetCatalogSettings s) => new
    {
        visibility = s.Visibility.ToString(),
        tags = s.Tags.Select(t => t.Tag).OrderBy(t => t, StringComparer.Ordinal).ToArray()
    };
}
