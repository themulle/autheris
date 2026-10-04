namespace Autheris.Application.Security.Rebac.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-SEC-04: Batch DataLoader implementation for ReBAC authorization checks.
/// Batches incoming requests within a request scope and executes them via IRebacEvaluator.BatchCheckAsync.
/// </summary>
public sealed class RebacBatchDataLoader : IRebacBatchDataLoader
{
    private readonly IRebacEvaluator _evaluator;
    private readonly ILogger<RebacBatchDataLoader> _logger;
    private readonly ConcurrentBag<RebacCheckRequest> _queue = new();

    public RebacBatchDataLoader(
        IRebacEvaluator evaluator,
        ILogger<RebacBatchDataLoader> logger)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Enqueue(RebacCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _queue.Add(request);
    }

    public async ValueTask<IReadOnlyDictionary<RebacCheckRequest, bool>> ExecuteBatchAsync(string tenantId, CancellationToken ct = default)
    {
        var distinctRequests = _queue
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        if (distinctRequests.Count == 0)
        {
            return new Dictionary<RebacCheckRequest, bool>();
        }

        _logger.LogDebug("F-SEC-04 ReBAC batching {Count} distinct check requests for tenant '{Tenant}'", distinctRequests.Count, tenantId);

        var batchReq = new RebacBatchCheckRequest(tenantId, distinctRequests);
        var batchResult = await _evaluator.BatchCheckAsync(batchReq, ct).ConfigureAwait(false);

        return batchResult.Decisions;
    }

    public async ValueTask<bool> CheckAsync(string tenantId, string user, string relation, string targetObject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetObject);

        var request = new RebacCheckRequest(tenantId, user, relation, targetObject);
        var result = await _evaluator.CheckAsync(request, ct).ConfigureAwait(false);
        return result.Allowed;
    }
}
