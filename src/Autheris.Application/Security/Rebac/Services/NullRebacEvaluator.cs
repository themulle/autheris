namespace Autheris.Application.Security.Rebac.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Model;

public sealed class NullRebacEvaluator : IRebacEvaluator
{
    public static readonly NullRebacEvaluator Instance = new();

    public bool IsEnabled => false;

    public ValueTask<RebacCheckResult> CheckAsync(RebacCheckRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(RebacCheckResult.Denied);

    public ValueTask<RebacBatchCheckResult> BatchCheckAsync(RebacBatchCheckRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(new RebacBatchCheckResult(new Dictionary<RebacCheckRequest, bool>()));

    public void RegisterInheritance(string baseRelation, params string[] inheritedBy) { }

    public Task InvalidateTenantCacheAsync(string tenantId, CancellationToken ct = default) => Task.CompletedTask;
}
