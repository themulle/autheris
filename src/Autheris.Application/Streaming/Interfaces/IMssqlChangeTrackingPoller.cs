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
    Task<int> PollTableChangesAsync(TableIdentifier table, CancellationToken ct = default);
    Task<long?> GetCurrentDbVersionAsync(CancellationToken ct = default);
}
