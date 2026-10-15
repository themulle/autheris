namespace Autheris.Application.Streaming.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Service responsible for polling CHANGETABLE changes from a Microsoft SQL Server database
/// and publishing them as strongly typed CdcEvents to the ICdcEventChannel.
/// </summary>
public interface IMssqlChangeTrackingPoller
{
    /// <summary>Polls one table and publishes its changes. <paramref name="fencingToken"/> is the lease token guarding the watermark commit.</summary>
    Task<int> PollTableChangesAsync(TableIdentifier table, long fencingToken, CancellationToken ct = default);
    Task<long?> GetCurrentDbVersionAsync(CancellationToken ct = default);
}
