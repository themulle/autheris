namespace Autheris.Application.Dbt.Interfaces;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IDbtHealthCircuitBreaker
{
    ValueTask<DbtHealthState> GetTableHealthAsync(TableIdentifier table, CancellationToken ct = default);
    Task<DbtHealthState> SetTableHealthAsync(TableIdentifier table, DbtModelHealthStatus status, IReadOnlyList<DbtTestFailure> failures, CancellationToken ct = default);
    Task<IReadOnlyDictionary<TableIdentifier, DbtHealthState>> GetAllHealthStatesAsync(CancellationToken ct = default);
    Task<DbtRunResultsReport> RecordRunResultsAsync(Stream runResultsStream, CancellationToken ct = default);
    Task ResetTableHealthAsync(TableIdentifier table, CancellationToken ct = default);
    Task ResetAllAsync(CancellationToken ct = default);
}
