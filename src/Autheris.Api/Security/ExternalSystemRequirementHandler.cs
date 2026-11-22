namespace Autheris.Api.Security;

using System;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;

public enum ExternalSystemOperation
{
    ConsentDecide,
    GrantRevoke
}

public sealed class ExternalSystemRequirement(ExternalSystemOperation operation) : IAuthorizationRequirement
{
    public ExternalSystemOperation Operation { get; } = operation;
}

/// <summary>
/// Enforces app-role vs delegated-scope boundaries for consent decision and grant revocation endpoints.
/// Delegated user tokens are strictly rejected from external system decision/revocation scopes.
/// Service principals must hold the required app role and a client identifier; tenant/system binding is checked in the endpoint handler.
/// </summary>
public sealed class ExternalSystemRequirementHandler(
    IConsentDecisionGuard decisionGuard,
    IConsentDecisionDenialRecorder? denialRecorder = null) : AuthorizationHandler<ExternalSystemRequirement>
{
    private readonly IConsentDecisionGuard _decisionGuard = decisionGuard ?? throw new ArgumentNullException(nameof(decisionGuard));
    private readonly IConsentDecisionDenialRecorder? _denialRecorder = denialRecorder;

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ExternalSystemRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true || user.IsReadOnly())
        {
            return;
        }

        if (requirement.Operation == ExternalSystemOperation.ConsentDecide)
        {
            await HandleDecideAsync(context, requirement, user).ConfigureAwait(false);
            return;
        }

        HandleRevoke(context, requirement, user);
    }

    private async Task HandleDecideAsync(
        AuthorizationHandlerContext context,
        ExternalSystemRequirement requirement,
        System.Security.Claims.ClaimsPrincipal user)
    {
        // A delegated user token MUST NEVER satisfy external decision scopes.
        if (ExternalApiPermissions.HasDelegatedScope(user, ExternalApiPermissions.ConsentDecide))
        {
            await RecordDenialAsync(user, ConsentDecisionDenialReasons.NotPerson).ConfigureAwait(false);
            return;
        }

        if (ExternalApiPermissions.IsServicePrincipal(user))
        {
            if (ExternalApiPermissions.HasAppRole(user, ExternalApiPermissions.ConsentDecide)
                && !string.IsNullOrWhiteSpace(ExternalApiPermissions.GetClientId(user)))
            {
                context.Succeed(requirement);
            }
            else
            {
                await RecordDenialAsync(user, ConsentDecisionDenialReasons.NotPerson).ConfigureAwait(false);
            }

            return;
        }

        var verdict = _decisionGuard.Evaluate(user);
        if (!verdict.Allowed)
        {
            await RecordDenialAsync(user, verdict.ReasonClass).ConfigureAwait(false);
            return;
        }

        if (GatewayPolicies.HasAnyRole(user, GatewayPolicies.GovernanceAdminRoles))
        {
            context.Succeed(requirement);
        }
    }

    private static void HandleRevoke(
        AuthorizationHandlerContext context,
        ExternalSystemRequirement requirement,
        System.Security.Claims.ClaimsPrincipal user)
    {
        if (ExternalApiPermissions.HasDelegatedScope(user, ExternalApiPermissions.GrantsRevoke)
            && !Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(user)
            && !GatewayPolicies.HasAnyRole(user, GatewayPolicies.AdminOnlyRoles))
        {
            return;
        }

        if (ExternalApiPermissions.IsServicePrincipal(user))
        {
            if (ExternalApiPermissions.HasAppRole(user, ExternalApiPermissions.GrantsRevoke)
                && !string.IsNullOrWhiteSpace(ExternalApiPermissions.GetClientId(user)))
            {
                context.Succeed(requirement);
            }

            return;
        }

        // Interactive human caller: grantee self-revocation, table data-owner, or admin is verified against the target grant in the handler.
        if (!ExternalApiPermissions.HasAnyExternalScope(user)
            || Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(user)
            || GatewayPolicies.HasAnyRole(user, GatewayPolicies.AdminOnlyRoles))
        {
            context.Succeed(requirement);
        }
    }

    private async Task RecordDenialAsync(System.Security.Claims.ClaimsPrincipal user, string reasonClass)
    {
        if (_denialRecorder is null)
        {
            return;
        }

        var claim = TenantClaimReader.Read(user);
        var tenantId = claim.Kind == ClaimTenantKind.Valid ? claim.Tenant : TenantId.Platform;
        await _denialRecorder.RecordAsync(user, reasonClass, tenantId).ConfigureAwait(false);
    }
}
