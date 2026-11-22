namespace Autheris.Tests.Integration.Security;

using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ForwardAuthGovernanceEndpointTests : IClassFixture<ForwardAuthGovernanceEndpointTests.HostFixture>
{
    private const string ForwardSecret = "forward-gov-endpoint-shared-secret-0123456789";
    private const string TenantA = "forward-gov-tenant";

    private readonly HostFixture _fixture;

    public ForwardAuthGovernanceEndpointTests(HostFixture fixture) => _fixture = fixture;

    public sealed class HostFixture : IAsyncLifetime
    {
        public WebApplicationFactory<Program> Factory { get; private set; } = null!;

        public Task InitializeAsync()
        {
            var connectionString = GovernanceTestDatabase.Create(nameof(ForwardAuthGovernanceEndpointTests));
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                Autheris.Tests.Integration.DbtWorkspaceTestSettings.Apply((k, v) => builder.UseSetting(k, v));
                builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                builder.UseSetting("Gateway:GovernanceDb:ConnectionString", connectionString);
                builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");
                builder.UseSetting("Gateway:Tenancy:DefaultTenantId", TenantA);
                builder.UseSetting("Gateway:Authentication:Mode", "Upstream");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:Enabled", "true");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:RequireTrustedProxy", "true");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:TrustedProxies:0", "127.0.0.1");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:SharedSecret", ForwardSecret);
                builder.UseSetting("Gateway:Authentication:ForwardAuth:AllowedRoles:0", "Consumer");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:AllowedRoles:1", "ClusterAdmin");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:AllowedRoles:2", "GovernanceAdmin");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:AllowedRoles:3", "TenantAdmin");
                builder.ConfigureServices(services => services.PostConfigure<IdentityRealmsOptions>(options =>
                {
                    options.Realms["forward-gov"] = new IdentityRealmConfigOptions
                    {
                        Kind = IdentityRealmKind.ForwardAuth,
                        FixedTenantId = TenantA,
                        GroupSource = RealmGroupSource.None,
                        Provisioning = RealmProvisioningMode.None,
                        AllowedGatewayRoles = ["Consumer", "ClusterAdmin", "GovernanceAdmin", "TenantAdmin"]
                    };
                }));
            });
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            Factory.Dispose();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Header_clusteradmin_refused()
    {
        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-User", "proxy-header-attacker");
        client.DefaultRequestHeaders.Add("X-Forwarded-Secret", ForwardSecret);
        client.DefaultRequestHeaders.Add("X-Forwarded-Roles", "Consumer,ClusterAdmin,GovernanceAdmin,TenantAdmin");

        var login = await client.GetAsync("/api/auth/login");
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>();
        var roles = loginJson.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        roles.ShouldContain("Consumer");
        roles.ShouldNotContain("ClusterAdmin");
        roles.ShouldNotContain("GovernanceAdmin");
        roles.ShouldNotContain("TenantAdmin");

        var clusterAdminResp = await client.GetAsync("/api/v1/admin/authz/migration-report");
        clusterAdminResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var governanceResp = await client.GetAsync("/api/v1/governance/classifications/PII");
        governanceResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var itAdminResp = await client.GetAsync("/api/v1/usage/users");
        itAdminResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
