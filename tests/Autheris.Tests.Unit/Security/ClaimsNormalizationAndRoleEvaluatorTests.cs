namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

public class ClaimsNormalizationAndRoleEvaluatorTests
{
    private readonly IGatewayRoleEvaluator _evaluator = new GatewayRoleEvaluator();

    [Fact]
    public async Task Middleware_NormalizesActiveDirectoryWindowsSids()
    {
        var identity = new ClaimsIdentity("Kerberos");
        identity.AddClaim(new Claim("primarysid", "S-1-5-21-123456789-500"));
        identity.AddClaim(new Claim("groupsid", "S-1-5-21-123456789-512"));
        identity.AddClaim(new Claim("http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid", "S-1-5-21-123456789-513"));
        identity.AddClaim(new Claim("role", "DataOwner"));

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        var middleware = new ClaimsNormalizationMiddleware(next: (ctx) => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        var user = context.User;
        user.FindFirst(ClaimTypes.PrimarySid)?.Value.ShouldBe("S-1-5-21-123456789-500");
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value.ShouldBe("S-1-5-21-123456789-500");

        var groupSids = user.FindAll(ClaimTypes.GroupSid).Select(c => c.Value).ToList();
        groupSids.ShouldContain("S-1-5-21-123456789-512");
        groupSids.ShouldContain("S-1-5-21-123456789-513");

        user.FindAll(ClaimTypes.Role).Select(c => c.Value).ShouldContain("DataOwner");
        user.FindFirst("__ClaimsNormalized")?.Issuer.ShouldBe(ClaimsNormalizer.MiddlewareMarkerIssuer);
    }

    [Fact]
    public async Task Middleware_NormalizesOidcClaimsAndTenantRoles()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim("sub", "oidc-user-999"));
        identity.AddClaim(new Claim("tid", "tenant-eu-west"));
        identity.AddClaim(new Claim("roles", "tenant-eu-west:DataOwner"));
        identity.AddClaim(new Claim("roles", "SecurityAuditor"));

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        var middleware = new ClaimsNormalizationMiddleware(next: (ctx) => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        var user = context.User;
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value.ShouldBe("oidc-user-999");
        user.FindFirst("tenant_id")?.Value.ShouldBe("tenant-eu-west");

        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        roles.ShouldContain("tenant-eu-west:DataOwner");
        roles.ShouldContain("SecurityAuditor");

        user.FindFirst("__ClaimsNormalized")?.Issuer.ShouldBe(ClaimsNormalizer.MiddlewareMarkerIssuer);
    }

    [Fact]
    public async Task Middleware_NormalizesClientCertificate_WhenConnectionPresentsMTLS()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=test-gateway-client, O=Autheris, C=DE", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity("Certificate"));
        context.Connection.ClientCertificate = cert;

        var middleware = new ClaimsNormalizationMiddleware(next: (ctx) => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        var user = context.User;
        user.Identity.ShouldNotBeNull();
        user.Identity.IsAuthenticated.ShouldBeTrue();
        user.Identity.AuthenticationType.ShouldBe("Certificate");

        var thumbprint = cert.Thumbprint;
        user.FindFirst("x509_thumbprint")?.Value.ShouldBe(thumbprint);
        user.FindFirst("client_cert_thumbprint")?.Value.ShouldBe(thumbprint);
        user.FindFirst(ClaimTypes.NameIdentifier)?.Value.ShouldBe($"cert:{thumbprint}");
        user.FindFirst(ClaimTypes.PrimarySid)?.Value.ShouldBe($"cert:{thumbprint}");
    }

    [Fact]
    public async Task Middleware_DoesNotAuthenticate_WhenClientCertificatePresentsOnUnauthenticatedConnection()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=attacker, O=Untrusted", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var context = new DefaultHttpContext();
        context.Connection.ClientCertificate = cert;

        var middleware = new ClaimsNormalizationMiddleware(next: (ctx) => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        context.User.Identity?.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task Middleware_StripsForgedMarkerClaims()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.AddClaim(new Claim("__ClaimsNormalized", "1", ClaimValueTypes.String, "https://forged.attacker.com"));
        identity.AddClaim(new Claim("__EnterpriseTransformed", "1", ClaimValueTypes.String, "https://forged.attacker.com"));
        identity.AddClaim(new Claim("sub", "valid-user"));

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity)
        };

        var middleware = new ClaimsNormalizationMiddleware(next: (ctx) => Task.CompletedTask);
        await middleware.InvokeAsync(context);

        var markers = context.User.FindAll(c => c.Type is "__ClaimsNormalized" or "__EnterpriseTransformed").ToList();
        markers.Count.ShouldBe(2);
        markers.ShouldAllBe(c => c.Issuer == ClaimsNormalizer.MiddlewareMarkerIssuer || c.Issuer == ClaimsNormalizer.LegacyMarkerIssuer);
    }

    [Fact]
    public void Evaluator_ClusterAdmin_ImpliesAllRolesGlobally()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "ClusterAdmin")], "Test");
        var user = new ClaimsPrincipal(identity);

        _evaluator.HasRole(user, GatewayRole.ClusterAdmin).ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.GovernanceAdmin).ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.TenantAdmin).ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.DataOwner).ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.DataSteward).ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.SchemaPublisher).ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.SecurityAuditor).ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.Consumer).ShouldBeTrue();

        // Also satisfies any tenant-scoped query
        _evaluator.HasRole(user, GatewayRole.DataOwner, "any-tenant").ShouldBeTrue();
    }

    [Fact]
    public void Evaluator_TenantAdmin_ImpliesStewardshipAndPublishing_WithinTenantScope()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "tenant-a:TenantAdmin")], "Test");
        var user = new ClaimsPrincipal(identity);

        // Within tenant-a scope
        _evaluator.HasRole(user, GatewayRole.TenantAdmin, "tenant-a").ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.DataOwner, "tenant-a").ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.DataSteward, "tenant-a").ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.SchemaPublisher, "tenant-a").ShouldBeTrue();
        _evaluator.HasRole(user, GatewayRole.Consumer, "tenant-a").ShouldBeTrue();

        // TenantAdmin NEVER implies GovernanceAdmin or ClusterAdmin
        _evaluator.HasRole(user, GatewayRole.GovernanceAdmin, "tenant-a").ShouldBeFalse();
        _evaluator.HasRole(user, GatewayRole.ClusterAdmin, "tenant-a").ShouldBeFalse();

        // Outside tenant-a scope (e.g. tenant-b or global)
        _evaluator.HasRole(user, GatewayRole.TenantAdmin, "tenant-b").ShouldBeFalse();
        _evaluator.HasRole(user, GatewayRole.TenantAdmin, null).ShouldBeFalse();
    }

    [Fact]
    public void Evaluator_TenantScopedRole_CannotEscalateToClusterAdminOrGovernanceAdmin()
    {
        // Malicious or misconfigured token claiming "tenant-x:ClusterAdmin" or "tenant-x:GovernanceAdmin"
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Role, "tenant-x:ClusterAdmin"),
            new Claim(ClaimTypes.Role, "tenant-x:GovernanceAdmin")
        ], "Test");
        var user = new ClaimsPrincipal(identity);

        _evaluator.HasRole(user, GatewayRole.ClusterAdmin, "tenant-x").ShouldBeFalse();
        _evaluator.HasRole(user, GatewayRole.ClusterAdmin, null).ShouldBeFalse();
        _evaluator.HasRole(user, GatewayRole.GovernanceAdmin, "tenant-x").ShouldBeFalse();
        _evaluator.HasRole(user, GatewayRole.GovernanceAdmin, null).ShouldBeFalse();
    }

    [Fact]
    public void Evaluator_ExactRoleChecks_DoNotApplyHierarchy()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "Analyst")], "Test");
        var user = new ClaimsPrincipal(identity);

        // Exact match works
        _evaluator.HasExactRole(user, "Analyst").ShouldBeTrue();

        // Exact match fails when checking a role not literally held, even if Analyst maps to Consumer
        _evaluator.HasExactRole(user, "Consumer").ShouldBeFalse();
        _evaluator.HasAnyExactRole(user, ["SchemaPublisher", "SchemaAdmin"]).ShouldBeFalse();

        // Hierarchical match works
        _evaluator.HasRole(user, GatewayRole.Consumer).ShouldBeTrue();
    }

    [Fact]
    public void SecurityPrincipalContext_TypedRoleMethods_RespectHierarchy()
    {
        var secContext = new SecurityPrincipalContext
        {
            UserSid = new Sid("user-1"),
            TenantId = new TenantId("tenant-1"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DataOwner" },
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = "Bearer"
        };

        secContext.HasRole(GatewayRole.DataOwner).ShouldBeTrue();
        secContext.HasRole(GatewayRole.DataSteward).ShouldBeTrue(); // Implied by DataOwner
        secContext.HasRole(GatewayRole.Consumer).ShouldBeTrue();    // Implied by DataOwner
        secContext.HasRole(GatewayRole.ClusterAdmin).ShouldBeFalse();
        secContext.HasRole(GatewayRole.GovernanceAdmin).ShouldBeFalse();

        secContext.HasAnyRole(GatewayRole.GovernanceAdmin, GatewayRole.DataOwner).ShouldBeTrue();
    }

    [Fact]
    public void SecurityPrincipalContext_TenantScopedRoleWithColon_CannotSatisfyClusterAdminOrGovernanceAdmin()
    {
        var secContext = new SecurityPrincipalContext
        {
            UserSid = new Sid("user-tenant-admin"),
            TenantId = new TenantId("tenant-1"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tenant-1:ClusterAdmin", "tenant-1:GovernanceAdmin" },
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tenant-1:ClusterAdmin" },
            AuthenticationScheme = "Bearer"
        };

        // Colon-prefixed roles must never satisfy global administrative roles
        secContext.IsClusterAdmin.ShouldBeFalse();
        secContext.HasRole(GatewayRole.ClusterAdmin).ShouldBeFalse();
        secContext.HasRole(GatewayRole.GovernanceAdmin).ShouldBeFalse();
        secContext.HasAnyRole(GatewayRole.ClusterAdmin, GatewayRole.GovernanceAdmin).ShouldBeFalse();
    }
}
