namespace Autheris.GraphQL.Interceptors;

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
using HotChocolate.Execution;
using RequestDelegate = HotChocolate.Execution.RequestDelegate;

public sealed class GraphQlDataSecurityMiddleware
{
    private readonly RequestDelegate _next;

    public GraphQlDataSecurityMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async ValueTask InvokeAsync(HotChocolate.Execution.RequestContext context)
    {
        await _next(context).ConfigureAwait(false);
    }

    public static ValueTask<TableAccessDecision> DecideAccessAsync(
        IAccessDecisionPoint decisionPoint,
        ClaimsPrincipal principal,
        TableMetadata metadata,
        IReadOnlyList<string>? requestedFields = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decisionPoint);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(metadata);

        var tenantId = principal.GetTenantId();
        var secContext = SecurityPrincipalContext.FromPrincipal(principal, tenantId);
        var request = new AccessRequest(
            secContext,
            metadata,
            AccessOperation.Read,
            AccessTransport.GraphQl,
            Columns: requestedFields);

        return decisionPoint.DecideAsync(request, ct);
    }
}
