namespace Autheris.Application.Streaming.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Polls CHANGETABLE changes of one tracked Microsoft SQL Server table and appends them to the durable CDC stream
/// (<see cref="ICdcStreamStore"/>). MSSQL changes are never published on the <see cref="ICdcEventChannel"/>.
/// </summary>
public interface IMssqlChangeTrackingPoller
{
    /// <summary>Polls one table and appends its changes to the stream; returns the number of appended events. <paramref name="fencingToken"/> is the table lease token guarding the append transaction.</summary>
    Task<int> PollTableChangesAsync(TableIdentifier table, long fencingToken, CancellationToken ct = default);
}
