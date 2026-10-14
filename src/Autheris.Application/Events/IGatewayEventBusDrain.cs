namespace Autheris.Application.Events;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Test helper: completes once every event accepted by the bus so far has been handled, timed out or dropped.
/// Not intended for production code paths.
/// </summary>
public interface IGatewayEventBusDrain
{
    Task DrainAsync(CancellationToken ct = default);
}
