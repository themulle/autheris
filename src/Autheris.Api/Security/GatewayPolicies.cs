namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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

    /// <summary>SEC2-09: Prometheus scrape endpoint; any authenticated user is not enough.</summary>
    public const string MetricsScrape = "MetricsScrape";

    // Declarative endpoint authorization policies
    public const string AdminOnly = "AdminOnly";
    public const string OperatorOrAbove = "OperatorOrAbove";
    public const string AuditorOrAbove = "AuditorOrAbove";
    public const string DataStewardOrAdmin = "DataStewardOrAdmin";
    public const string AuthenticatedCaller = "AuthenticatedCaller";
    public const string MetricsScraper = "MetricsScraper";
    public const string McpCaller = "McpCaller";
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
    public const string GovernanceAuditRead = "GovernanceAuditRead";
    public const string SunsettingRead = "SunsettingRead";
    public const string SunsettingEvaluate = "SunsettingEvaluate";
    public const string ComplianceCertificateRead = "ComplianceCertificateRead";

    /// <summary>
    /// Security review: SCIM provisioning endpoints. Authenticates only with the SCIM secret scheme and requires the
    /// dedicated provisioner role (a SCIM secret holder is not a ClusterAdmin).
    /// </summary>
    public const string Scim = "Scim";
    public const string ScimProvisionerRole = "SCIM_PROVISIONER";

    public static readonly string[] GovernanceAdminRoles = ["GovernanceAdmin", "ClusterAdmin"];
    public static readonly string[] ApproverRoles = ["DataSteward", "DataOwner", "GovernanceAdmin", "ClusterAdmin"];
    public static readonly string[] ClusterAdminRoles = ["ClusterAdmin"];
    public static readonly string[] PrivacyAdminRoles = ["GovernanceAdmin", "PrivacyAdmin", "DataProtectionOfficer", "ClusterAdmin"];
    public static readonly string[] SchemaAdminRoles = ["GovernanceAdmin", "SchemaAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin"];
    public static readonly string[] SchemaPublisherRoles = ["GovernanceAdmin", "SchemaAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin", "Developer", "DataOwner"];
    public static readonly string[] MetricsScrapeRoles = ["MetricsScraper", "ClusterAdmin"];

    public static readonly string[] AdminOnlyRoles = ["GovernanceAdmin", "TenantAdmin", "SecurityAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin"];
    public static readonly string[] OperatorOrAboveRoles = ["Operator", "DataSteward", "DataOwner", "SchemaAdmin", "GovernanceAdmin", "TenantAdmin", "SecurityAdmin", "GatewayAdmin", "PlatformAdmin", "ClusterAdmin"];
    public static readonly string[] AuditorOrAboveRoles = ["SecurityAuditor", "Auditor", "PrivacyAdmin", "DataProtectionOfficer", "GovernanceAdmin", "TenantAdmin", "SecurityAdmin", "ClusterAdmin"];
    public static readonly string[] DataStewardOrAdminRoles = ["DataSteward", "DataOwner", "GovernanceAdmin", "TenantAdmin", "ClusterAdmin"];
    public static readonly string[] McpCallerRoles = ["AiAgent", "McpCaller", "Developer", "Consumer", "DataSteward", "DataOwner", "GovernanceAdmin", "TenantAdmin", "ClusterAdmin"];
    public static readonly string[] UsageAdminRoles = ["TenantAdmin", "ClusterAdmin"];
    public static readonly string[] ScimProvisionerRoles = [ScimProvisionerRole];
    public static readonly string[] CatalogArtifactUploaderRoles = ["CatalogArtifactUploader", "GovernanceAdmin", "ClusterAdmin"];
    public static readonly string[] CallerAttributeReadRoles = ["GovernanceAdmin", "SecurityAuditor", "FilterAdmin", "CallerAttributeApprover", "ClusterAdmin"];
    public static readonly string[] CallerAttributeApproveRoles = ["GovernanceAdmin", "CallerAttributeApprover", "ClusterAdmin"];
    public static readonly string[] CallerAttributeSimulateRoles = ["GovernanceAdmin", "FilterAdmin", "ClusterAdmin"];
    public static readonly string[] VirtualFilterReadRoles = ["FilterAdmin", "FilterSync", "GovernanceAdmin", "SecurityAuditor", "ClusterAdmin"];
    public static readonly string[] VirtualFilterWriteRoles = ["FilterAdmin", "GovernanceAdmin", "ClusterAdmin"];
    public static readonly string[] VirtualFilterApproveRoles = ["GovernanceAdmin", "DataOwner", "ClusterAdmin"];
    public static readonly string[] FilterAdminRoles = ["FilterAdmin", "ClusterAdmin"];
    public static readonly string[] VirtualFilterSyncPlanRoles = ["FilterSync", "FilterAdmin", "ClusterAdmin"];
    public static readonly string[] VirtualFilterSyncApplyRoles = ["FilterSync", "ClusterAdmin"];
    public static readonly string[] EffectiveFilterInspectRoles = ["FilterAdmin", "GovernanceAdmin", "SecurityAuditor", "ClusterAdmin"];
    public static readonly string[] ProductOwnerReadRoles = ["GovernanceAdmin", "TenantAdmin", "SecurityAuditor", "DataProductAdmin", "ClusterAdmin"];
    public static readonly string[] ProductOwnerAdminRoles = ["GovernanceAdmin", "TenantAdmin", "ClusterAdmin"];
    public static readonly string[] ProductSyncRoles = ["DataProductSync", "ClusterAdmin"];
    public static readonly string[] OpenApiPublishRoles = ["GovernanceAdmin", "SchemaPublisher", "TenantAdmin", "ClusterAdmin"];
    public static readonly string[] ProfileAdminRoles = ["ClusterAdmin", "TenantAdmin", "GovernanceAdmin", "SecurityAdmin"];
    public static readonly string[] ProfileReadRoles = ["ClusterAdmin", "TenantAdmin", "GovernanceAdmin", "SecurityAuditor", "SecurityAdmin"];
    public static readonly string[] GovernanceAuditReadRoles = ["PrivacyAdmin", "GovernanceAdmin", "TenantAdmin", "SecurityAuditor", "DataOwner", "ClusterAdmin"];
    public static readonly string[] SunsettingReadRoles = ["GovernanceAdmin", "SchemaPublisher", "DataOwner", "TenantAdmin", "ClusterAdmin"];
    public static readonly string[] SunsettingEvaluateRoles = ["GovernanceAdmin", "SchemaPublisher", "DataOwner", "Consumer", "TenantAdmin", "ClusterAdmin"];
    public static readonly string[] ComplianceCertificateReadRoles = ["GovernanceAdmin", "TenantAdmin", "SecurityAuditor", "ClusterAdmin"];

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
        AddRolePolicy(options, MetricsScrape, MetricsScrapeRoles);

        // 06 policies
        AddRolePolicy(options, AdminOnly, AdminOnlyRoles);
        AddRolePolicy(options, OperatorOrAbove, OperatorOrAboveRoles);
        AddRolePolicy(options, AuditorOrAbove, AuditorOrAboveRoles);
        AddRolePolicy(options, DataStewardOrAdmin, DataStewardOrAdminRoles);
        AddRolePolicy(options, MetricsScraper, MetricsScrapeRoles);
        AddRolePolicy(options, UsageAdmin, UsageAdminRoles);
        options.AddPolicy(AuthenticatedCaller, policy => policy.RequireAuthenticatedUser());
        AddRolePolicy(options, McpCaller, McpCallerRoles);
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
                return Evaluator.HasRole(ctx.User, GatewayRole.CatalogArtifactUploader, tenantId);
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
            [GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor, GatewayRole.FilterAdmin, GatewayRole.CallerAttributeApprover, GatewayRole.ClusterAdmin],
            CallerAttributeReadRoles);
        AddSecurityRolePolicy(options, CallerAttributeApprove,
            [GatewayRole.GovernanceAdmin, GatewayRole.CallerAttributeApprover, GatewayRole.ClusterAdmin],
            CallerAttributeApproveRoles);
        AddSecurityRolePolicy(options, CallerAttributeSimulate,
            [GatewayRole.GovernanceAdmin, GatewayRole.FilterAdmin, GatewayRole.ClusterAdmin],
            CallerAttributeSimulateRoles);

        AddSecurityRolePolicy(options, VirtualFilterRead,
            [GatewayRole.FilterAdmin, GatewayRole.FilterSync, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor, GatewayRole.ClusterAdmin],
            VirtualFilterReadRoles);
        AddSecurityRolePolicy(options, VirtualFilterWrite,
            [GatewayRole.FilterAdmin, GatewayRole.GovernanceAdmin, GatewayRole.ClusterAdmin],
            VirtualFilterWriteRoles);
        AddSecurityRolePolicy(options, VirtualFilterApprove,
            [GatewayRole.GovernanceAdmin, GatewayRole.DataOwner, GatewayRole.ClusterAdmin],
            VirtualFilterApproveRoles);
        AddSecurityRolePolicy(options, FilterAdmin,
            [GatewayRole.FilterAdmin, GatewayRole.ClusterAdmin],
            FilterAdminRoles);
        AddSecurityRolePolicy(options, VirtualFilterSyncPlan,
            [GatewayRole.FilterSync, GatewayRole.FilterAdmin, GatewayRole.ClusterAdmin],
            VirtualFilterSyncPlanRoles);
        AddSecurityRolePolicy(options, VirtualFilterSyncApply,
            [GatewayRole.FilterSync, GatewayRole.ClusterAdmin],
            VirtualFilterSyncApplyRoles);
        AddSecurityRolePolicy(options, EffectiveFilterInspect,
            [GatewayRole.FilterAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor, GatewayRole.ClusterAdmin],
            EffectiveFilterInspectRoles);

        options.AddPolicy(ProductAccess, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(AllowsProductAccess));
        AddSecurityRolePolicy(options, ProductOwnerRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor, GatewayRole.DataProductAdmin, GatewayRole.ClusterAdmin],
            ProductOwnerReadRoles);
        AddSecurityRolePolicy(options, ProductOwnerAdmin,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.ClusterAdmin],
            ProductOwnerAdminRoles);
        AddSecurityRolePolicy(options, ProductSync,
            [GatewayRole.DataProductSync, GatewayRole.ClusterAdmin],
            ProductSyncRoles);

        AddSecurityRolePolicy(options, OpenApiPublish,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.ClusterAdmin],
            OpenApiPublishRoles);
        AddSecurityRolePolicy(options, ProfileAdmin,
            [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin],
            ProfileAdminRoles);
        AddSecurityRolePolicy(options, ProfileRead,
            [GatewayRole.ClusterAdmin, GatewayRole.TenantAdmin, GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor],
            ProfileReadRoles);
        AddSecurityRolePolicy(options, GovernanceAuditRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor, GatewayRole.DataOwner, GatewayRole.ClusterAdmin],
            GovernanceAuditReadRoles);
        AddSecurityRolePolicy(options, SunsettingRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.DataOwner, GatewayRole.ClusterAdmin],
            SunsettingReadRoles);
        AddSecurityRolePolicy(options, SunsettingEvaluate,
            [GatewayRole.GovernanceAdmin, GatewayRole.SchemaPublisher, GatewayRole.DataOwner, GatewayRole.Consumer, GatewayRole.ClusterAdmin],
            SunsettingEvaluateRoles);
        AddSecurityRolePolicy(options, ComplianceCertificateRead,
            [GatewayRole.GovernanceAdmin, GatewayRole.TenantAdmin, GatewayRole.SecurityAuditor, GatewayRole.ClusterAdmin],
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
            Approver => ApproverRoles,
            ClusterAdmin => ClusterAdminRoles,
            PrivacyAdmin => PrivacyAdminRoles,
            SchemaAdmin => SchemaAdminRoles,
            SchemaPublisher => SchemaPublisherRoles,
            MetricsScrape => MetricsScrapeRoles,
            MetricsScraper => MetricsScrapeRoles,
            AdminOnly => AdminOnlyRoles,
            OperatorOrAbove => OperatorOrAboveRoles,
            AuditorOrAbove => AuditorOrAboveRoles,
            DataStewardOrAdmin => DataStewardOrAdminRoles,
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
            GovernanceAuditRead => GovernanceAuditReadRoles,
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

    internal static bool HasSecurityRole(
        AuthorizationHandlerContext ctx,
        GatewayRole[] gatewayRoles,
        string[] fallbackRoleNames)
    {
        if (ctx.Resource is HttpContext httpContext)
        {
            SecurityPrincipalContext security;
            try
            {
                security = EndpointSecurity.GetSecurityContext(httpContext);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }

            return security.HasAnyRole(gatewayRoles) || HasAnyFallbackRole(ctx.User, fallbackRoleNames);
        }

        return HasAnyGatewayRolesCanonical(ctx.User, gatewayRoles) || HasAnyFallbackRole(ctx.User, fallbackRoleNames);
    }

    private static bool AllowsProductAccess(AuthorizationHandlerContext ctx)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (ctx.Resource is HttpContext httpContext)
        {
            SecurityPrincipalContext security;
            try
            {
                security = EndpointSecurity.GetSecurityContext(httpContext);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }

            bool hasSync = security.HasRole(GatewayRole.DataProductSync) || HasAnyFallbackRole(ctx.User, ["DataProductSync"]);
            bool hasAdmin = security.HasRole(GatewayRole.DataProductAdmin)
                || HasAnyFallbackRole(ctx.User, ["DataProductAdmin"])
                || security.IsClusterAdmin
                || EndpointSecurity.IsCanonicalClusterAdmin(ctx.User);
            return !hasSync || hasAdmin;
        }

        bool sync = HasAnyFallbackRole(ctx.User, ["DataProductSync"]);
        bool admin = HasAnyFallbackRole(ctx.User, ["DataProductAdmin"]) || EndpointSecurity.IsCanonicalClusterAdmin(ctx.User);
        return !sync || admin;
    }

    private static bool HasAnyGatewayRolesCanonical(ClaimsPrincipal? principal, GatewayRole[] gatewayRoles)
    {
        if (principal?.Identity?.IsAuthenticated != true || gatewayRoles.Length == 0)
        {
            return false;
        }

        if (!principal.Identities.Any(i =>
                i.IsAuthenticated &&
                string.Equals(i.AuthenticationType, ClusterAdminPolicy.ForwardAuthScheme, StringComparison.OrdinalIgnoreCase)))
        {
            return HasAnyRole(principal, gatewayRoles);
        }

        // Non-canonical ForwardAuth identities must never satisfy roles via an implied ClusterAdmin claim.
        var effective = Evaluator.GetEffectiveRoles(principal);
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

    private static bool HasAnyFallbackRole(ClaimsPrincipal? principal, string[] fallbackRoleNames)
    {
        if (fallbackRoleNames.Length == 0 || principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        foreach (var roleName in fallbackRoleNames)
        {
            if (string.Equals(roleName, ClusterAdmin, StringComparison.OrdinalIgnoreCase))
            {
                if (ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
                {
                    return true;
                }
            }
            else if (Evaluator.HasExactRole(principal, roleName))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddRolePolicy(AuthorizationOptions options, string name, string[] roles)
    {
        options.AddPolicy(name, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => string.Equals(name, ClusterAdmin, StringComparison.Ordinal)
                ? ClusterAdminPolicy.IsCanonicalClusterAdmin(ctx.User)
                : HasAnyRole(ctx.User, roles)));
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
