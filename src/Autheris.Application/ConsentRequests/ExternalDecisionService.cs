namespace Autheris.Application.ConsentRequests;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Tenancy;

/// <summary>
/// External decisions are decisions made by a mapped person through a transport. This service validates the evidence and hands the
/// decision to <see cref="IPermissionRequestEngine"/> as that person; eligibility, separation of duties, validity and first-decision-wins
/// are the engine's. It never writes requests or grants.
/// </summary>
public sealed class ExternalDecisionService(
    IPermissionRequestEngine engine,
    IExternalDecisionGuard guard,
    Autheris.Application.Common.Interfaces.IConsentApprovalRepository approvals,
    IConsentWorkflowConfigRepository workflowConfig,
    IConsentWorkflowRouter router) : IExternalDecisionService
{
    public async Task<PermissionDecisionResult> DecideAsync(ExternalDecisionEnvelope envelope, TenantId tenant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var request = await approvals.GetConsentRequestAsync(envelope.RequestId, ct).ConfigureAwait(false);
        if (request is null || request.TenantId != tenant || request.ExternalSystemId != envelope.ExternalSystemId)
        {
            throw new ConsentDecisionException("NOT_FOUND", "Permission request was not found.", 404);
        }

        var system = await workflowConfig.GetExternalWorkflowSystemAsync(request.TenantId, envelope.ExternalSystemId, ct).ConfigureAwait(false);
        // The same effective route (domain, then tenant; removed or unknown scopes inert) that directed the request at submission.
        var route = await router.ResolveForTableAsync(request.TenantId, request.TableIdentifier, ct).ConfigureAwait(false);

        var validated = await guard.ValidateAsync(envelope, request, route, system, ct).ConfigureAwait(false);

        return await engine.DecideAsync(
            new PermissionDecisionCommand(
                envelope.RequestId,
                envelope.ExpectedVersion,
                validated.Approver,
                envelope.Approve,
                ValidTo: null,
                Reason: envelope.Reason,
                External: validated.Evidence),
            ct).ConfigureAwait(false);
    }
}
