namespace Autheris.Infrastructure.Streaming;

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.Metrics;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

/// <summary>
/// Durable, fenced MSSQL Change Tracking watermark store on top of the governance database (WP-ARC-A-11).
/// Recovery after a lost or corrupted watermark: delete the row of the table in <c>autheris_cdc_watermarks</c>; the
/// poller then restarts at the current change tracking version.
/// </summary>
public sealed class GovernanceDbMssqlWatermarkStore : IMssqlWatermarkStore
{
    private const string Source = "mssql_ct";

    private static readonly Meter Meter = new("Autheris.Core");
    private static readonly Counter<long> FencedTotal =
        Meter.CreateCounter<long>("autheris.cdc.watermark.fenced", description: "Number of CDC watermark writes rejected because a newer fencing token exists");

    private readonly ICdcWatermarkRepository _repository;
    private readonly ILogger<GovernanceDbMssqlWatermarkStore> _logger;

    public GovernanceDbMssqlWatermarkStore(ICdcWatermarkRepository repository, ILogger<GovernanceDbMssqlWatermarkStore> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsDurable => true;

    public async ValueTask<long?> GetWatermarkAsync(TableIdentifier table, CancellationToken ct = default) =>
        await _repository.GetAsync(Source, table.Domain, table.Schema, table.TableName, ct).ConfigureAwait(false);

    public async ValueTask<bool> TryAdvanceWatermarkAsync(TableIdentifier table, long watermark, long fencingToken, CancellationToken ct = default)
    {
        var accepted = await _repository.TryAdvanceAsync(Source, table.Domain, table.Schema, table.TableName, watermark, fencingToken, ct).ConfigureAwait(false);
        if (!accepted)
        {
            FencedTotal.Add(1);
            _logger.LogCritical("CDC watermark write for {Table} was rejected: fencing token {Token} is stale. Another replica owns the lease, or the fence counter was reset: check cluster-state persistence (AOF).", table, fencingToken);
        }

        return accepted;
    }
}
