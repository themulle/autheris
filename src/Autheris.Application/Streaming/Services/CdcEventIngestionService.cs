namespace Autheris.Application.Streaming.Services;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class CdcEventIngestionService : ICdcEventIngestionService
{
    private readonly ICdcEventChannel _eventChannel;
    private readonly ILogger<CdcEventIngestionService> _logger;

    public CdcEventIngestionService(
        ICdcEventChannel eventChannel,
        ILogger<CdcEventIngestionService> logger)
    {
        _eventChannel = eventChannel ?? throw new ArgumentNullException(nameof(eventChannel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask PublishEventAsync(CdcEvent cdcEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);

        _logger.LogDebug(
            "Ingesting CDC event '{EventId}' for table '{Table}' ({Operation})",
            cdcEvent.EventId, cdcEvent.Table.ToQualifiedName(), cdcEvent.Operation);

        await _eventChannel.PublishAsync(cdcEvent, ct);
    }
}
