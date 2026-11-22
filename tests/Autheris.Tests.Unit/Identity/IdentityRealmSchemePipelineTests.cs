namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class IdentityRealmSchemePipelineTests : IDisposable
{
    private const string RealmId = "group-exit-oidc";
    private const string ClientId = "group-exit-client";
    private const string Issuer = "https://group-exit-idp.example.org/oauth2";
    private const string Tenant = "group-exit-tenant";
    private const string SubjectId = "user-exit-1";
    private const string KeyId = "kid-group-exit";
    private static readonly string GroupSid = SidFormat.FormatRealmGroupKey(RealmId, "group-5001");

    private readonly string _jwksDir = Directory.CreateTempSubdirectory("unit-jwks").FullName;
    private readonly RSA _rsa = RSA.Create(2048);

    public void Dispose()
    {
        _rsa.Dispose();
        if (Directory.Exists(_jwksDir))
        {
            Directory.Delete(_jwksDir, recursive: true);
        }
    }

    private string WriteJwksFile()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
            new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = KeyId });
        var path = Path.Combine(_jwksDir, "jwks.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", kid = KeyId, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } }
        }));
        return path;
    }

    private IConfiguration BuildHostConfig(string jwksFile, bool enableEntraId = false) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Tenancy:DefaultTenantId"] = Tenant,
                ["Gateway:Authentication:Mode"] = "Direct",
                ["Gateway:Authentication:GroupCacheTtlMinutes"] = "5",
                ["Gateway:Authentication:EntraId:Enabled"] = enableEntraId ? "true" : "false",
                [$"Authentication:Realms:Realms:{RealmId}:Kind"] = nameof(IdentityRealmKind.GenericOidc),
                [$"Authentication:Realms:Realms:{RealmId}:Issuers:0"] = Issuer,
                [$"Authentication:Realms:Realms:{RealmId}:Audiences:0"] = ClientId,
                [$"Authentication:Realms:Realms:{RealmId}:UserKeyClaim"] = "sub",
                [$"Authentication:Realms:Realms:{RealmId}:GroupSource"] = nameof(RealmGroupSource.Resolver),
                [$"Authentication:Realms:Realms:{RealmId}:ResolverChain:0"] = "toggle-exit-provider",
                [$"Authentication:Realms:Realms:{RealmId}:Provisioning"] = nameof(RealmProvisioningMode.None),
                [$"Authentication:Realms:Realms:{RealmId}:FixedTenantId"] = Tenant,
                [$"Authentication:Realms:Realms:{RealmId}:Jwks:File"] = jwksFile
            })
            .Build();

    [Fact]
    public void GenericOidc_config_without_entra_passes_validators_and_routes_to_realm_scheme()
    {
        var jwksPath = WriteJwksFile();
        var config = BuildHostConfig(jwksPath, enableEntraId: false);
        var env = new TestHostEnvironment(Environments.Development);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(config);
        services.AddSingleton<IHostEnvironment>(env);

        var gatewayOptions = new GatewayOptions();
        config.GetSection(GatewaySections.Root).Bind(gatewayOptions);
        var realmsOptions = config.GetSection(IdentityRealmsOptions.SectionName).Get<IdentityRealmsOptions>()!;

        services.AddGatewayAuth(gatewayOptions, env, config);
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
            return opts;
        });
        services.Configure<GatewayOptions>(config.GetSection(GatewaySections.Root));
        services.Configure<IdentityRealmsOptions>(config.GetSection(IdentityRealmsOptions.SectionName));

        using var provider = services.BuildServiceProvider();
        var schemeValidator = new IdentityRealmSchemeValidator(gatewayOptions);
        schemeValidator.Validate(null, realmsOptions).Succeeded.ShouldBeTrue();

        var coverageValidator = new GenericOidcSchemeCoverageValidator(provider.GetRequiredService<GenericOidcSchemeMap>());
        coverageValidator.Validate(null, realmsOptions).Succeeded.ShouldBeTrue();

        var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            Subject = new ClaimsIdentity([new("sub", SubjectId)]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(_rsa) { KeyId = KeyId },
                SecurityAlgorithms.RsaSha256)
        });

        var selected = GatewayAuthSchemes.SelectJwtScheme(jwt, gatewayOptions, provider.GetRequiredService<GenericOidcSchemeMap>());
        selected.ShouldBe(GenericOidcSchemeMap.SchemeName(RealmId));

        // Contrast: enabling EntraId without an EntraWorkforce realm fails IdentityRealmSchemeValidator.
        var entraConfig = BuildHostConfig(jwksPath, enableEntraId: true);
        var entraGateway = new GatewayOptions();
        entraConfig.GetSection(GatewaySections.Root).Bind(entraGateway);
        var invalidResult = new IdentityRealmSchemeValidator(entraGateway).Validate(null, realmsOptions);
        invalidResult.Failed.ShouldBeTrue();
        invalidResult.FailureMessage.ShouldContain("EntraWorkforce");
    }

    [Fact]
    public async Task GenericOidc_pipeline_with_di_cache_bounds_group_membership_to_ttl()
    {
        var jwksPath = WriteJwksFile();
        var config = BuildHostConfig(jwksPath, enableEntraId: false);
        var gatewayOptions = new GatewayOptions();
        config.GetSection(GatewaySections.Root).Bind(gatewayOptions);
        var realmsOptions = config.GetSection(IdentityRealmsOptions.SectionName).Get<IdentityRealmsOptions>()!;

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        var member = true;
        var providerStub = new StubToggleRoleProvider(() => member);

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<GatewayOptions>(config.GetSection(GatewaySections.Root));
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<RoleResolverCache>();
        using var di = services.BuildServiceProvider();
        var cache = di.GetRequiredService<RoleResolverCache>();
        var sessions = new Autheris.Application.Mcp.Services.McpSessionStore(
            di.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Autheris.Application.Mcp.Services.McpSessionStore>>(),
            timeProvider: time,
            options: di.GetRequiredService<IOptions<GatewayOptions>>());
        var mcpSession = sessions.CreateSession("mcp-agent", Tenant, $"{RealmId}|{SubjectId}", groupSids: [GroupSid]);
        mcpSession.GroupsExpireAt.ShouldBe(time.GetUtcNow().AddMinutes(5));

        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<IdentityPipeline>>();
        var groupLogger = Substitute.For<Microsoft.Extensions.Logging.ILogger<GroupSidResolver>>();
        var captured = new List<string>();
        void Capture(NSubstitute.Core.CallInfo ci) => captured.Add(ci.ArgAt<object>(2)?.ToString() ?? string.Empty);
        logger.When(l => l.Log(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>())).Do(Capture);
        groupLogger.When(l => l.Log(
            Microsoft.Extensions.Logging.LogLevel.Warning,
            Arg.Any<Microsoft.Extensions.Logging.EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>())).Do(Capture);
        var registry = new IdentityRealmRegistry();
        registry.Initialize(realmsOptions);
        realmsOptions.Realms[RealmId].ResolverChain.ShouldBe([Autheris.Application.Identity.Providers.ScimStoreRoleProvider.Id, "toggle-exit-provider"]);
        var store = Substitute.For<Autheris.Domain.Interfaces.IIdentityStoreRepository>();
        store.GetEffectiveGroupSidsForUserAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Autheris.Domain.Interfaces.EffectiveScimGroupsResult(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                MaxDepthExceeded: false,
                HasNonCanonicalSid: false,
                GroupIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        IRoleProvider[] providers =
        [
            new Autheris.Application.Identity.Providers.ScimStoreRoleProvider(store),
            providerStub
        ];
        var optsWrapper = Options.Create(gatewayOptions);
        var roleResolver = new RoleResolver(registry, providers, cache, optsWrapper, timeProvider: time);
        var groupResolver = new GroupSidResolver(roleResolver, providers, optsWrapper, groupLogger, timeProvider: time);
        var pipeline = new IdentityPipeline(registry, groupResolver, roleResolver, options: optsWrapper, logger: logger, timeProvider: time);

        static ClaimsPrincipal RawOidcPrincipal(string scheme) =>
            new(new ClaimsIdentity(
                [new("iss", Issuer), new("aud", ClientId), new("sub", SubjectId)],
                scheme));

        var scheme = GenericOidcSchemeMap.SchemeName(RealmId);

        // Initial authentication populates the cache and resolves realm group key + user SID + fixed tenant.
        var first = await pipeline.ProcessAsync(RawOidcPrincipal(scheme), scheme, null, CancellationToken.None);
        captured.ShouldBeEmpty();
        var firstCtx = SecurityPrincipalContext.FromPrincipal(first, new TenantId(Tenant), timeProvider: time);
        firstCtx.IsAuthenticated.ShouldBeTrue();
        firstCtx.UserSid.Value.ShouldBe($"{RealmId}|{SubjectId}");
        firstCtx.ResolvedTenant.ShouldBe(new TenantId(Tenant));
        firstCtx.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Complete);
        firstCtx.GroupSids.ShouldContain(new Sid(GroupSid));
        firstCtx.GroupsExpireAt.ShouldBe(time.GetUtcNow().AddMinutes(5));

        // Membership removed in directory while L1 cache is still within TTL (2m < 5m): cached group remains valid until TTL expiry.
        member = false;
        time.Advance(TimeSpan.FromMinutes(2));
        var warm = await pipeline.ProcessAsync(RawOidcPrincipal(scheme), scheme, null, CancellationToken.None);
        var warmCtx = SecurityPrincipalContext.FromPrincipal(warm, new TenantId(Tenant), timeProvider: time);
        warmCtx.GroupSids.ShouldContain(new Sid(GroupSid));

        // Advance past the 5m TTL bound (total 5m01s): DI RoleResolverCache sees FakeTimeProvider expiry and queries provider -> access ends.
        time.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1));
        var freshAfterExpiry = await pipeline.ProcessAsync(RawOidcPrincipal(scheme), scheme, null, CancellationToken.None);
        var afterExpiryCtx = SecurityPrincipalContext.FromPrincipal(freshAfterExpiry, new TenantId(Tenant), timeProvider: time);
        afterExpiryCtx.GroupSids.ShouldBeEmpty();

        // Previously stamped pipeline principal also degrades once its TTL expires.
        var staleReused = await pipeline.ProcessAsync(first, scheme, null, CancellationToken.None);
        var staleCtx = SecurityPrincipalContext.FromPrincipal(staleReused, new TenantId(Tenant), timeProvider: time);
        staleCtx.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Degraded);
        staleCtx.GroupSids.ShouldBeEmpty();

        // Bound MCP session built from live IOptions<GatewayOptions> also degrades past its 5m expiry.
        var expiredMcpPrincipal = Autheris.Application.Mcp.Services.McpProtocolHandler.BuildPrincipalFromSession(mcpSession, time)!;
        var expiredMcpCtx = SecurityPrincipalContext.FromPrincipal(expiredMcpPrincipal, new TenantId(Tenant), timeProvider: time);
        expiredMcpCtx.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Degraded);
        expiredMcpCtx.GroupSids.ShouldBeEmpty();
    }

    private sealed class StubToggleRoleProvider(Func<bool> isMember) : IRoleProvider
    {
        public string ProviderId => "toggle-exit-provider";

        public Task<ProviderRoleResult> GetRolesAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderRoleResult(
                isMember() ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GroupSid } : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }
}
