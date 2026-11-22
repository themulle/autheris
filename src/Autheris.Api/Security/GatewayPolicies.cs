namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security;
using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Enterprise named authorization policies and RBAC integration.
/// Standardizes declarative policy attachment via <c>.RequireAuthorization(GatewayPolicies.GovernanceAdmin)</c>
/// and typed role requirements via <c>.RequireGatewayRole(GatewayRole.GovernanceAdmin)</c>.
/// </summary>
public static class GatewayPolicies
{
    private static readonly IGatewayRoleEvaluator Evaluator = new GatewayRoleEvaluator();

    public const string GovernanceAdmin = "GovernanceAdmin";
    public const string ItAdmin = nameof(ItAdmin);
    public const string AuditRead = nameof(AuditRead);
    public const string ClusterAdmin = "ClusterAdmin";
    public const string SchemaPublisher = "SchemaPublisher";

    /// <summary>Prometheus scrape endpoint; any authenticated user is not enough.</summary>
    public const string MetricsScrape = "MetricsScrape";

    // Declarative endpoint authorization policies
    public const string AdminOnly = "AdminOnly";
    public const string AuthenticatedCaller = "AuthenticatedCaller";
    public const string MetricsScraper = "MetricsScraper";
    public const string McpCaller = nameof(McpCaller);
    public const string UsageAdmin = "UsageAdmin";
    public const string CatalogRead = "CatalogRead";
    public const string CatalogReadAdmin = "CatalogReadAdmin";
    public const string ConsentRead = "ConsentRead";
    public const string ConsentDecide = "ConsentDecide";
    public const string GrantRevoke = "GrantRevoke";
    public const string GrantVerify = "GrantVerify";
    public const string CatalogArtifactUploader = "CatalogArtifactUploader";
    public const string CallerAttributeRead = "CallerAttributeRead";
    public const string CallerAttributeApprove = "CallerAttributeApprove";
    public const string CallerAttributeSimulate = "CallerAttributeSimulate";
    public const string VirtualFilterRead = "VirtualFilterRead";
    public const string VirtualFilterWrite = "VirtualFilterWrite";
    public const string VirtualFilterApprove = "VirtualFilterApprove";
    public const string FilterAdmin = "FilterAdmin";
    public const string VirtualFilterSyncPlan = "VirtualFilterSyncPlan";
    public const string VirtualFilterSyncApply = "VirtualFilterSyncApply";
    public const string EffectiveFilterInspect = "EffectiveFilterInspect";
    public const string ProductAccess = "ProductAccess";
    public const string ProductOwnerRead = "ProductOwnerRead";
    public const string ProductOwnerAdmin = "ProductOwnerAdmin";
    public const string ProductSync = "ProductSync";
    public const string OpenApiPublish = "OpenApiPublish";
    public const string ProfileAdmin = "ProfileAdmin";
    public const string ProfileRead = "ProfileRead";
    public const string SqlEndpointRegister = "SqlEndpointRegister";
    public const string SunsettingRead = "SunsettingRead";
    public const string SunsettingEvaluate = "SunsettingEvaluate";
    public const string ComplianceCertificateRead = "ComplianceCertificateRead";

    /// <summary>
    /// Security review: SCIM provisioning endpoints. Authenticates only with the SCIM secret scheme and requires the
    /// dedicated provisioner role (a SCIM secret holder is not a ClusterAdmin).
    /// </summary>
    public const string Scim = "Scim";
    public const string ScimProvisionerRole = "SCIM_PROVISIONER";

    public static readonly string[] GovernanceAdminRoles = ["GovernanceAdmin"];
    public static readonly string[] ItAdminRoles = ["TenantAdmin"];
    public static readonly string[] AuditReadRoles = ["GovernanceAdmin", "SecurityAuditor"];
    public static readonly string[] ClusterAdminRoles = [];
    public static readonly string[] SchemaPublisherRoles = ["SchemaPublisher", "GovernanceAdmin", "TenantAdmin"];
    public static readonly string[] MetricsScrapeRoles = ["MetricsScraper"];

    public static readonly string[] AdminOnlyRoles = ["GovernanceAdmin", "TenantAdmin"];
    public static readonly string[] McpCallerRoles = ["Consumer", "SchemaPublisher", "SecurityAuditor", "GovernanceAdmin", "TenantAdmin"];
    public static readonly string[] UsageAdminRoles = ["TenantAdmin"];
    public static readonly string[] ScimProvisionerRoles = [ScimProvisionerRole];
    public static readonly string[] CatalogArtifactUploaderRoles = ["CatalogArtifactUploader"];
    public static readonly string[] CallerAttributeReadRoles = ["GovernanceAdmin", "SecurityAuditor", "FilterAdmin", "CallerAttributeApprover"];
    public static readonly string[] CallerAttributeApproveRoles = ["GovernanceAdmin", "CallerAttributeApprover"];
    public static readonly string[] CallerAttributeSimulateRoles = ["GovernanceAdmin", "FilterAdmin"];
    public static readonly string[] VirtualFilterReadRoles = ["FilterAdmin", "FilterSync", "GovernanceAdmin", "SecurityAuditor"];
    public static readonly string[] VirtualFilterWriteRoles = ["FilterAdmin", "GovernanceAdmin"];
    public static readonly string[] VirtualFilterApproveRoles = ["GovernanceAdmin"];
    public static readonly string[] FilterAdminRoles = ["FilterAdmin"];
    public static readonly string[] VirtualFilterSyncPlanRoles = ["FilterSync", "FilterAdmin"];
    public static readonly string[] VirtualFilterSyncApplyRoles = ["FilterSync"];
    public static readonly string[] EffectiveFilterInspectRoles = ["FilterAdmin", "GovernanceAdmin", "SecurityAuditor"];
    public static readonly string[] ProductOwnerReadRoles = ["GovernanceAdmin", "TenantAdmin", "SecurityAuditor", "DataProductAdmin"];
    public static readonly string[] ProductOwnerAdminRoles = ["GovernanceAdmin", "TenantAdmin"];
    public static readonly string[] ProductSyncRoles = ["DataProductSync"];
    public static readonly string[] OpenApiPublishRoles = ["GovernanceAdmin", "SchemaPublisher", "TenantAdmin"];
    public static readonly string[] ProfileAdminRoles = ["TenantAdmin", "GovernanceAdmin"];
    public static readonly string[] ProfileReadRoles = ["TenantAdmin", "GovernanceAdmin", "SecurityAuditor"];
    public static readonly string[] SunsettingReadRoles = ["GovernanceAdmin", "SchemaPublisher", "TenantAdmin"];
    public static readonly string[] SunsettingEvaluateRoles = ["GovernanceAdmin", "SchemaPublisher", "Consumer", "TenantAdmin"];
    public static readonly string[] ComplianceCertificateReadRoles = ["GovernanceAdmin", "TenantAdmin", "SecurityAuditor"];

    /// <summary>
    /// Checks if principal satisfies the strongly-typed GatewayRole requirement,
    /// respecting role hierarchy (e.g. ClusterAdmin implies TenantAdmin/SecurityAuditor)
    /// and tenant-bound role prefixes for non-cluster roles.
    /// </summary>
    public static bool HasRole(ClaimsPrincipal? principal, GatewayRole role, string? tenantId = null)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(principal);
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
        {
            return false;
        }

        bool isCanonicalClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (role == GatewayRole.ClusterAdmin)
        {
            return isCanonicalClusterAdmin;
        }

        if (!TryResolvePrincipalTenantScope(claim, tenantId, isCanonicalClusterAdmin, out var effectiveTenant))
        {
            return false;
        }

        if (GatewayRole.GovernanceAdmin.Implies(role) &&
            GatewayRoleEvaluator.HasTenantBoundRole(principal, nameof(GatewayRole.GovernanceAdmin), effectiveTenant))
        {
            return true;
        }

        return HasEffectiveGatewayRole(principal, role, effectiveTenant);
    }

    /// <summary>
    /// Checks if the request principal and resolved tenant satisfy the strongly-typed GatewayRole requirement.
    /// </summary>
    public static bool HasRequestRole(HttpContext context, GatewayRole role)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryResolveHttpContextRoleScope(context, out var effectiveTenant, out var security))
        {
            return false;
        }

        if (security != null && security.HasRole(role))
        {
            return true;
        }

        return HasRole(context.User, role, effectiveTenant);
    }

    /// <summary>
    /// Checks if principal satisfies any of the strongly-typed GatewayRoles.
    /// </summary>
    public static bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<GatewayRole> roles, string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(roles);
        foreach (var role in roles)
        {
            if (HasRole(principal, role, tenantId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the request principal and resolved tenant satisfy any of the strongly-typed GatewayRoles.
    /// </summary>
    public static bool HasAnyRequestRole(HttpContext context, IReadOnlyList<GatewayRole> roles)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roles);
        if (!TryResolveHttpContextRoleScope(context, out var effectiveTenant, out var security))
        {
            return false;
        }

        foreach (var role in roles)
        {
            if ((security != null && security.HasRole(role)) || HasRole(context.User, role, effectiveTenant))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the principal carries any of the given roles by exact name (unprefixed or scoped to the
    /// resolved tenant claim, excluding wildcard prefixes and ForwardAuth ClusterAdmin escalation).
    /// </summary>
    public static bool HasAnyRole(ClaimsPrincipal? principal, IReadOnlyList<string> roles, string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(roles);
        if (principal?.Identity?.IsAuthenticated != true || roles.Count == 0)
        {
            return false;
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(principal);
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
        {
            return false;
        }

        bool isCanonicalClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (!TryResolvePrincipalTenantScope(claim, tenantId, isCanonicalClusterAdmin, out var effectiveTenant))
        {
            return false;
        }

        return HasAnyExactRoleCore(principal, roles, effectiveTenant);
    }

    /// <summary>
    /// True when the request principal carries any of the given roles by exact name (unprefixed or scoped
    /// to the resolved request tenant, excluding wildcard prefixes and ForwardAuth ClusterAdmin escalation).
    /// </summary>
    public static bool HasAnyRequestRole(HttpContext context, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roles);
        if (roles.Count == 0 || !TryResolveHttpContextRoleScope(context, out var effectiveTenant, out var security))
        {
            return false;
        }

        if (HasAnyExactRoleCore(context.User, roles, effectiveTenant))
        {
            return true;
        }

        if (security == null)
        {
            return false;
        }

        bool isCanonicalClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(context.User);
        foreach (var roleName in roles)
        {
            if (string.Equals(roleName, nameof(GatewayRole.ClusterAdmin), StringComparison.OrdinalIgnoreCase))
            {
                if (isCanonicalClusterAdmin && security.IsClusterAdmin)
                {
                    return true;
                }

                continue;
            }

            if (security.TenantRoles.Contains(roleName) ||
                (!string.IsNullOrEmpty(effectiveTenant) && security.TenantRoles.Contains($"{effectiveTenant}:{roleName}")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fluent extension method registering all named gateway policies and the authenticated-user fallback policy.
    /// </summary>
    public static AuthorizationOptions AddGatewayAuthorizationPolicies(this AuthorizationOptions options)
    {
        Configure(options);
        return options;
    }

    /// <summary>
    /// Registers all named gateway policies and the authenticated-user fallback policy.
    /// </summary>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Endpoints without explicit metadata require an authenticated user.
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        options.AddPolicy(GovernanceAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(AllowsGovernanceAdmin));
        options.AddPolicy(ItAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(AllowsItAdmin));
        options.AddPolicy(AuditRead, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(AllowsAuditRead));
        options.AddPolicy(ClusterAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ClusterAdminPolicy.IsCanonicalClusterAdmin(ctx.User)));
        AddSecurityRolePolicy(options, SchemaPublisher, [GatewayRole.SchemaPublisher]);
        options.AddPolicy(MetricsScrape, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ClusterAdminPolicy.IsCanonicalClusterAdmin(ctx.User) || HasAnyRole(ctx.User, MetricsScrapeRoles)));

        options.AddPolicy(AdminOnly, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => AllowsGovernanceAdmin(ctx) || AllowsItAdmin(ctx)));
        options.AddPolicy(MetricsScraper, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ClusterAdminPolicy.IsCanonicalClusterAdmin(ctx.User) || HasAnyRole(ctx.User, MetricsScrapeRoles)));
        options.AddPolicy(UsageAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(AllowsItAdmin));
        options.AddPolicy(AuthenticatedCaller, policy => policy.RequireAuthenticatedUser());
        AddSecurityRolePolicy(options, McpCaller, [GatewayRole.Consumer]);
        options.AddPolicy(CatalogRead, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ExternalApiPermissions.AllowsCatalogRead(ctx.User)));
        options.AddPolicy(CatalogReadAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ExternalApiPermissions.AllowsCatalogReadAdmin(ctx.User)));
        options.AddPolicy(ConsentRead, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ExternalApiPermissions.AllowsConsentRead(ctx.User)));
        options.AddPolicy(ConsentDecide, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new ExternalSystemRequirement(ExternalSystemOperation.ConsentDecide));
        });
        options.AddPolicy(GrantRevoke, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.Requirements.Add(new ExternalSystemRequirement(ExternalSystemOperation.GrantRevoke));
        });
        options.AddPolicy(GrantVerify, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ExternalApiPermissions.AllowsGrantVerify(ctx.User)));

        options.AddPolicy(CatalogArtifactUploader, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
            {
                var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(ctx.User);
                if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
                {
                    return false;
                }

                var tenantId = claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? claim.Tenant.Value : null;
                return HasAnyRole(ctx.User, [GatewayRole.CatalogArtifactUploader], tenantId);
            }));

        // Security review: previously RequireAuthorization(GatewayAuthSchemes.Scim) referenced a non-existent policy.
        // Only an identity issued by the SCIM scheme carrying the provisioner role is accepted, so a user token with a
        // forged or mapped "SCIM_PROVISIONER" role from another scheme does not satisfy it.
        // Furthermore, the authenticated realm must match the route {realmId}.
        options.AddPolicy(Scim, policy => policy
            .AddAuthenticationSchemes(GatewayAuthSchemes.Scim)
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => IsScimProvisioner(ctx.User))
            .RequireAssertion(MatchesRouteRealm));

        AddSecurityRolePolicy(options, CallerAttributeRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor, GatewayRole.FilterAdmin, GatewayRole.CallerAttributeApprover]);
        AddSecurityRolePolicy(options, CallerAttributeApprove,
            [GatewayRole.GovernanceAdmin, GatewayRole.CallerAttributeApprover]);
        AddSecurityRolePolicy(options, CallerAttributeSimulate,
            [GatewayRole.GovernanceAdmin, GatewayRole.FilterAdmin]);

        AddSecurityRolePolicy(options, VirtualFilterRead,
            [GatewayRole.FilterAdmin, GatewayRole.FilterSync, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor]);
        AddSecurityRolePolicy(options, VirtualFilterWrite,
            [GatewayRole.FilterAdmin, GatewayRole.GovernanceAdmin]);
        AddSecurityRolePolicy(options, VirtualFilterApprove,
            [GatewayRole.GovernanceAdmin]);
        AddSecurityRolePolicy(options, FilterAdmin,
            [GatewayRole.FilterAdmin]);
        AddSecurityRolePolicy(options, VirtualFilterSyncPlan,
            [GatewayRole.FilterSync, GatewayRole.FilterAdmin]);
        AddSecurityRolePolicy(options, VirtualFilterSyncApply,
            [GatewayRole.FilterSync]);
        AddSecurityRolePolicy(options, EffectiveFilterInspect,
            [GatewayRole.FilterAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor]);

        options.AddPolicy(ProductAccess, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(AllowsProductAccess));
        AddSecurityRolePolicy(options, ProductOwnerRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor, GatewayRole.DataProductAdmin]);
        AddSecurityRolePolicy(options, ProductOwnerAdmin,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin]);
        AddSecurityRolePolicy(options, ProductSync,
            [GatewayRole.DataProductSync]);

        AddSecurityRolePolicy(options, OpenApiPublish,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.TenantAdmin]);
        AddSecurityRolePolicy(options, ProfileAdmin,
            [GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin]);
        AddSecurityRolePolicy(options, ProfileRead,
            [GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor]);
        AddSecurityRolePolicy(options, SunsettingRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.TenantAdmin]);
        AddSecurityRolePolicy(options, SunsettingEvaluate,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.Consumer, GatewayRole.TenantAdmin]);
        AddSecurityRolePolicy(options, ComplianceCertificateRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor]);

        options.AddPolicy(SqlEndpointRegister, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => EndpointSecurity.IsCanonicalClusterAdmin(ctx.User) && !ctx.User.IsReadOnly()));
    }

    /// <summary>True when the principal has an authenticated SCIM-scheme identity with the provisioner role and no other roles.</summary>
    public static bool IsScimProvisioner(ClaimsPrincipal? principal) =>
        principal?.Identities.Any(i =>
            i.IsAuthenticated &&
            string.Equals(i.AuthenticationType, GatewayAuthSchemes.Scim, StringComparison.Ordinal) &&
            i.HasClaim(i.RoleClaimType, ScimProvisionerRole) &&
            !i.Claims.Any(c => c.Type == i.RoleClaimType && !string.Equals(c.Value, ScimProvisionerRole, StringComparison.Ordinal))) == true;

    /// <summary>
    /// Defense-in-depth: enforces that the identity's autheris:realm claim matches the route realmId when invoked in an HTTP context.
    /// </summary>
    internal static bool MatchesRouteRealm(AuthorizationHandlerContext ctx)
    {
        if (ctx.Resource is HttpContext httpContext &&
            httpContext.Request.RouteValues.TryGetValue("realmId", out var routeRealmObj) &&
            routeRealmObj?.ToString() is { Length: > 0 } routeRealm)
        {
            var tokenRealm = ctx.User.FindFirst("autheris:realm")?.Value;
            return string.Equals(routeRealm, tokenRealm, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    /// <summary>Roles that satisfy a named role policy (used for diagnostics only).</summary>
    public static bool TryGetPolicyRoles(string policyName, out string[] roles)
    {
        roles = policyName switch
        {
            GovernanceAdmin => GovernanceAdminRoles,
            ItAdmin => ItAdminRoles,
            AuditRead => AuditReadRoles,
            ClusterAdmin => ClusterAdminRoles,
            SchemaPublisher => SchemaPublisherRoles,
            MetricsScrape => MetricsScrapeRoles,
            MetricsScraper => MetricsScrapeRoles,
            AdminOnly => AdminOnlyRoles,
            McpCaller => McpCallerRoles,
            UsageAdmin => UsageAdminRoles,
            Scim => ScimProvisionerRoles,
            CatalogArtifactUploader => CatalogArtifactUploaderRoles,
            CallerAttributeRead => CallerAttributeReadRoles,
            CallerAttributeApprove => CallerAttributeApproveRoles,
            CallerAttributeSimulate => CallerAttributeSimulateRoles,
            VirtualFilterRead => VirtualFilterReadRoles,
            VirtualFilterWrite => VirtualFilterWriteRoles,
            VirtualFilterApprove => VirtualFilterApproveRoles,
            FilterAdmin => FilterAdminRoles,
            VirtualFilterSyncPlan => VirtualFilterSyncPlanRoles,
            VirtualFilterSyncApply => VirtualFilterSyncApplyRoles,
            EffectiveFilterInspect => EffectiveFilterInspectRoles,
            ProductOwnerRead => ProductOwnerReadRoles,
            ProductOwnerAdmin => ProductOwnerAdminRoles,
            ProductSync => ProductSyncRoles,
            OpenApiPublish => OpenApiPublishRoles,
            ProfileAdmin => ProfileAdminRoles,
            ProfileRead => ProfileReadRoles,
            SqlEndpointRegister => ClusterAdminRoles,
            SunsettingRead => SunsettingReadRoles,
            SunsettingEvaluate => SunsettingEvaluateRoles,
            ComplianceCertificateRead => ComplianceCertificateReadRoles,
            _ => []
        };
        return roles.Length > 0;
    }

    private static void AddSecurityRolePolicy(
        AuthorizationOptions options,
        string name,
        GatewayRole[] gatewayRoles)
    {
        options.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => HasSecurityRole(ctx, gatewayRoles)));
    }

    private static bool TryGetSecurityContext(
        HttpContext httpContext,
        [NotNullWhen(true)] out SecurityPrincipalContext? security)
    {
        try
        {
            security = EndpointSecurity.GetSecurityContext(httpContext);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            security = null;
            return false;
        }
    }

    private static bool AllowsGovernanceAdmin(AuthorizationHandlerContext ctx)
        => HasTenantBoundRole(ctx, nameof(GatewayRole.GovernanceAdmin));

    private static bool AllowsItAdmin(AuthorizationHandlerContext ctx)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (ctx.Resource is HttpContext httpContext && !TryGetSecurityContext(httpContext, out _))
        {
            return false;
        }

        return ClusterAdminPolicy.IsCanonicalClusterAdmin(ctx.User)
            || HasTenantBoundRole(ctx, nameof(GatewayRole.TenantAdmin));
    }

    private static bool AllowsAuditRead(AuthorizationHandlerContext ctx)
        => AllowsGovernanceAdmin(ctx)
            || HasSecurityRole(ctx, [GatewayRole.SecurityAuditor]);

    private static bool HasTenantBoundRole(AuthorizationHandlerContext ctx, string roleName)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(ctx.User);
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
        {
            return false;
        }

        if (ctx.Resource is HttpContext httpContext)
        {
            if (!TryGetSecurityContext(httpContext, out var security) || security.TenantClaimInvalid)
            {
                return false;
            }

            var resolvedTenant = security.ResolvedTenant?.Value;
            if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid &&
                !string.IsNullOrEmpty(resolvedTenant) &&
                !string.Equals(claim.Tenant.Value, resolvedTenant, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var scopedRole = string.IsNullOrEmpty(resolvedTenant) ? null : $"{resolvedTenant}:{roleName}";
            return security.TenantRoles.Contains(roleName) ||
                   (scopedRole != null && security.TenantRoles.Contains(scopedRole));
        }

        return GatewayRoleEvaluator.HasTenantBoundRole(ctx.User, roleName);
    }

    internal static bool HasSecurityRole(
        AuthorizationHandlerContext ctx,
        GatewayRole[] gatewayRoles)
    {
        foreach (var required in gatewayRoles)
        {
            if (GatewayRole.GovernanceAdmin.Implies(required) && AllowsGovernanceAdmin(ctx))
            {
                return true;
            }
        }

        if (ctx.Resource is HttpContext httpContext)
        {
            if (!TryGetSecurityContext(httpContext, out var security) || security.TenantClaimInvalid)
            {
                return false;
            }

            if (security.HasAnyRole(gatewayRoles))
            {
                return true;
            }

            var resolvedTenant = security.ResolvedTenant?.Value;
            return !string.IsNullOrEmpty(resolvedTenant) &&
                   HasAnyRole(ctx.User, gatewayRoles, resolvedTenant);
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(ctx.User);
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
        {
            return false;
        }

        var claimTenant = claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? claim.Tenant.Value : null;
        if (HasAnyRole(ctx.User, gatewayRoles, claimTenant))
        {
            return true;
        }

        // Tenant-scoped-only roles are excluded from Evaluator.GetEffectiveRoles global branch; match unprefixed or claim-tenant-prefixed explicitly (no '*' wildcard).
        foreach (var role in gatewayRoles)
        {
            if (role.IsTenantScopedOnly() && HasTenantBoundRole(ctx, role.ToString()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllowsProductAccess(AuthorizationHandlerContext ctx)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (ctx.Resource is HttpContext httpContext)
        {
            if (!TryGetSecurityContext(httpContext, out var security))
            {
                return false;
            }

            bool hasSync = security.HasRole(GatewayRole.DataProductSync) || Evaluator.HasExactRole(ctx.User, nameof(GatewayRole.DataProductSync), security.ResolvedTenant?.Value);
            bool hasAdmin = security.HasRole(GatewayRole.DataProductAdmin)
                || Evaluator.HasExactRole(ctx.User, nameof(GatewayRole.DataProductAdmin), security.ResolvedTenant?.Value)
                || EndpointSecurity.IsCanonicalClusterAdmin(ctx.User);
            return !hasSync || hasAdmin;
        }

        bool sync = Evaluator.HasExactRole(ctx.User, nameof(GatewayRole.DataProductSync));
        bool admin = Evaluator.HasExactRole(ctx.User, nameof(GatewayRole.DataProductAdmin)) || EndpointSecurity.IsCanonicalClusterAdmin(ctx.User);
        return !sync || admin;
    }

    private static bool IsForwardAuthPrincipal(ClaimsPrincipal principal) =>
        principal.Identities.Any(i =>
            i.IsAuthenticated &&
            string.Equals(i.AuthenticationType, ClusterAdminPolicy.ForwardAuthScheme, StringComparison.OrdinalIgnoreCase));

    private static bool TryResolvePrincipalTenantScope(
        Autheris.Domain.Tenancy.ClaimTenant claim,
        string? explicitTenantId,
        bool isCanonicalClusterAdmin,
        out string? effectiveTenant)
    {
        var trimmedTenant = string.IsNullOrWhiteSpace(explicitTenantId) ? null : explicitTenantId.Trim();
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid)
        {
            if (trimmedTenant != null &&
                !string.Equals(claim.Tenant.Value, trimmedTenant, StringComparison.OrdinalIgnoreCase) &&
                !isCanonicalClusterAdmin)
            {
                effectiveTenant = null;
                return false;
            }

            effectiveTenant = trimmedTenant ?? claim.Tenant.Value;
            return true;
        }

        effectiveTenant = trimmedTenant;
        return true;
    }

    private static bool TryResolveHttpContextRoleScope(
        HttpContext context,
        out string? effectiveTenant,
        out SecurityPrincipalContext? security)
    {
        security = null;
        effectiveTenant = null;

        var principal = context.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(principal);
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
        {
            return false;
        }

        bool isCanonicalClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (context.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var secObj) && secObj is SecurityPrincipalContext sec)
        {
            if (sec.TenantClaimInvalid)
            {
                return false;
            }

            var resolved = sec.ResolvedTenant?.Value;
            if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid &&
                !string.IsNullOrEmpty(resolved) &&
                !string.Equals(claim.Tenant.Value, resolved, StringComparison.OrdinalIgnoreCase) &&
                !isCanonicalClusterAdmin)
            {
                return false;
            }

            security = sec;
            effectiveTenant = !string.IsNullOrEmpty(resolved)
                ? resolved
                : (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? claim.Tenant.Value : null);
            return true;
        }

        if (context.Items.TryGetValue(SecurityPrincipalContext.TenantIdItemKey, out var itemTenantObj) && itemTenantObj is TenantId itemTenant)
        {
            if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid &&
                !string.Equals(claim.Tenant.Value, itemTenant.Value, StringComparison.OrdinalIgnoreCase) &&
                !isCanonicalClusterAdmin)
            {
                return false;
            }

            effectiveTenant = itemTenant.Value;
            return true;
        }

        if (context.Request.Headers.TryGetValue(SecurityContextFactory.TenantHeaderName, out var hv) && !string.IsNullOrWhiteSpace(hv))
        {
            var headerTenant = hv.ToString().Trim();
            if (!isCanonicalClusterAdmin &&
                (claim.Kind != Autheris.Domain.Tenancy.ClaimTenantKind.Valid ||
                 !string.Equals(claim.Tenant.Value, headerTenant, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            effectiveTenant = headerTenant;
            return true;
        }

        effectiveTenant = claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? claim.Tenant.Value : null;
        return true;
    }

    private static bool HasEffectiveGatewayRole(ClaimsPrincipal principal, GatewayRole required, string? effectiveTenant)
    {
        bool isForwardAuth = IsForwardAuthPrincipal(principal);
        foreach (var granted in Evaluator.GetEffectiveRoles(principal, effectiveTenant))
        {
            if (isForwardAuth && granted == GatewayRole.ClusterAdmin)
            {
                continue;
            }

            if (granted.Implies(required))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnyExactRoleCore(ClaimsPrincipal principal, IReadOnlyList<string> roles, string? effectiveTenant)
    {
        bool isCanonicalClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        foreach (var raw in principal.GetUserRoles())
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (!trimmed.Contains(':', StringComparison.Ordinal))
            {
                if (string.Equals(trimmed, nameof(GatewayRole.ClusterAdmin), StringComparison.OrdinalIgnoreCase) &&
                    !isCanonicalClusterAdmin)
                {
                    continue;
                }

                foreach (var roleName in roles)
                {
                    if (string.Equals(trimmed, roleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                continue;
            }

            if (string.IsNullOrEmpty(effectiveTenant))
            {
                continue;
            }

            var firstColon = trimmed.IndexOf(':', StringComparison.Ordinal);
            var lastColon = trimmed.LastIndexOf(':', StringComparison.Ordinal);
            if (firstColon <= 0 || firstColon != lastColon || firstColon == trimmed.Length - 1)
            {
                continue;
            }

            var roleTenant = trimmed[..firstColon];
            var parsedRoleName = trimmed[(firstColon + 1)..];
            if (roleTenant == "*" ||
                !string.Equals(roleTenant, effectiveTenant, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parsedRoleName, nameof(GatewayRole.ClusterAdmin), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var roleName in roles)
            {
                if (string.Equals(parsedRoleName, roleName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        foreach (var roleName in roles)
        {
            if (string.Equals(roleName, nameof(GatewayRole.ClusterAdmin), StringComparison.OrdinalIgnoreCase) &&
                !isCanonicalClusterAdmin)
            {
                continue;
            }

            if (principal.IsInRole(roleName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fluent route helper to enforce strongly-typed GatewayRole requirements on Minimal API endpoints.
    /// </summary>
    public static RouteHandlerBuilder RequireGatewayRole(this RouteHandlerBuilder builder, params GatewayRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(roles);

        return builder.RequireAuthorization(policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => ctx.Resource is HttpContext httpContext
                ? HasAnyRequestRole(httpContext, roles)
                : HasAnyRole(ctx.User, roles)));
    }
}
