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

using Autheris.Api.Extensions;
using Autheris.Domain.Audit;

public static class AccessPolicyEndpoints
{
    public static IEndpointRouteBuilder MapAccessPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId}/governance/policies")
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .RequireTenantRouteMatch();

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
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapGet("/{policyId}", async (
            string tenantId,
            string policyId,
            IAccessPolicyRepository repository,
            CancellationToken ct) =>
        {
            var policy = await repository.GetPolicyByIdAsync(new TenantId(tenantId), policyId, ct).ConfigureAwait(false);
            return policy != null ? Results.Ok(policy) : Results.NotFound();
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        group.MapPost("", async (
            string tenantId,
            AccessPolicy policy,
            HttpContext httpContext,
            IScopedAccessPolicyEngine engine,
            CancellationToken ct) =>
        {
            // SEC-SC-06: Reject policies attempting to grant access to individual users
            foreach (var grantee in policy.Grantees ?? [])
            {
                if (grantee.GranteeType == AccessPolicyGranteeType.User)
                {
                    return Results.BadRequest(new { error = "Individual user grantees are prohibited in scoped access policies (SEC-SC-06). Only groups and roles are allowed." });
                }
            }

            if (string.IsNullOrWhiteSpace(policy.PolicyId))
            {
                return Results.BadRequest(new { error = "policyId is required." });
            }

            var creatorSid = httpContext.User.GetUserSid() ?? new Sid("ADMIN");
            // Security review: tenant, policy id and audit fields are set server-side; approval fields are never taken from the body.
            var sanitized = BuildServerSidePolicy(policy, tenantId, policy.PolicyId, creatorSid.Value, DateTimeOffset.UtcNow);

            await engine.CreatePolicyAsync(sanitized, creatorSid, ct).ConfigureAwait(false);
            return Results.Created($"/api/v1/tenants/{tenantId}/governance/policies/{sanitized.PolicyId}", sanitized);
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        group.MapPut("/{policyId}", async (
            string tenantId,
            string policyId,
            AccessPolicy policy,
            HttpContext httpContext,
            IAccessPolicyRepository repository,
            IScopedAccessPolicyEngine engine,
            CancellationToken ct) =>
        {
            foreach (var grantee in policy.Grantees ?? [])
            {
                if (grantee.GranteeType == AccessPolicyGranteeType.User)
                {
                    return Results.BadRequest(new { error = "Individual user grantees are prohibited in scoped access policies (SEC-SC-06). Only groups and roles are allowed." });
                }
            }

            var existing = await repository.GetPolicyByIdAsync(new TenantId(tenantId), policyId, ct).ConfigureAwait(false);
            if (existing == null)
            {
                return Results.NotFound();
            }

            var actorSid = httpContext.User.GetUserSid() ?? new Sid("ADMIN");
            // Security review: the route decides tenant and policy id (also for grantees and selectors); creation fields are kept from the
            // stored policy and approval fields are never taken from the body.
            var sanitized = BuildServerSidePolicy(policy, tenantId, policyId, existing.CreatedBy, existing.CreatedAt);

            await engine.UpdatePolicyAsync(sanitized, actorSid, ct).ConfigureAwait(false);
            return Results.Ok(sanitized);
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

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
        }).WithAudit(AuditLevel.Full, AuditEventTypes.AuditConfigChanged);

        return app;
    }

    /// <summary>
    /// Security review: builds the policy that is persisted from the request body. Only the editable content (name, description, active flag,
    /// priority, decision, grantee and selector values) comes from the body; <paramref name="tenantId"/> and <paramref name="policyId"/> come
    /// from the route and are applied to every grantee and selector (a body cannot plant rows in another tenant or policy), the creation
    /// fields are set by the server and the approval fields are never accepted from input.
    /// </summary>
    internal static AccessPolicy BuildServerSidePolicy(AccessPolicy input, string tenantId, string policyId, string createdBy, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(input);

        var policy = new AccessPolicy
        {
            TenantId = tenantId,
            PolicyId = policyId,
            Name = input.Name,
            Description = input.Description,
            IsActive = input.IsActive,
            Priority = input.Priority,
            Decision = input.Decision,
            CreatedBy = createdBy,
            CreatedAt = createdAt,
            ApprovedBy = null,
            ApprovedAt = null
        };

        foreach (var g in input.Grantees ?? [])
        {
            policy.Grantees.Add(new AccessPolicyGrantee(tenantId, policyId, g.RealmId, g.GranteeType, g.GranteeId));
        }

        foreach (var s in input.Selectors ?? [])
        {
            policy.Selectors.Add(new AccessPolicySelector(tenantId, policyId, s.SelectorType, s.SelectorValue));
        }

        return policy;
    }
}
