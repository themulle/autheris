namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Providers;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.Security;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class EntraOverageIntegrationTests : IClassFixture<EntraOverageIntegrationTests.HostFixture>
{
    private const string UserOid = "33333333-3333-3333-3333-333333333333";
    private const string UserSid = "S-1-5-21-7100-7200-7300-1001";
    private const string GraphGroupSid = "S-1-5-21-7100-7200-7300-2001";
    private const string OtherGroupSid = "S-1-5-21-7100-7200-7300-2002";
    private const string CloudGroupObjectId = "11223344-5566-7788-99aa-bbccddeeff00";
    private const string CloudGroupSid = "S-1-12-1-287454020-2005423462-3434850969-16772829";
    private const string EntraTenant = "11111111-1111-1111-1111-111111111111";
    private const string EntraClient = "22222222-2222-2222-2222-222222222222";
    private const string Issuer = $"https://login.microsoftonline.com/{EntraTenant}/v2.0";
    private const string RequestTenant = "overage-e2e-tenant";

    private static readonly SymmetricSecurityKey TestKey = new(Encoding.UTF8.GetBytes("overage-e2e-signing-key-at-least-256-bits-long-1234"));
    private static readonly TableIdentifier OpenTable = new("default", "public", "overage_e2e_open");
    private static readonly TableIdentifier UserGrantedTable = new("default", "public", "overage_e2e_user");
    private static readonly TableIdentifier NoGrantTable = new("default", "public", "overage_e2e_nogrant");

    private readonly HostFixture _fixture;

    public EntraOverageIntegrationTests(HostFixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class HostFixture : IntegrationHost
    {
        public ControllableGraphClientAdapter GraphAdapter { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            base.ConfigureWebHost(builder);
        }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create(nameof(EntraOverageIntegrationTests)),
                ["Gateway:Tenancy:DefaultTenantId"] = RequestTenant,
                ["Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit"] = "1000",
                ["Gateway:Authentication:Mode"] = "Direct",
                ["Gateway:Authentication:EntraId:Enabled"] = "true",
                ["Gateway:Authentication:EntraId:TenantId"] = EntraTenant,
                ["Gateway:Authentication:EntraId:ClientId"] = EntraClient,
                ["Gateway:Authentication:EntraId:Audience"] = EntraClient,
                ["Gateway:Authentication:EntraId:RequireHttpsMetadata"] = "false",
                ["Gateway:Authentication:MaxGroupSidsPerCaller"] = "4",
            });
        }

        protected override void ConfigureHostServices(IServiceCollection services)
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters.IssuerSigningKey = TestKey;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration());
            });
            services.PostConfigure<IdentityRealmsOptions>(options =>
            {
                options.Realms["overage-entra"] = new IdentityRealmConfigOptions
                {
                    Kind = IdentityRealmKind.EntraWorkforce,
                    Issuers = [Issuer],
                    Audiences = [EntraClient],
                    GroupSource = RealmGroupSource.Token,
                    Provisioning = RealmProvisioningMode.None,
                    FixedTenantId = RequestTenant
                };
            });
            services.RemoveAll<IGraphClientAdapter>();
            services.AddSingleton<IGraphClientAdapter>(GraphAdapter);
        }
    }

    private async Task EnsureSeededAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        if (await repo.GetTableMetadataAsync(OpenTable) is not null)
        {
            return;
        }

        foreach (var id in new[] { OpenTable, UserGrantedTable, NoGrantTable })
        {
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    DisplayName = id.TableName,
                    TableName = id.TableName,
                    SchemaName = id.Schema,
                    SourceType = "PostgreSql",
                    SourceName = id.Domain,
                    IsActive = true
                },
                Identifier = id,
                Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
            });
        }

        var openMeta = await repo.GetTableMetadataAsync(OpenTable);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = openMeta!.Table.Id,
            TableIdentifier = OpenTable,
            TenantId = new TenantId(RequestTenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid(GraphGroupSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            ColumnRules = []
        });

        var userMeta = await repo.GetTableMetadataAsync(UserGrantedTable);
        await repo.CreateConsentAsync(new Consent
        {
            TableId = userMeta!.Table.Id,
            TableIdentifier = UserGrantedTable,
            TenantId = new TenantId(RequestTenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(7),
            ColumnRules = []
        });
    }

    private static string SignedJwt(params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            new("sub", "overage-subject"),
            new("oid", UserOid),
            new("tid", EntraTenant),
            new("azp", EntraClient),
            new("scp", "Data.Read"),
            new("onprem_sid", UserSid)
        };
        claims.AddRange(extraClaims);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = EntraClient,
            Subject = new ClaimsIdentity(claims),
            SigningCredentials = new SigningCredentials(TestKey, SecurityAlgorithms.HmacSha256Signature),
            Expires = DateTime.UtcNow.AddHours(1)
        });
        return handler.WriteToken(token);
    }

    [Fact]
    public async Task Overage_token_resolves_via_graph_or_denies()
    {
        await EnsureSeededAsync();
        using var client = _fixture.CreateClient();
        var graphProvider = _fixture.Services.GetServices<IRoleProvider>().OfType<GraphRoleProvider>().Single();

        try
        {
            // 1. Overage via hasgroups=true with partial token group (OtherGroupSid):
            //    Graph is consulted with the token oid; partial token group is discarded and Graph groups are applied.
            _fixture.GraphAdapter.TransitiveMemberOf = (key, _) =>
                Task.FromResult<IReadOnlyList<string>>(key == UserOid ? [GraphGroupSid, CloudGroupObjectId] : []);

            var hasGroupsJwt = SignedJwt(new Claim("hasgroups", "true"), new Claim("groups", OtherGroupSid));
            using (var loginReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/login"))
            {
                loginReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hasGroupsJwt);
                using var loginResp = await client.SendAsync(loginReq);
                loginResp.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var json = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
                var groups = json.RootElement.GetProperty("groups").EnumerateArray().Select(e => e.GetString()).ToList();
                groups.ShouldContain(GraphGroupSid);
                groups.ShouldContain(CloudGroupSid);
                groups.ShouldNotContain(OtherGroupSid);
            }

            using (var allowReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName))
            {
                allowReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hasGroupsJwt);
                using var allowResp = await client.SendAsync(allowReq);
                allowResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            using (var noGrantReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + NoGrantTable.TableName))
            {
                noGrantReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hasGroupsJwt);
                using var noGrantResp = await client.SendAsync(noGrantReq);
                noGrantResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            // 2. Overage via _claim_names and via _claim_sources with omitted groups also resolve via Graph.
            var claimNamesJwt = SignedJwt(new Claim("_claim_names", "{\"groups\":\"src1\"}"));
            using (var claimNamesReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName))
            {
                claimNamesReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", claimNamesJwt);
                using var claimNamesResp = await client.SendAsync(claimNamesReq);
                claimNamesResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            var claimSourcesJwt = SignedJwt(new Claim("_claim_sources", "{\"src1\":{\"endpoint\":\"https://graph.windows.net/v1/users/u/getMemberObjects\"}}"));
            using (var claimSourcesReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName))
            {
                claimSourcesReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", claimSourcesJwt);
                using var claimSourcesResp = await client.SendAsync(claimSourcesReq);
                claimSourcesResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            // 3. Graph group count exceeding MaxGroupSidsPerCaller (4) denies fail-closed (even with partial token group or direct user Allow).
            _fixture.GraphAdapter.TransitiveMemberOf = (_, _) => Task.FromResult<IReadOnlyList<string>>(
            [
                GraphGroupSid,
                "S-1-5-21-7100-7200-7300-2010",
                "S-1-5-21-7100-7200-7300-2011",
                "S-1-5-21-7100-7200-7300-2012",
                "S-1-5-21-7100-7200-7300-2013"
            ]);
            var overageWithGrantedPartialJwt = SignedJwt(new Claim("hasgroups", "true"), new Claim("groups", GraphGroupSid));

            using (var capOpenReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName))
            {
                capOpenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", overageWithGrantedPartialJwt);
                using var capOpenResp = await client.SendAsync(capOpenReq);
                capOpenResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var capUserReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + UserGrantedTable.TableName))
            {
                capUserReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", overageWithGrantedPartialJwt);
                using var capUserResp = await client.SendAsync(capUserReq);
                capUserResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            // 4. Graph failure (429 throttle) denies fail-closed and yields empty groups on /api/auth/login.
            _fixture.GraphAdapter.TransitiveMemberOf = (_, _) =>
                throw new GraphThrottledException(TimeSpan.FromSeconds(10), "429 Too Many Requests");

            using (var failLoginReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/login"))
            {
                failLoginReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", overageWithGrantedPartialJwt);
                using var failLoginResp = await client.SendAsync(failLoginReq);
                failLoginResp.StatusCode.ShouldBe(HttpStatusCode.OK);
                using var failJson = JsonDocument.Parse(await failLoginResp.Content.ReadAsStringAsync());
                failJson.RootElement.GetProperty("groups").GetArrayLength().ShouldBe(0);
            }

            using (var failCheckReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + OpenTable.TableName))
            {
                failCheckReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", overageWithGrantedPartialJwt);
                using var failCheckResp = await client.SendAsync(failCheckReq);
                failCheckResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var failUserReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + UserGrantedTable.TableName))
            {
                failUserReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", overageWithGrantedPartialJwt);
                using var failUserResp = await client.SendAsync(failUserReq);
                failUserResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }
        }
        finally
        {
            _fixture.GraphAdapter.TransitiveMemberOf = (_, _) => Task.FromResult<IReadOnlyList<string>>([]);
            graphProvider.ResetCircuitBreaker();
        }
    }

    public sealed class ControllableGraphClientAdapter : IGraphClientAdapter
    {
        public Func<string, CancellationToken, Task<IReadOnlyList<string>>> TransitiveMemberOf { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> GetTransitiveMemberOfAsync(string userKey, CancellationToken ct = default) =>
            TransitiveMemberOf(userKey, ct);

        public Task<IReadOnlyList<string>> GetAppRoleAssignmentsAsync(string userKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
