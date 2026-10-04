namespace Autheris.Application.Streaming.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

public interface ICdcEventChannel
{
    ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default);
    IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default);
}

public interface ICdcEventIngestionService
{
    ValueTask PublishEventAsync(CdcEvent cdcEvent, CancellationToken ct = default);
}
