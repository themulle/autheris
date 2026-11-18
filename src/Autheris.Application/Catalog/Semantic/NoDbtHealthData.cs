namespace Autheris.Application.Catalog.Semantic;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Core default of <see cref="IDbtHealthCircuitBreaker"/>: reports "no dbt health data" (every table healthy, nothing recorded),
/// which is the behaviour for tables without dbt tests. The dbt integration replaces it with the recording implementation.
/// </summary>
public sealed class NoDbtHealthData : IDbtHealthCircuitBreaker
{
    public ValueTask<DbtHealthState> GetTableHealthAsync(TableIdentifier table, CancellationToken ct = default) =>
        ValueTask.FromResult(new DbtHealthState(table, DbtModelHealthStatus.Healthy, [], TimeProvider.System.GetUtcNow()));

    public Task<DbtHealthState> SetTableHealthAsync(TableIdentifier table, DbtModelHealthStatus status, IReadOnlyList<DbtTestFailure> failures, CancellationToken ct = default) =>
        Task.FromResult(new DbtHealthState(table, DbtModelHealthStatus.Healthy, [], TimeProvider.System.GetUtcNow()));

    public Task<IReadOnlyDictionary<TableIdentifier, DbtHealthState>> GetAllHealthStatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<TableIdentifier, DbtHealthState>>(FrozenDictionary<TableIdentifier, DbtHealthState>.Empty);

    public Task<DbtRunResultsReport> RecordRunResultsAsync(Stream runResultsStream, TenantId tenant, CancellationToken ct = default) =>
        Task.FromResult(new DbtRunResultsReport("unknown", TimeProvider.System.GetUtcNow(), TimeSpan.Zero, []));

    public Task ResetTableHealthAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResetAllAsync(CancellationToken ct = default) => Task.CompletedTask;
}
