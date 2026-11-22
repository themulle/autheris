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
    /// K-K10: Checks if principal satisfies any of the strongly-typed GatewayRole requirements,
    /// respecting role hierarchy (e.g. ClusterAdmin implies GovernanceAdmin/DataSteward).
    /// </summary>
    public static bool HasRole(ClaimsPrincipal? principal, GatewayRole role, string? tenantId = null)
        => role == GatewayRole.ClusterAdmin && string.IsNullOrEmpty(tenantId)
            ? ClusterAdminPolicy.IsCanonicalClusterAdmin(principal)
            : Evaluator.HasRole(principal, role, tenantId);

    /// <summary>
    /// K-K10: Checks if principal satisfies any of the strongly-typed GatewayRoles.
    /// </summary>
    public static bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<GatewayRole> roles, string? tenantId = null)
        => Evaluator.HasAnyRole(principal, roles, tenantId);

    /// <summary>
    /// True when the principal carries any of the given roles, by exact name via
    /// <see cref="ClaimsPrincipal.IsInRole"/>, or raw "role"/"roles" claims.
    /// </summary>
    public static bool HasAnyRole(ClaimsPrincipal? principal, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return Evaluator.HasAnyExactRole(principal, roles);
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
        AddSecurityRolePolicy(options, SchemaPublisher, [GatewayRole.SchemaPublisher], SchemaPublisherRoles);
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
        AddSecurityRolePolicy(options, McpCaller, [GatewayRole.Consumer], McpCallerRoles);
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
                return HasAnyGatewayRolesCanonical(ctx.User, [GatewayRole.CatalogArtifactUploader], tenantId);
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
            [GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor, GatewayRole.FilterAdmin, GatewayRole.CallerAttributeApprover],
            CallerAttributeReadRoles);
        AddSecurityRolePolicy(options, CallerAttributeApprove,
            [GatewayRole.GovernanceAdmin, GatewayRole.CallerAttributeApprover],
            CallerAttributeApproveRoles);
        AddSecurityRolePolicy(options, CallerAttributeSimulate,
            [GatewayRole.GovernanceAdmin, GatewayRole.FilterAdmin],
            CallerAttributeSimulateRoles);

        AddSecurityRolePolicy(options, VirtualFilterRead,
            [GatewayRole.FilterAdmin, GatewayRole.FilterSync, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor],
            VirtualFilterReadRoles);
        AddSecurityRolePolicy(options, VirtualFilterWrite,
            [GatewayRole.FilterAdmin, GatewayRole.GovernanceAdmin],
            VirtualFilterWriteRoles);
        AddSecurityRolePolicy(options, VirtualFilterApprove,
            [GatewayRole.GovernanceAdmin],
            VirtualFilterApproveRoles);
        AddSecurityRolePolicy(options, FilterAdmin,
            [GatewayRole.FilterAdmin],
            FilterAdminRoles);
        AddSecurityRolePolicy(options, VirtualFilterSyncPlan,
            [GatewayRole.FilterSync, GatewayRole.FilterAdmin],
            VirtualFilterSyncPlanRoles);
        AddSecurityRolePolicy(options, VirtualFilterSyncApply,
            [GatewayRole.FilterSync],
            VirtualFilterSyncApplyRoles);
        AddSecurityRolePolicy(options, EffectiveFilterInspect,
            [GatewayRole.FilterAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor],
            EffectiveFilterInspectRoles);

        options.AddPolicy(ProductAccess, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(AllowsProductAccess));
        AddSecurityRolePolicy(options, ProductOwnerRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor, GatewayRole.DataProductAdmin],
            ProductOwnerReadRoles);
        AddSecurityRolePolicy(options, ProductOwnerAdmin,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin],
            ProductOwnerAdminRoles);
        AddSecurityRolePolicy(options, ProductSync,
            [GatewayRole.DataProductSync],
            ProductSyncRoles);

        AddSecurityRolePolicy(options, OpenApiPublish,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.TenantAdmin],
            OpenApiPublishRoles);
        AddSecurityRolePolicy(options, ProfileAdmin,
            [GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin],
            ProfileAdminRoles);
        AddSecurityRolePolicy(options, ProfileRead,
            [GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor],
            ProfileReadRoles);
        AddSecurityRolePolicy(options, SunsettingRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.TenantAdmin],
            SunsettingReadRoles);
        AddSecurityRolePolicy(options, SunsettingEvaluate,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.Consumer, GatewayRole.TenantAdmin],
            SunsettingEvaluateRoles);
        AddSecurityRolePolicy(options, ComplianceCertificateRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor],
            ComplianceCertificateReadRoles);

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
        GatewayRole[] gatewayRoles,
        string[] fallbackRoleNames)
    {
        options.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => HasSecurityRole(ctx, gatewayRoles, fallbackRoleNames)));
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
            || HasSecurityRole(ctx, [GatewayRole.SecurityAuditor], AuditReadRoles);

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

        var claimTenant = claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? claim.Tenant.Value : null;
        var claimScopedRole = string.IsNullOrEmpty(claimTenant) ? null : $"{claimTenant}:{roleName}";
        foreach (var raw in ctx.User.GetUserRoles())
        {
            var trimmed = raw.Trim();
            if (string.Equals(trimmed, roleName, StringComparison.OrdinalIgnoreCase) ||
                (claimScopedRole != null && string.Equals(trimmed, claimScopedRole, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasSecurityRole(
        AuthorizationHandlerContext ctx,
        GatewayRole[] gatewayRoles,
        string[] fallbackRoleNames)
    {
        _ = fallbackRoleNames;
        if (Array.IndexOf(gatewayRoles, GatewayRole.GovernanceAdmin) >= 0 && AllowsGovernanceAdmin(ctx))
        {
            return true;
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
                   HasAnyGatewayRolesCanonical(ctx.User, gatewayRoles, resolvedTenant);
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(ctx.User);
        if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid)
        {
            return false;
        }

        var claimTenant = claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? claim.Tenant.Value : null;
        if (HasAnyGatewayRolesCanonical(ctx.User, gatewayRoles, claimTenant))
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

    private static bool HasAnyGatewayRolesCanonical(ClaimsPrincipal? principal, GatewayRole[] gatewayRoles, string? tenantId = null)
    {
        if (principal?.Identity?.IsAuthenticated != true || gatewayRoles.Length == 0)
        {
            return false;
        }

        if (!principal.Identities.Any(i =>
                i.IsAuthenticated &&
                string.Equals(i.AuthenticationType, ClusterAdminPolicy.ForwardAuthScheme, StringComparison.OrdinalIgnoreCase)))
        {
            return HasAnyRole(principal, gatewayRoles, tenantId);
        }

        // Non-canonical ForwardAuth identities must never satisfy roles via an implied ClusterAdmin claim.
        var effective = Evaluator.GetEffectiveRoles(principal, tenantId);
        foreach (var granted in effective)
        {
            if (granted == GatewayRole.ClusterAdmin)
            {
                continue;
            }

            foreach (var required in gatewayRoles)
            {
                if (granted.Implies(required))
                {
                    return true;
                }
            }
        }

        return false;
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
