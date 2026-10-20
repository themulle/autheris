namespace Autheris.Api.Endpoints;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Governance.Policies;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class AccessPolicyEndpoints
{
    public static IEndpointRouteBuilder MapAccessPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId}/governance/policies")
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin);

        group.MapGet("", async (
            string tenantId,
            bool? activeOnly,
            IAccessPolicyRepository repository,
            CancellationToken ct) =>
        {
            var policies = await repository.GetPoliciesAsync(new TenantId(tenantId), ct).ConfigureAwait(false);
            if (activeOnly == true)
            {
                policies = policies.Where(p => p.IsActive).ToList();
            }
            return Results.Ok(policies);
        });

        group.MapGet("/{policyId}", async (
            string tenantId,
            string policyId,
            IAccessPolicyRepository repository,
            CancellationToken ct) =>
        {
            var policy = await repository.GetPolicyByIdAsync(new TenantId(tenantId), policyId, ct).ConfigureAwait(false);
            return policy != null ? Results.Ok(policy) : Results.NotFound();
        });

        group.MapPost("", async (
            string tenantId,
            AccessPolicy policy,
            HttpContext httpContext,
            IScopedAccessPolicyEngine engine,
            CancellationToken ct) =>
        {
            // SEC-SC-06: Reject policies attempting to grant access to individual users
            foreach (var grantee in policy.Grantees)
            {
                if (grantee.GranteeType == AccessPolicyGranteeType.User)
                {
                    return Results.BadRequest(new { error = "Individual user grantees are prohibited in scoped access policies (SEC-SC-06). Only groups and roles are allowed." });
                }
            }

            var creatorSid = httpContext.User.GetUserSid() ?? new Sid("ADMIN");
            policy.TenantId = tenantId;
            policy.CreatedBy = creatorSid.Value;
            policy.CreatedAt = DateTimeOffset.UtcNow;

            await engine.CreatePolicyAsync(policy, creatorSid, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/tenants/{tenantId}/governance/policies/{policy.PolicyId}", policy);
        });

        group.MapPut("/{policyId}", async (
            string tenantId,
            string policyId,
            AccessPolicy policy,
            HttpContext httpContext,
            IScopedAccessPolicyEngine engine,
            CancellationToken ct) =>
        {
            foreach (var grantee in policy.Grantees)
            {
                if (grantee.GranteeType == AccessPolicyGranteeType.User)
                {
                    return Results.BadRequest(new { error = "Individual user grantees are prohibited in scoped access policies (SEC-SC-06). Only groups and roles are allowed." });
                }
            }

            var actorSid = httpContext.User.GetUserSid() ?? new Sid("ADMIN");
            policy.TenantId = tenantId;
            policy.PolicyId = policyId;

            await engine.UpdatePolicyAsync(policy, actorSid, ct).ConfigureAwait(false);
            return Results.Ok(policy);
        });

        group.MapDelete("/{policyId}", async (
            string tenantId,
            string policyId,
            HttpContext httpContext,
            IScopedAccessPolicyEngine engine,
            CancellationToken ct) =>
        {
            var actorSid = httpContext.User.GetUserSid() ?? new Sid("ADMIN");
            await engine.DeletePolicyAsync(new TenantId(tenantId), policyId, actorSid, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        return app;
    }
}
