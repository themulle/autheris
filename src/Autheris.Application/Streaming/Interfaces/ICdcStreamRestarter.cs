namespace Autheris.Application.Streaming.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Restarts a stream at the current source version after a gap (PLAN-CDC-99 D-A5, D-A14): one fenced append transaction writes a
/// <see cref="CdcStreamEventKind.Resync"/> event and moves the watermark to the current change tracking version; a halted stream is
/// re-activated. Implemented by the producer (I1-B) because it owns the source connection, the lease and the fencing token.
/// </summary>
public interface ICdcStreamRestarter
{
    /// <summary>False when the stream cannot be restarted now (source unavailable, activation failed); nothing was written then.</summary>
    Task<bool> RestartAsync(CdcStreamId id, CancellationToken ct = default);
}
