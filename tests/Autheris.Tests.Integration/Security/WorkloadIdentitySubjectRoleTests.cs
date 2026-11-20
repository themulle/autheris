namespace Autheris.Tests.Integration.Security;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Application.Common.Interfaces;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

/// <summary>
/// Workload identity end to end: a Kubernetes-style service account token (no roles claim) is validated against a static JWKS file of its
/// GenericOidc realm and receives ClusterAdmin only through the configured subject mapping.
/// </summary>
[Trait("Category", "Security")]
public sealed class WorkloadIdentitySubjectRoleTests : IClassFixture<WorkloadIdentitySubjectRoleTests.HostFixture>
{
    private const string MappedSubject = "system:serviceaccount:data:dbt-sync";
    private const string OtherSubject = "system:serviceaccount:data:other";
    private const string IssuerA = "https://cluster-a.example";
    private const string IssuerB = "https://cluster-b.example";
    private const string Audience = "autheris";
    private const string TenantA = "workload-tenant-a";
    private const string TenantB = "workload-tenant-b";

    private readonly HostFixture _fixture;

    public WorkloadIdentitySubjectRoleTests(HostFixture fixture) => _fixture = fixture;

    public sealed class HostFixture : IAsyncLifetime
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("workload-jwks").FullName;

        public RSA KeyA { get; } = RSA.Create(2048);
        public RSA KeyB { get; } = RSA.Create(2048);
        public WebApplicationFactory<Program> Factory { get; private set; } = null!;

        public Task InitializeAsync()
        {
            var fileA = WriteJwks("a.json", KeyA, "kid-a");
            var fileB = WriteJwks("b.json", KeyB, "kid-b");
            var connectionString = GovernanceTestDatabase.Create(nameof(WorkloadIdentitySubjectRoleTests));
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                builder.UseSetting("Gateway:GovernanceDb:ConnectionString", connectionString);
                builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                builder.UseSetting("Gateway:Authentication:Mode", "Direct");
                // Host settings (not ConfigureAppConfiguration): the realm schemes are registered from the configuration available at service registration.
                builder.UseSetting("Gateway:Tenancy:DefaultTenantId", TenantA);
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:Kind", "GenericOidc");
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:Issuers:0", IssuerA);
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:Audiences:0", Audience);
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:UserKeyClaim", "sub");
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:FixedTenantId", TenantA);
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:Provisioning", "None");
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:ResolverChain:0", "subject-roles");
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:AllowedGatewayRoles:0", "ClusterAdmin");
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:AllowPrivilegedSubjectRoles", "true");
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:Jwks:File", fileA);
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:SubjectRoles:0:Subject", MappedSubject);
                builder.UseSetting("Authentication:Realms:Realms:cluster-a:SubjectRoles:0:Roles:0", "ClusterAdmin");
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:Kind", "GenericOidc");
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:Issuers:0", IssuerB);
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:Audiences:0", Audience);
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:UserKeyClaim", "sub");
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:FixedTenantId", TenantB);
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:Provisioning", "None");
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:ResolverChain:0", "subject-roles");
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:AllowedGatewayRoles:0", "TenantAdmin");
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:Jwks:File", fileB);
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:SubjectRoles:0:Subject", MappedSubject);
                builder.UseSetting("Authentication:Realms:Realms:cluster-b:SubjectRoles:0:Roles:0", "TenantAdmin");
            });
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            Factory.Dispose();
            KeyA.Dispose();
            KeyB.Dispose();
            Directory.Delete(_dir, recursive: true);
            return Task.CompletedTask;
        }

        private string WriteJwks(string name, RSA key, string kid)
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(key.ExportParameters(false)) { KeyId = kid });
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                keys = new[] { new { kty = "RSA", kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } }
            }));
            return path;
        }
    }

    private static string Token(RSA key, string kid, string issuer, string subject, string? scope = null, string scopeClaimType = "scp") =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = scope is null
                ? new System.Collections.Generic.Dictionary<string, object> { ["sub"] = subject }
                : new System.Collections.Generic.Dictionary<string, object> { ["sub"] = subject, [scopeClaimType] = scope },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256)
        });

    private async Task<HttpStatusCode> PostGovernanceAsync(string token)
    {
        using var client = _fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/extensions/dbt/governance?dryRun=true")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Dbt_Governance_WithMappedServiceAccountToken_Succeeds()
    {
        var status = await PostGovernanceAsync(Token(_fixture.KeyA, "kid-a", IssuerA, MappedSubject));

        status.ShouldNotBe(HttpStatusCode.Forbidden);
        status.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dbt_Governance_WithOtherSubjectToken_Forbidden()
    {
        var status = await PostGovernanceAsync(Token(_fixture.KeyA, "kid-a", IssuerA, OtherSubject));

        status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MappedPrincipal_ReadOnlyScope_StillDeniedOnWrite()
    {
        var status = await PostGovernanceAsync(Token(_fixture.KeyA, "kid-a", IssuerA, MappedSubject, scope: "App.Read"));

        status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RealmToken_ReadOnlyScope_WriteEndpoint_Returns403()
    {
        var status = await PostGovernanceAsync(Token(_fixture.KeyA, "kid-a", IssuerA, MappedSubject, scope: "Agent.Read", scopeClaimType: "scope"));

        status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WorkloadIdentity_MappedRole_WritesAuditRowInStore()
    {
        await PostGovernanceAsync(Token(_fixture.KeyA, "kid-a", IssuerA, MappedSubject));

        using var scope = _fixture.Factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
        var rows = await audit.QueryAuditLogsAsync(targetTable: "IDENTITY_REALMS", tenantId: new TenantId(TenantA), limit: 50);

        rows.Any(r => r.EventType == AuditEventTypes.WorkloadRoleMappingApplied && r.DetailsJson.Contains(MappedSubject, StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task WorkloadIdentity_TokenFromOtherTenantRealm_NoCrossTenantAccess()
    {
        // The second realm maps the same subject to TenantAdmin in another tenant only: no ClusterAdmin, no access to the governance import.
        var status = await PostGovernanceAsync(Token(_fixture.KeyB, "kid-b", IssuerB, MappedSubject));

        status.ShouldBe(HttpStatusCode.Forbidden);

        // A token of realm B signed with realm A's key (or the reverse) never authenticates.
        (await PostGovernanceAsync(Token(_fixture.KeyA, "kid-a", IssuerB, MappedSubject))).ShouldBe(HttpStatusCode.Unauthorized);
    }
}
