namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Compliance;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GatewayPoliciesRoleConsolidationTests
{
    public static readonly string[] LegacyRoleNames =
    [
        "DataOwner",
        "DataSteward",
        "Auditor",
        "Analyst",
        "Operator",
        "SchemaAdmin",
        "SecurityAdmin",
        "GatewayAdmin",
        "PlatformAdmin",
        "PrivacyAdmin",
        "DataProtectionOfficer",
        "AiAgent",
        "McpCaller",
        "Developer",
    ];

    private static readonly string[] RemovedPolicyFieldNames =
    [
        "Approver",
        "PrivacyAdmin",
        "SchemaAdmin",
        "OperatorOrAbove",
        "DataStewardOrAdmin",
        "AuditorOrAbove",
        "GovernanceAuditRead",
        "ApproverRoles",
        "PrivacyAdminRoles",
        "SchemaAdminRoles",
        "OperatorOrAboveRoles",
        "DataStewardOrAdminRoles",
        "AuditorOrAboveRoles",
        "GovernanceAuditReadRoles",
    ];

    private static ServiceProvider BuildAuthorizationProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayPolicies.Configure);
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Principal(
        string scheme = "Bearer",
        string? tenantId = null,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-TEST-USER"),
        };
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    private static HttpContext HttpContextWithPrincipal(ClaimsPrincipal principal, string? resolvedTenant)
    {
        var context = new DefaultHttpContext
        {
            User = principal,
        };
        if (!string.IsNullOrWhiteSpace(resolvedTenant))
        {
            context.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId(resolvedTenant);
            context.Items[SecurityPrincipalContext.ItemKey] = SecurityPrincipalContext.FromPrincipal(
                principal,
                overrideTenant: new TenantId(resolvedTenant));
        }

        return context;
    }

    [Fact]
    public async Task ClusterAdmin_only_through_canonical_policy()
    {
        foreach (var removedField in RemovedPolicyFieldNames)
        {
            typeof(GatewayPolicies).GetField(removedField, BindingFlags.Public | BindingFlags.Static)
                .ShouldBeNull($"Legacy GatewayPolicies field '{removedField}' must be removed.");
        }

        var roleArrayFields = typeof(GatewayPolicies)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string[]));

        foreach (var field in roleArrayFields)
        {
            var values = (string[])field.GetValue(null)!;
            values.ShouldNotContain("ClusterAdmin", $"GatewayPolicies.{field.Name} must not contain 'ClusterAdmin' string literal.");
            foreach (var legacy in LegacyRoleNames)
            {
                values.ShouldNotContain(legacy, $"GatewayPolicies.{field.Name} must not contain legacy role '{legacy}'.");
            }
        }

        await using var sp = BuildAuthorizationProvider();
        var authz = sp.GetRequiredService<IAuthorizationService>();

        var canonicalClusterAdmin = Principal(scheme: "Bearer", roles: "ClusterAdmin");
        var forwardAuthClusterAdmin = Principal(scheme: "ForwardAuth", roles: "ClusterAdmin");
        var tenantPrefixedClusterAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:ClusterAdmin");

        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.ItAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.AdminOnly)).Succeeded.ShouldBeTrue();

        // ClusterAdmin does NOT imply GovernanceAdmin, FilterAdmin, CallerAttribute*, or DataProduct*.
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.VirtualFilterWrite)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.VirtualFilterApprove)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.CallerAttributeApprove)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(canonicalClusterAdmin, null, GatewayPolicies.ProductSync)).Succeeded.ShouldBeFalse();

        // Non-canonical ClusterAdmin (ForwardAuth or tenant-prefixed) is denied everywhere ClusterAdmin is gated.
        (await authz.AuthorizeAsync(forwardAuthClusterAdmin, null, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(forwardAuthClusterAdmin, null, GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(tenantPrefixedClusterAdmin, null, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(tenantPrefixedClusterAdmin, null, GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();
    }

    public static IEnumerable<object[]> LegacyRoleCases() =>
        LegacyRoleNames.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(LegacyRoleCases))]
    public async Task Legacy_role_strings_satisfy_nothing(string legacyRole)
    {
        GatewayRoleExtensions.TryParseRole(legacyRole, out _).ShouldBeFalse($"TryParseRole('{legacyRole}') must return false.");

        await using var sp = BuildAuthorizationProvider();
        var authz = sp.GetRequiredService<IAuthorizationService>();

        var principal = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: legacyRole);
        var httpContext = HttpContextWithPrincipal(principal, "tenant-a");

        string[] policiesToDeny =
        [
            GatewayPolicies.ClusterAdmin,
            GatewayPolicies.ItAdmin,
            GatewayPolicies.GovernanceAdmin,
            GatewayPolicies.AdminOnly,
            GatewayPolicies.AuditRead,
            GatewayPolicies.SchemaPublisher,
            GatewayPolicies.McpCaller,
            GatewayPolicies.VirtualFilterRead,
            GatewayPolicies.VirtualFilterWrite,
            GatewayPolicies.VirtualFilterApprove,
            GatewayPolicies.CallerAttributeRead,
            GatewayPolicies.CallerAttributeApprove,
            GatewayPolicies.CallerAttributeSimulate,
            GatewayPolicies.ProductOwnerRead,
            GatewayPolicies.ProductOwnerAdmin,
            GatewayPolicies.ProductSync,
            GatewayPolicies.SunsettingRead,
            GatewayPolicies.SunsettingEvaluate,
            GatewayPolicies.ComplianceCertificateRead,
        ];

        foreach (var policy in policiesToDeny)
        {
            (await authz.AuthorizeAsync(principal, httpContext, policy)).Succeeded
                .ShouldBeFalse($"Legacy role '{legacyRole}' must be denied by policy '{policy}'.");
        }
    }

    [Fact]
    public async Task Consolidated_policies_enforce_role_and_tenant_boundaries()
    {
        await using var sp = BuildAuthorizationProvider();
        var authz = sp.GetRequiredService<IAuthorizationService>();

        // tenant-a:GovernanceAdmin satisfies GovernanceAdmin/AdminOnly/AuditRead only for tenant-a.
        var scopedGovAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:GovernanceAdmin");
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-a"), GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-a"), GatewayPolicies.AdminOnly)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-a"), GatewayPolicies.AuditRead)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-b"), GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-b"), GatewayPolicies.AuditRead)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(scopedGovAdmin, HttpContextWithPrincipal(scopedGovAdmin, "tenant-a"), GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();

        // TenantAdmin satisfies ItAdmin & AdminOnly & AuditRead (via SecurityAuditor implication), not GovernanceAdmin.
        var scopedTenantAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:TenantAdmin");
        (await authz.AuthorizeAsync(scopedTenantAdmin, HttpContextWithPrincipal(scopedTenantAdmin, "tenant-a"), GatewayPolicies.ItAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedTenantAdmin, HttpContextWithPrincipal(scopedTenantAdmin, "tenant-a"), GatewayPolicies.AdminOnly)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedTenantAdmin, HttpContextWithPrincipal(scopedTenantAdmin, "tenant-a"), GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(scopedTenantAdmin, HttpContextWithPrincipal(scopedTenantAdmin, "tenant-b"), GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();

        // SecurityAuditor satisfies AuditRead, not GovernanceAdmin or ItAdmin.
        var auditor = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "SecurityAuditor");
        (await authz.AuthorizeAsync(auditor, HttpContextWithPrincipal(auditor, "tenant-a"), GatewayPolicies.AuditRead)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(auditor, HttpContextWithPrincipal(auditor, "tenant-a"), GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(auditor, HttpContextWithPrincipal(auditor, "tenant-a"), GatewayPolicies.ItAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(auditor, HttpContextWithPrincipal(auditor, "tenant-a"), GatewayPolicies.AdminOnly)).Succeeded.ShouldBeFalse();
    }

    private static DefaultHttpContext HandlerContext(ClaimsPrincipal principal, string? resolvedTenant, string query = "")
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            User = principal,
            RequestServices = services,
        };
        if (!string.IsNullOrEmpty(query))
        {
            ctx.Request.QueryString = new QueryString(query);
        }

        if (!string.IsNullOrWhiteSpace(resolvedTenant))
        {
            ctx.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId(resolvedTenant);
            ctx.Items[SecurityPrincipalContext.ItemKey] = SecurityPrincipalContext.FromPrincipal(
                principal,
                overrideTenant: new TenantId(resolvedTenant));
        }

        return ctx;
    }

    private static int StatusCodeOf(IResult result) =>
        result is IStatusCodeHttpResult sc && sc.StatusCode.HasValue
            ? sc.StatusCode.Value
            : StatusCodes.Status200OK;

    [Fact]
    public async Task Tenant_scoped_roles_match_route_policies_and_in_handler_checks_and_reject_cross_tenant()
    {
        await using var sp = BuildAuthorizationProvider();
        var authz = sp.GetRequiredService<IAuthorizationService>();
        var profileRepo = Substitute.For<IAccessProfileRepository>();
        profileRepo.GetAllProfilesAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AccessProfile>());
        var applySvc = Substitute.For<IDeclarativeConsentApplyService>();
        var sunsetSvc = Substitute.For<ISchemaSunsettingService>();
        var grantSvc = Substitute.For<IGrantRecordExportService>();
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var gdprSvc = Substitute.For<IGdprDisclosureReportService>();
        var pdfExporter = Substitute.For<IGdprAuditReportExporter>();
        bool capturedIsGovAdmin = false;
        gdprSvc.GetGdprDataDisclosureReportAsync(
                Arg.Any<TableIdentifier?>(),
                Arg.Any<Sid?>(),
                Arg.Any<int>(),
                Arg.Do<CallerSecurityContext?>(c => capturedIsGovAdmin = c?.IsGovernanceAdmin == true),
                Arg.Any<CancellationToken>())
            .Returns(new GdprDisclosureReport(
                null,
                "S-1-5-21-OTHER",
                DateTimeOffset.UtcNow,
                365,
                0,
                Array.Empty<GdprRecipientAccessRecord>(),
                Array.Empty<string>(),
                "0 active consents",
                false,
                "sha256:0",
                null));
        pdfExporter.ExportReportToPdf(Arg.Any<GdprDisclosureReport>())
            .Returns(new GdprAuditExportResult([1, 2, 3], "application/pdf", "report.pdf", null, DateTimeOffset.UtcNow));

        // 1. tenant-a:GovernanceAdmin passes both route policies and all GovernanceAdmin / AuditRead handlers in tenant-a.
        var scopedGovAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:GovernanceAdmin");
        var govCtx = HandlerContext(scopedGovAdmin, "tenant-a", "?prune=invalid");
        foreach (var policy in new[]
        {
            GatewayPolicies.GovernanceAdmin,
            GatewayPolicies.AuditRead,
            GatewayPolicies.ComplianceCertificateRead,
            GatewayPolicies.SunsettingRead,
            GatewayPolicies.SunsettingEvaluate,
            GatewayPolicies.ProfileRead,
            GatewayPolicies.ProfileAdmin,
        })
        {
            (await authz.AuthorizeAsync(scopedGovAdmin, govCtx, policy)).Succeeded.ShouldBeTrue(policy);
        }

        GatewayPolicies.HasRequestRole(govCtx, GatewayRole.GovernanceAdmin).ShouldBeTrue();
        GatewayPolicies.HasAnyRequestRole(govCtx, [GatewayRole.GovernanceAdmin, GatewayRole.SecurityAuditor]).ShouldBeTrue();
        StatusCodeOf(await GovernanceEndpoints.ApplyConsentManifestAsync(govCtx, applySvc, CancellationToken.None)).ShouldBe(StatusCodes.Status400BadRequest);
        StatusCodeOf(await GovernanceEndpoints.ApproveProfileAssignmentAsync("p1", null!, govCtx, profileRepo, CancellationToken.None)).ShouldBe(StatusCodes.Status400BadRequest);
        StatusCodeOf(await GovernanceEndpoints.GetProfilesAsync(govCtx, profileRepo, CancellationToken.None)).ShouldBe(StatusCodes.Status200OK);
        StatusCodeOf(await GovernanceEndpoints.EvaluateSunsettingAsync(null, sunsetSvc, govCtx)).ShouldBe(StatusCodes.Status400BadRequest);
        (await GovernanceEndpoints.IsAuthorizedForSimulationAsync(govCtx, null)).ShouldBeTrue();
        StatusCodeOf(await GovernanceEndpoints.ExportGdprPdfAsync(null, null, null, "S-1-5-21-OTHER", null, gdprSvc, pdfExporter, govCtx, CancellationToken.None)).ShouldBe(StatusCodes.Status200OK);
        capturedIsGovAdmin.ShouldBeTrue();
        StatusCodeOf(await ConsentWorkflowAdminEndpoints.VerifyGrantRecordsAsync(null, govCtx, grantSvc, CancellationToken.None)).ShouldBe(StatusCodes.Status400BadRequest);
        StatusCodeOf(await AdminWritePrivilegesEndpoints.GetAsync("", govCtx, Options.Create(new GatewayOptions()), tableRepo, consentRepo)).ShouldBe(StatusCodes.Status400BadRequest);
        ClassificationAdminEndpoints.BuildCaller(govCtx).IsGovernanceAdmin.ShouldBeTrue();
        DocEndpointAccess.BuildCaller(govCtx, "tenant-a", "S-1-5-21-TEST-USER").IsGovernanceAdmin.ShouldBeTrue();
        ConsentApiEndpoints.ResolveCaller(govCtx).IsPrivReader.ShouldBeTrue();
        EndpointSecurity.IsRequestApprover(govCtx).ShouldBeTrue();
        EndpointSecurity.IsRequestTenantBoundGovernanceAdmin(govCtx).ShouldBeTrue();

        // Cross-tenant check fails closed for tenant-a:GovernanceAdmin in tenant-b.
        var wrongGovCtx = HandlerContext(Principal(scheme: "Bearer", roles: "tenant-a:GovernanceAdmin"), "tenant-b", "?prune=invalid");
        (await authz.AuthorizeAsync(wrongGovCtx.User, wrongGovCtx, GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        GatewayPolicies.HasRequestRole(wrongGovCtx, GatewayRole.GovernanceAdmin).ShouldBeFalse();
        StatusCodeOf(await GovernanceEndpoints.ApplyConsentManifestAsync(wrongGovCtx, applySvc, CancellationToken.None)).ShouldBe(StatusCodes.Status403Forbidden);
        StatusCodeOf(await GovernanceEndpoints.ApproveProfileAssignmentAsync("p1", null!, wrongGovCtx, profileRepo, CancellationToken.None)).ShouldBe(StatusCodes.Status403Forbidden);
        StatusCodeOf(await AdminWritePrivilegesEndpoints.GetAsync("", wrongGovCtx, Options.Create(new GatewayOptions()), tableRepo, consentRepo)).ShouldBe(StatusCodes.Status403Forbidden);
        StatusCodeOf(await ConsentWorkflowAdminEndpoints.VerifyGrantRecordsAsync(null, wrongGovCtx, grantSvc, CancellationToken.None)).ShouldBe(StatusCodes.Status403Forbidden);
        ClassificationAdminEndpoints.BuildCaller(wrongGovCtx).IsGovernanceAdmin.ShouldBeFalse();
        DocEndpointAccess.BuildCaller(wrongGovCtx, "tenant-b", "S-1-5-21-TEST-USER").IsGovernanceAdmin.ShouldBeFalse();

        // 2. tenant-a:TenantAdmin passes route policies and handlers for TenantAdmin/AuditRead in tenant-a only.
        var scopedTenantAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:TenantAdmin");
        var tenantAdminCtx = HandlerContext(scopedTenantAdmin, "tenant-a");
        (await authz.AuthorizeAsync(scopedTenantAdmin, tenantAdminCtx, GatewayPolicies.ItAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedTenantAdmin, tenantAdminCtx, GatewayPolicies.UsageAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedTenantAdmin, tenantAdminCtx, GatewayPolicies.ProfileAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedTenantAdmin, tenantAdminCtx, GatewayPolicies.AuditRead)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedTenantAdmin, tenantAdminCtx, GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        GatewayPolicies.HasAnyRequestRole(tenantAdminCtx, GatewayPolicies.UsageAdminRoles).ShouldBeTrue();
        StatusCodeOf(await GovernanceEndpoints.GetProfilesAsync(tenantAdminCtx, profileRepo, CancellationToken.None)).ShouldBe(StatusCodes.Status200OK);
        (await GovernanceEndpoints.IsAuthorizedForSimulationAsync(tenantAdminCtx, null)).ShouldBeTrue();
        ConsentApiEndpoints.ResolveCaller(tenantAdminCtx).IsPrivReader.ShouldBeTrue();

        var wrongTenantAdminCtx = HandlerContext(Principal(scheme: "Bearer", roles: "tenant-a:TenantAdmin"), "tenant-b");
        GatewayPolicies.HasAnyRequestRole(wrongTenantAdminCtx, GatewayPolicies.UsageAdminRoles).ShouldBeFalse();
        StatusCodeOf(await GovernanceEndpoints.GetProfilesAsync(wrongTenantAdminCtx, profileRepo, CancellationToken.None)).ShouldBe(StatusCodes.Status403Forbidden);

        // 3. tenant-a:SecurityAuditor passes AuditRead/ProfileRead/GrantRecord handlers in tenant-a only.
        var scopedAuditor = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "tenant-a:SecurityAuditor");
        var auditorCtx = HandlerContext(scopedAuditor, "tenant-a");
        (await authz.AuthorizeAsync(scopedAuditor, auditorCtx, GatewayPolicies.AuditRead)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedAuditor, auditorCtx, GatewayPolicies.ProfileRead)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(scopedAuditor, auditorCtx, GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeFalse();
        StatusCodeOf(await GovernanceEndpoints.GetProfilesAsync(auditorCtx, profileRepo, CancellationToken.None)).ShouldBe(StatusCodes.Status200OK);
        (await GovernanceEndpoints.IsAuthorizedForSimulationAsync(auditorCtx, null)).ShouldBeTrue();
        StatusCodeOf(await ConsentWorkflowAdminEndpoints.VerifyGrantRecordsAsync(null, auditorCtx, grantSvc, CancellationToken.None)).ShouldBe(StatusCodes.Status400BadRequest);

        var wrongAuditorCtx = HandlerContext(Principal(scheme: "Bearer", roles: "tenant-a:SecurityAuditor"), "tenant-b");
        StatusCodeOf(await ConsentWorkflowAdminEndpoints.VerifyGrantRecordsAsync(null, wrongAuditorCtx, grantSvc, CancellationToken.None)).ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ClusterAdmin_is_denied_on_governance_admin_only_handlers_and_forward_auth_cluster_admin_never_escalates()
    {
        var profileRepo = Substitute.For<IAccessProfileRepository>();
        var applySvc = Substitute.For<IDeclarativeConsentApplyService>();

        // Canonical ClusterAdmin does not satisfy GovernanceAdmin-only handlers.
        var clusterAdmin = Principal(scheme: "Bearer", tenantId: "tenant-a", roles: "ClusterAdmin");
        var clusterCtx = HandlerContext(clusterAdmin, "tenant-a", "?prune=invalid");
        StatusCodeOf(await GovernanceEndpoints.ApproveProfileAssignmentAsync("p1", null!, clusterCtx, profileRepo, CancellationToken.None)).ShouldBe(StatusCodes.Status403Forbidden);
        StatusCodeOf(await GovernanceEndpoints.ApplyConsentManifestAsync(clusterCtx, applySvc, CancellationToken.None)).ShouldBe(StatusCodes.Status403Forbidden);

        // ForwardAuth ClusterAdmin never implies any role in GatewayPolicies.HasRole or HasAnyRole.
        var forwardAuth = Principal(scheme: ClusterAdminPolicy.ForwardAuthScheme, tenantId: "tenant-a", roles: "ClusterAdmin");
        var forwardCtx = HandlerContext(forwardAuth, "tenant-a");

        GatewayRole[] deniedRoles =
        [
            GatewayRole.ClusterAdmin,
            GatewayRole.TenantAdmin,
            GatewayRole.SecurityAuditor,
            GatewayRole.SchemaPublisher,
            GatewayRole.Consumer,
            GatewayRole.GovernanceAdmin,
        ];
        foreach (var role in deniedRoles)
        {
            GatewayPolicies.HasRole(forwardAuth, role).ShouldBeFalse();
            GatewayPolicies.HasRole(forwardAuth, role, "tenant-a").ShouldBeFalse();
            GatewayPolicies.HasRequestRole(forwardCtx, role).ShouldBeFalse();
        }

        GatewayPolicies.HasAnyRole(forwardAuth, deniedRoles).ShouldBeFalse();
        GatewayPolicies.HasAnyRole(forwardAuth, deniedRoles, "tenant-a").ShouldBeFalse();
        GatewayPolicies.HasAnyRequestRole(forwardCtx, deniedRoles).ShouldBeFalse();
        GatewayPolicies.HasAnyRole(forwardAuth, ["ClusterAdmin"]).ShouldBeFalse();
        GatewayPolicies.HasAnyRequestRole(forwardCtx, ["ClusterAdmin"]).ShouldBeFalse();
    }
}
