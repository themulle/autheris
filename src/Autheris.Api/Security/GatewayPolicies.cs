namespace Autheris.Api.Security;

using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

/// <summary>
/// SEC M-03 & K-K10: Enterprise Named Authorization Policies and RBAC integration.
/// Standardizes declarative policy attachment via <c>.RequireAuthorization(GatewayPolicies.GovernanceAdmin)</c>
/// and typed role requirements via <c>.RequireGatewayRole(GatewayRole.DataSteward)</c>.
/// </summary>
public static class GatewayPolicies
{
    private static readonly IGatewayRoleEvaluator Evaluator = new GatewayRoleEvaluator();

    public const string GovernanceAdmin = "GovernanceAdmin";
    public const string Approver = "Approver";
    public const string ClusterAdmin = "ClusterAdmin";
    public const string PrivacyAdmin = "PrivacyAdmin";
    public const string SchemaAdmin = "SchemaAdmin";
    public const string SchemaPublisher = "SchemaPublisher";

    public static readonly string[] GovernanceAdminRoles = ["GovernanceAdmin", "ClusterAdmin"];
    public static readonly string[] ApproverRoles = ["DataSteward", "DataOwner", "GovernanceAdmin", "ClusterAdmin"];
    public static readonly string[] ClusterAdminRoles = ["ClusterAdmin"];
    public static readonly string[] PrivacyAdminRoles = ["GovernanceAdmin", "PrivacyAdmin", "DataProtectionOfficer", "ClusterAdmin"];
    public static readonly string[] SchemaAdminRoles = ["GovernanceAdmin", "SchemaAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin"];
    public static readonly string[] SchemaPublisherRoles = ["GovernanceAdmin", "SchemaAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin", "Developer", "DataOwner"];

    private static readonly string[] RoleClaimTypes = [ClaimTypes.Role, "role", "roles"];

    /// <summary>
    /// K-K10: Checks if principal satisfies any of the strongly-typed GatewayRole requirements,
    /// respecting role hierarchy (e.g. ClusterAdmin implies GovernanceAdmin/DataSteward).
    /// </summary>
    public static bool HasRole(ClaimsPrincipal? principal, GatewayRole role, string? tenantId = null)
        => Evaluator.HasRole(principal, role, tenantId);

    /// <summary>
    /// K-K10: Checks if principal satisfies any of the strongly-typed GatewayRoles.
    /// </summary>
    public static bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<GatewayRole> roles, string? tenantId = null)
        => Evaluator.HasAnyRole(principal, roles, tenantId);

    /// <summary>
    /// True when the principal carries any of the given roles, checking hierarchy,
    /// <see cref="ClaimsPrincipal.IsInRole"/>, or raw "role"/"roles" claims.
    /// </summary>
    public static bool HasAnyRole(ClaimsPrincipal? principal, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        // 1. Direct claim or IsInRole check
        foreach (var role in roles)
        {
            if (principal.IsInRole(role))
            {
                return true;
            }
        }

        if (principal.Claims.Any(c =>
            RoleClaimTypes.Contains(c.Type, StringComparer.Ordinal) &&
            roles.Contains(c.Value, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 2. K-K10: Hierarchy evaluation for typed role names
        foreach (var roleName in roles)
        {
            if (GatewayRoleExtensions.TryParseRole(roleName, out var targetRole))
            {
                if (Evaluator.HasRole(principal, targetRole))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Registers all named gateway policies and the authenticated-user fallback policy.
    /// </summary>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // SEC M-03: Endpoints without explicit metadata require an authenticated user.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        AddRolePolicy(options, GovernanceAdmin, GovernanceAdminRoles);
        AddRolePolicy(options, Approver, ApproverRoles);
        AddRolePolicy(options, ClusterAdmin, ClusterAdminRoles);
        AddRolePolicy(options, PrivacyAdmin, PrivacyAdminRoles);
        AddRolePolicy(options, SchemaAdmin, SchemaAdminRoles);
        AddRolePolicy(options, SchemaPublisher, SchemaPublisherRoles);
    }

    /// <summary>Roles that satisfy a named role policy (used for diagnostics only).</summary>
    public static bool TryGetPolicyRoles(string policyName, out string[] roles)
    {
        roles = policyName switch
        {
            GovernanceAdmin => GovernanceAdminRoles,
            Approver => ApproverRoles,
            ClusterAdmin => ClusterAdminRoles,
            PrivacyAdmin => PrivacyAdminRoles,
            SchemaAdmin => SchemaAdminRoles,
            SchemaPublisher => SchemaPublisherRoles,
            _ => []
        };
        return roles.Length > 0;
    }

    private static void AddRolePolicy(AuthorizationOptions options, string name, string[] roles)
    {
        options.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => HasAnyRole(ctx.User, roles)));
    }

    /// <summary>
    /// K-K10: Fluent route helper to enforce strongly-typed GatewayRole requirements on Minimal API endpoints.
    /// </summary>
    public static RouteHandlerBuilder RequireGatewayRole(this RouteHandlerBuilder builder, params GatewayRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(roles);

        return builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => HasAnyRole(ctx.User, roles)));
    }
}
