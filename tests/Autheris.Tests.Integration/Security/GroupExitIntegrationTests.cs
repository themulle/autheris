namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GroupExitIntegrationTests : IClassFixture<GroupExitIntegrationTests.HostFixture>
{
    private const string RealmId = "group-exit-oidc";
    private const string ClientId = "group-exit-client";
    private const string Issuer = "https://group-exit-idp.example.org/oauth2";
    private const string Tenant = "group-exit-tenant";
    private const string SubjectId = "user-exit-1";
    private const string UserSid = $"{RealmId}|{SubjectId}";
    private const string KeyId = "kid-group-exit";
    private static readonly string GroupSid = SidFormat.FormatRealmGroupKey(RealmId, "group-5001");

    private readonly HostFixture _fixture;

    public GroupExitIntegrationTests(HostFixture fixture)
    {
        _fixture = fixture;
    }

    public sealed class HostFixture : IntegrationHost
    {
        private readonly string _jwksDir = Directory.CreateTempSubdirectory("group-exit-jwks").FullName;
        private readonly string _jwksFile;
        private bool _disposed;

        public RSA SigningKey { get; } = RSA.Create(2048);
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
        public ToggleRoleProvider RoleProvider { get; } = new();

        public HostFixture()
        {
            _jwksFile = WriteJwks(_jwksDir, "jwks.json", SigningKey, KeyId);
        }

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
                ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create(nameof(GroupExitIntegrationTests)),
                ["Gateway:Tenancy:DefaultTenantId"] = Tenant,
                ["Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit"] = "1000",
                ["Gateway:Authentication:Mode"] = "Direct",
                ["Gateway:Authentication:GroupCacheTtlMinutes"] = "5",
                // Explicit GenericOidc realm in host configuration (registers 'realm:group-exit-oidc' without enabling EntraId).
                [$"Authentication:Realms:Realms:{RealmId}:Kind"] = nameof(IdentityRealmKind.GenericOidc),
                [$"Authentication:Realms:Realms:{RealmId}:Issuers:0"] = Issuer,
                [$"Authentication:Realms:Realms:{RealmId}:Audiences:0"] = ClientId,
                [$"Authentication:Realms:Realms:{RealmId}:UserKeyClaim"] = "sub",
                [$"Authentication:Realms:Realms:{RealmId}:GroupSource"] = nameof(RealmGroupSource.Resolver),
                [$"Authentication:Realms:Realms:{RealmId}:ResolverChain:0"] = ToggleRoleProvider.Id,
                [$"Authentication:Realms:Realms:{RealmId}:Provisioning"] = nameof(RealmProvisioningMode.None),
                [$"Authentication:Realms:Realms:{RealmId}:FixedTenantId"] = Tenant,
                [$"Authentication:Realms:Realms:{RealmId}:Jwks:File"] = _jwksFile
            });
        }

        protected override void ConfigureHostServices(IServiceCollection services)
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IRoleProvider>(RoleProvider);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_disposed || !disposing)
            {
                return;
            }

            _disposed = true;
            SigningKey.Dispose();
            if (Directory.Exists(_jwksDir))
            {
                Directory.Delete(_jwksDir, recursive: true);
            }
        }

        private static string WriteJwks(string directory, string name, RSA key, string kid)
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
                new RsaSecurityKey(key.ExportParameters(false)) { KeyId = kid });
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                keys = new[] { new { kty = "RSA", kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } }
            }));
            return path;
        }
    }

    [Fact]
    public async Task Access_ends_within_bound()
    {
        var tableId = new TableIdentifier("default", "public", "group_exit_orders");
        using (var seedScope = _fixture.Services.CreateScope())
        {
            var repo = seedScope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Identifier = tableId,
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    DisplayName = tableId.TableName,
                    TableName = tableId.TableName,
                    SchemaName = tableId.Schema,
                    SourceType = "PostgreSql",
                    SourceName = tableId.Domain,
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
            });
            var savedMeta = (await repo.GetTableMetadataAsync(tableId))!;
            await repo.CreateConsentAsync(new Consent
            {
                TableId = savedMeta.Table.Id,
                TableIdentifier = tableId,
                TenantId = new TenantId(Tenant),
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.Group,
                GranteeSid = new Sid(GroupSid),
                ValidFrom = _fixture.Time.GetUtcNow().AddDays(-1),
                ValidTo = _fixture.Time.GetUtcNow().AddDays(30),
                // Empty ColumnRules grants unconstrained clear access required by Envoy ext_authz.
                ColumnRules = [],
                RowFilters = []
            });
        }

        _fixture.RoleProvider.SetMember(true);
        using var client = _fixture.CreateClient();
        var jwt = SignedJwt(_fixture.Time.GetUtcNow());

        // Within GroupCacheTtlMinutes (5m): provider resolves group, HTTP check passes, and MCP session carries valid group expiry.
        using (var allowReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + tableId.TableName))
        {
            allowReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            var allowResp = await client.SendAsync(allowReq);
            allowResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var sessions = new McpSessionStore(
            _fixture.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<McpSessionStore>>(),
            timeProvider: _fixture.Time,
            options: _fixture.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions>>());
        var mcpSession = sessions.CreateSession("mcp-agent", Tenant, UserSid, groupSids: [GroupSid]);
        mcpSession.GroupsExpireAt.ShouldBe(_fixture.Time.GetUtcNow().AddMinutes(5));

        // Remove user from authoritative directory group while L1 cache is warm -> after 5m01s cache expires and access ends.
        _fixture.RoleProvider.SetMember(false);
        _fixture.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using (var denyReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/envoy/check/api/v1/" + tableId.TableName))
        {
            denyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            var denyResp = await client.SendAsync(denyReq);
            denyResp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using var scope = _fixture.Services.CreateScope();
        var pdp = scope.ServiceProvider.GetRequiredService<IAccessDecisionPoint>();
        var meta = (await scope.ServiceProvider.GetRequiredService<IGovernanceRepository>().GetTableMetadataAsync(tableId))!;
        var expiredMcpPrincipal = McpProtocolHandler.BuildPrincipalFromSession(mcpSession, _fixture.Time)!;
        var mcpDecision = await pdp.DecideAsync(new AccessRequest(
            SecurityPrincipalContext.FromPrincipal(expiredMcpPrincipal, new TenantId(Tenant), timeProvider: _fixture.Time),
            meta,
            AccessOperation.Read,
            AccessTransport.Mcp));
        mcpDecision.IsAllowed.ShouldBeFalse();
        mcpDecision.Reasons.ShouldContain(r => r.Code == "GROUPS_DEGRADED");
    }

    private string SignedJwt(DateTimeOffset issuedAt) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Subject = new ClaimsIdentity(
            [
                new("sub", SubjectId),
                new("scp", "Data.Read")
            ]),
            NotBefore = issuedAt.UtcDateTime.AddMinutes(-5),
            Expires = issuedAt.UtcDateTime.AddHours(2),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(_fixture.SigningKey) { KeyId = KeyId },
                SecurityAlgorithms.RsaSha256)
        });

    public sealed class ToggleRoleProvider : IRoleProvider
    {
        public const string Id = "toggle-exit-provider";
        private volatile bool _isMember = true;

        public string ProviderId => Id;

        public void SetMember(bool isMember) => _isMember = isMember;

        public Task<ProviderRoleResult> GetRolesAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default)
        {
            var groups = _isMember
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GroupSid }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new ProviderRoleResult(
                groups,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        }
    }
}
