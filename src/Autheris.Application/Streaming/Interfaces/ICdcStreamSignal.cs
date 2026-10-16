namespace Autheris.Application.Streaming.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Data-free wake-up signal that a stream advanced (PLAN-CDC-99 D-A12). Carries no row data, tenant or table name.</summary>
public interface ICdcStreamSignal
{
    ValueTask NotifyAdvancedAsync(CdcStreamId id, long headOffset, CancellationToken ct = default);

    IAsyncDisposable Subscribe(Func<CdcStreamId, long, ValueTask> onAdvanced);
}
