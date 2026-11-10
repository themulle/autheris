namespace Autheris.Application.OData;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

public interface IODataQueryPolicyEnforcer
{
    ValueTask<TableAccessDecision> EnforceAsync(
        ClaimsPrincipal principal,
        TableMetadata metadata,
        IReadOnlyList<string>? requestedColumns = null,
        CancellationToken ct = default);
}

public sealed class ODataQueryPolicyEnforcer : IODataQueryPolicyEnforcer
{
    private readonly IAccessDecisionPoint _decisionPoint;

    public ODataQueryPolicyEnforcer(IAccessDecisionPoint decisionPoint)
    {
        _decisionPoint = decisionPoint ?? throw new ArgumentNullException(nameof(decisionPoint));
    }

    public ValueTask<TableAccessDecision> EnforceAsync(
        ClaimsPrincipal principal,
        TableMetadata metadata,
        IReadOnlyList<string>? requestedColumns = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(metadata);

        var tenantId = principal.GetTenantId();
        var secContext = SecurityPrincipalContext.FromPrincipal(principal, tenantId);
        var request = new AccessRequest(
            secContext,
            metadata,
            AccessOperation.Read,
            AccessTransport.OData,
            Columns: requestedColumns);

        return _decisionPoint.DecideAsync(request, ct);
    }
}
