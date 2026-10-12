namespace Autheris.Application.Policy.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

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
