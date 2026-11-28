using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;

namespace Autheris.Tests.Unit.TestDoubles;

/// <summary>Cost quota that records every call and refuses to answer: a path that must not touch the quota fails loudly.</summary>
internal sealed class RecordingCostQuota : IQueryCostQuota
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls;

    public Task<QueryCostAdmission> AdmitAsync(QueryCostRequest request, CancellationToken ct)
    {
        _calls.Add(nameof(AdmitAsync));
        throw new InvalidOperationException("The cost quota must not be consulted.");
    }

    public Task<CostQuotaResult?> SettleAsync(QueryCostAdmission admission, int actualCost, CancellationToken ct)
    {
        _calls.Add(nameof(SettleAsync));
        throw new InvalidOperationException("The cost quota must not be consulted.");
    }

    public Task ChargeFailureFeeAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        _calls.Add(nameof(ChargeFailureFeeAsync));
        throw new InvalidOperationException("The cost quota must not be consulted.");
    }
}
