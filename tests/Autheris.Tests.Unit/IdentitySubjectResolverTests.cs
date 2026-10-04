namespace Autheris.Tests.Unit;

using System.Security.Claims;
using Autheris.Application.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class IdentitySubjectResolverTests
{
    private readonly IdentitySubjectResolver _resolver = new();

    [Fact]
    public void EntraId_AppOnlyToken_ResolvesToServicePrincipal()
    {
        var identity = new ClaimsIdentity([
            new Claim("idtyp", "app"),
            new Claim("appid", "87654321-4321-4321-4321-210987654321"),
            new Claim("tid", "tenant-entra-prod"),
            new Claim("app_displayname", "ETL-Data-Pipeline"),
            new Claim(ClaimTypes.Role, "BatchReader")
        ], "Bearer");

        var principal = new ClaimsPrincipal(identity);
        var subject = _resolver.ResolveSubject(principal);

        subject.IsServicePrincipal.ShouldBeTrue();
        subject.Type.ShouldBe(SubjectType.ServicePrincipal);
        subject.Provider.ShouldBe(IdentityProviderType.EntraId);
        subject.Id.ShouldBe("87654321-4321-4321-4321-210987654321");
        subject.DisplayName.ShouldBe("ETL-Data-Pipeline");
        subject.Tenant.ShouldNotBeNull();
        subject.Tenant.Value.Value.ShouldBe("tenant-entra-prod");
        subject.Roles.ShouldNotBeNull();
        subject.Roles.ShouldContain("BatchReader");
    }

    [Fact]
    public void EntraId_UserToken_ResolvesToInteractiveUser()
    {
        var identity = new ClaimsIdentity([
            new Claim("oid", "11112222-3333-4444-5555-666677778888"),
            new Claim("tid", "tenant-entra-prod"),
            new Claim("upn", "alice.smith@corp.com"),
            new Claim("preferred_username", "alice.smith@corp.com"),
            new Claim(ClaimTypes.Role, "FinanceAuditor")
        ], "Bearer");

        var principal = new ClaimsPrincipal(identity);
        var subject = _resolver.ResolveSubject(principal);

        subject.IsUser.ShouldBeTrue();
        subject.Type.ShouldBe(SubjectType.User);
        subject.Provider.ShouldBe(IdentityProviderType.EntraId);
        subject.Id.ShouldBe("11112222-3333-4444-5555-666677778888");
        subject.DisplayName.ShouldBe("alice.smith@corp.com");
        subject.Tenant.ShouldNotBeNull();
        subject.Tenant.Value.Value.ShouldBe("tenant-entra-prod");
    }

    [Fact]
    public void ActiveDirectory_KerberosTicket_ResolvesToAdUser()
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-123456789-987654321-1111"),
            new Claim(ClaimTypes.Name, "CORP\\bob.miller"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-9999-FINANCE-GROUP")
        ], "Negotiate");

        var principal = new ClaimsPrincipal(identity);
        var subject = _resolver.ResolveSubject(principal);

        subject.IsUser.ShouldBeTrue();
        subject.Type.ShouldBe(SubjectType.User);
        subject.Provider.ShouldBe(IdentityProviderType.ActiveDirectory);
        subject.Id.ShouldBe("S-1-5-21-123456789-987654321-1111");
        subject.DisplayName.ShouldBe("CORP\\bob.miller");
    }

    [Fact]
    public void ClientCertificate_mTLS_ResolvesToServicePrincipal()
    {
        var identity = new ClaimsIdentity([
            new Claim("client_cert_thumbprint", "9F8E7D6C5B4A3120"),
            new Claim(ClaimTypes.Name, "CN=microservice-orders.corp.local")
        ], "Certificate");

        var principal = new ClaimsPrincipal(identity);
        var subject = _resolver.ResolveSubject(principal);

        subject.IsServicePrincipal.ShouldBeTrue();
        subject.Id.ShouldBe("9F8E7D6C5B4A3120");
        subject.DisplayName.ShouldNotBeNull();
        subject.DisplayName.ShouldContain("mTLS:9F8E7D6C5B4A3120");
    }

    [Fact]
    public void Anonymous_ResolvesToAnonymousUser()
    {
        var subject = _resolver.ResolveSubject(null);

        subject.IsUser.ShouldBeTrue();
        subject.Id.ShouldBe("anonymous");
        subject.DisplayName.ShouldBe("Anonymous");
    }
}
