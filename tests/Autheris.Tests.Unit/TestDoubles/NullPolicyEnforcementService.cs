namespace Autheris.Tests.Unit.TestDoubles;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>Allow-all <see cref="IPolicyEnforcementService"/> for isolated unit tests only (test-only; never referenced by production code).</summary>
public sealed class NullPolicyEnforcementService : IPolicyEnforcementService
{
    public static readonly NullPolicyEnforcementService Instance = new();

    public ValueTask<TableAccessDecision> EvaluatePolicyAsync(SecurityEvaluationContext context, CancellationToken ct = default)
    {
        var table = context.TargetTable;
        return ValueTask.FromResult(TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
    }

    public bool HasPolicies(TenantId tenant) => false;

    public Task ReloadPoliciesAsync(TenantId tenant, CancellationToken ct = default) => Task.CompletedTask;
}
