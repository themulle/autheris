namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.Security;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

/// <summary>
/// One host in <c>Authentication:Mode=Both</c>: the same person arriving by bearer token, Basic or trusted ForwardAuth headers gets the
/// same user SID, the same canonical group SIDs and the same column and grant outcome, and the Envoy check decides from those groups too.
/// </summary>
[Trait("Category", "Security")]
public sealed class AuthModeEndToEndTests : IClassFixture<AuthModeEndToEndTests.HostFixture>
{
    private const string UserSid = "S-1-5-21-1111-2222-3333-1001";
    private const string GroupSid = "S-1-5-21-1111-2222-3333-2001";
    private const string EntraTenant = "11111111-1111-1111-1111-111111111111";
    private const string EntraClient = "22222222-2222-2222-2222-222222222222";
    private const string Issuer = $"https://login.microsoftonline.com/{EntraTenant}/v2.0";
    private const string RequestTenant = "e2e-tenant";
    private const string BasicUser = "e2e-basic-user";
    private const string BasicPassword = "E2e-Basic-Password-1!";
    private const string ForwardSecret = "e2e-forward-auth-shared-secret-0123456789";
    private const string PeerHeader = "X-E2E-Peer-Ip";

    private static readonly SymmetricSecurityKey TestKey = new(Encoding.UTF8.GetBytes("a-very-secret-test-key-of-at-least-256-bits-length!"));
    private static readonly TableIdentifier Restricted = new("default", "public", "e2e_restricted");
    private static readonly TableIdentifier Open = new("default", "public", "e2e_open");
    private static readonly TableIdentifier NoGrant = new("default", "public", "e2e_nogrant");

    private readonly HostFixture _fixture;

    public AuthModeEndToEndTests(HostFixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class HostFixture : IAsyncLifetime
    {
        public WebApplicationFactory<Program> Factory { get; }

        public HostFixture()
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create(nameof(AuthModeEndToEndTests)));
                // appsettings.Development.json sets the default tenant and wins over UseSetting; a later configuration source overrides it.
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Gateway:Tenancy:DefaultTenantId"] = RequestTenant }));
                builder.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "1000");

                builder.UseSetting("Gateway:Authentication:Mode", "Both");
                builder.UseSetting("Gateway:Authentication:BasicAuth:Enabled", "true");
                builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Username", BasicUser);
                builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Password", BasicPassword);
                builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:Sid", UserSid);
                builder.UseSetting("Gateway:Authentication:BasicAuth:Users:0:GroupSids:0", GroupSid);

                builder.UseSetting("Gateway:Authentication:EntraId:Enabled", "true");
                builder.UseSetting("Gateway:Authentication:EntraId:TenantId", EntraTenant);
                builder.UseSetting("Gateway:Authentication:EntraId:ClientId", EntraClient);
                builder.UseSetting("Gateway:Authentication:EntraId:Audience", EntraClient);
                builder.UseSetting("Gateway:Authentication:EntraId:RequireHttpsMetadata", "false");

                builder.UseSetting("Gateway:Authentication:ForwardAuth:Enabled", "true");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:RequireTrustedProxy", "true");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:TrustedProxies:0", "127.0.0.1");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:SharedSecret", ForwardSecret);
                builder.UseSetting("Gateway:Authentication:ForwardAuth:GroupSource", "Header");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:TrustUpstreamSids", "true");
                builder.UseSetting("Gateway:Authentication:ForwardAuth:UserSidHeader", "X-Forwarded-User-Sid");

                builder.ConfigureServices(services =>
                {
                    // Test-only: a signing key instead of the identity provider metadata, and a peer address chosen per request.
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.TokenValidationParameters.IssuerSigningKey = TestKey;
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration());
                    });
                    services.PostConfigure<IdentityRealmsOptions>(options =>
                    {
                        options.Realms["e2e-entra"] = new IdentityRealmConfigOptions
                        {
                            Kind = IdentityRealmKind.EntraWorkforce,
                            Issuers = [Issuer],
                            Audiences = [EntraClient],
                            GroupSource = RealmGroupSource.Token,
                            Provisioning = RealmProvisioningMode.None
                        };
                        options.Realms["e2e-forward-auth"] = new IdentityRealmConfigOptions
                        {
                            Kind = IdentityRealmKind.ForwardAuth,
                            Provisioning = RealmProvisioningMode.None
                        };
                    });
                    services.AddSingleton<IStartupFilter>(new PeerAddressStartupFilter());
                });
            });
        }

        public async Task InitializeAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            foreach (var id in new[] { Restricted, Open, NoGrant })
            {
                await repo.UpsertTableMetadataAsync(new TableMetadata
                {
                    Table = new Table { Id = Guid.NewGuid(), DisplayName = id.TableName, TableName = id.TableName, SchemaName = id.Schema, SourceType = "Sqlite", SourceName = id.Domain, IsActive = true },
                    Identifier = id,
                    Columns =
                    [
                        new TableColumn { ColumnName = "id", DataType = "integer" },
                        new TableColumn { ColumnName = "region", DataType = "varchar" },
                        new TableColumn { ColumnName = "email", DataType = "varchar" }
                    ]
                });
            }

            await GrantAsync(repo, Restricted, withRowFilter: true);
            await GrantAsync(repo, Open, withRowFilter: false);
        }

        public Task DisposeAsync()
        {
            Factory.Dispose();
            return Task.CompletedTask;
        }

        private static async Task GrantAsync(IGovernanceRepository repo, TableIdentifier id, bool withRowFilter)
        {
            var meta = await repo.GetTableMetadataAsync(id);
            await repo.CreateConsentAsync(new Consent
            {
                TableId = meta!.Table.Id,
                TableIdentifier = id,
                TenantId = new TenantId(RequestTenant),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.Group,
                GranteeSid = new Sid(GroupSid),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                // A consent without column rules releases every column in clear (an unconstrained allow, which the Envoy check needs).
                ColumnRules = withRowFilter
                    ?
                    [
                        new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                        new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
                        new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
                    ]
                    : [],
                RowFilters = withRowFilter
                    ? [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueJson = "\"EU\"" }]
                    : []
            });
        }
    }

    /// <summary>Test-only: takes the connection peer address from a request header so a request can come from an untrusted peer.</summary>
    private sealed class PeerAddressStartupFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    if (context.Request.Headers.TryGetValue(PeerHeader, out var value) && IPAddress.TryParse(value.ToString(), out var peer))
                    {
                        context.Connection.RemoteIpAddress = peer;
                    }

                    await nextMiddleware();
                });
                next(app);
            };
    }

    public enum Mode
    {
        Jwt,
        Basic,
        ForwardAuth
    }

    public static TheoryData<Mode> Modes => new() { Mode.Jwt, Mode.Basic, Mode.ForwardAuth };

    private static string SignedToken()
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = EntraClient,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "e2e-subject"),
                new Claim("oid", "33333333-3333-3333-3333-333333333333"),
                new Claim("tid", EntraTenant),
                new Claim("azp", EntraClient),
                new Claim("scp", "Data.Read"),
                new Claim("onprem_sid", UserSid),
                new Claim("groups", GroupSid)
            ]),
            SigningCredentials = new SigningCredentials(TestKey, SecurityAlgorithms.HmacSha256Signature),
            Expires = DateTime.UtcNow.AddHours(1)
        });
        return handler.WriteToken(token);
    }

    private static void Authenticate(HttpRequestMessage request, Mode mode)
    {
        switch (mode)
        {
            case Mode.Jwt:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SignedToken());
                break;
            case Mode.Basic:
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{BasicUser}:{BasicPassword}")));
                break;
            default:
                AddForwardAuthHeaders(request);
                break;
        }
    }

    private static void AddForwardAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-Forwarded-User", "e2e-forward-user");
        request.Headers.Add("X-Forwarded-User-Sid", UserSid);
        request.Headers.Add("X-Forwarded-Groups", GroupSid);
        request.Headers.Add("X-Forwarded-Secret", ForwardSecret);
    }

    private static HttpRequestMessage Explain(Mode mode)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/authz/explain")
        {
            Content = JsonContent.Create(new
            {
                subject = new { sid = UserSid },
                @object = Restricted.ToString(),
                operation = "Read"
            })
        };
        Authenticate(request, mode);
        return request;
    }

    private static string LevelOf(JsonElement level) =>
        level.ValueKind == JsonValueKind.Number ? ((ColumnAccessLevel)level.GetInt32()).ToString() : level.GetString()!;

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Same_user_same_groups_identical_security_context_across_modes(Mode mode)
    {
        // The explain call answers for the caller's own SID only (any other SID is Forbidden), so a 200 proves the user SID.
        var response = await _fixture.Factory.CreateClient().SendAsync(Explain(mode));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        root.GetProperty("allowed").GetBoolean().ShouldBeTrue();
        var groups = root.GetProperty("groups");
        groups.GetProperty("status").ToString().ShouldBe("Complete", StringCompareShould.IgnoreCase);
        groups.GetProperty("groupSids").EnumerateArray().Select(g => g.GetString()).ShouldBe([GroupSid]);
        var levels = root.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("columnName").GetString()!, c => LevelOf(c.GetProperty("level")), StringComparer.OrdinalIgnoreCase);
        levels["id"].ShouldBe(nameof(ColumnAccessLevel.Clear));
        levels["email"].ShouldBe(nameof(ColumnAccessLevel.Mask));
        root.GetProperty("grants").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Upstream_untrusted_peer_returns_401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/authz/objects/" + Restricted + "/grants");
        AddForwardAuthHeaders(request);
        request.Headers.Add(PeerHeader, "203.0.113.7");

        var response = await _fixture.Factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertNoDataAsync(response);
    }

    [Fact]
    public async Task Both_mode_ambiguous_identity_returns_401()
    {
        using var request = Explain(Mode.Jwt);
        AddForwardAuthHeaders(request);

        var response = await _fixture.Factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await AssertNoDataAsync(response);
    }

    [Fact]
    public async Task Envoy_check_uses_same_groups_as_http()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + Open.TableName);
        Authenticate(request, Mode.Jwt);

        var response = await _fixture.Factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Discriminating: the same token is denied on a table without a grant for its group.
        using var other = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + NoGrant.TableName);
        Authenticate(other, Mode.Jwt);
        (await _fixture.Factory.CreateClient().SendAsync(other)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Envoy_check_denies_without_group_grant()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + NoGrant.TableName);
        Authenticate(request, Mode.Jwt);

        var response = await _fixture.Factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task AssertNoDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(GroupSid);
        body.ShouldNotContain(UserSid);
        body.ShouldNotContain(Restricted.TableName);
    }
}
