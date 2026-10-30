namespace Autheris.Tests.Unit.Tenancy;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Application.Interfaces;
using Autheris.Application.Mesh.Services;
using Autheris.Application.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-DT-4 (DT-A-2..5, DT-A-12, DT-SC-03/05/06/09/10): ingress cutover, claims-only tenant read and the middleware gate (semantics S1-S8).</summary>
[Trait("Category", "Security")]
public sealed class TenantIngressTests
{
    private const string Default = "dflt";

    private static ClaimsPrincipal User(string? tenant, string[]? roles = null, string authType = "Bearer", params Claim[] extra)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-u"), new(ClaimTypes.NameIdentifier, "u") };
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(extra);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authType));
    }

    private static TenantResolver Resolver(string? defaultTenant, IIdentityRealmRegistry? realms = null) =>
        new(() => new TenancyOptions { DefaultTenantId = defaultTenant }, realms, NullLogger<TenantResolver>.Instance);

    private static async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal, string? defaultTenant, IdentityRealm? realm = null)
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        if (realm is not null)
        {
            registry.GetRealm(realm.Id).Returns(realm);
            registry.GetDefaultRealm().Returns(realm);
        }

        var transformation = new EnterpriseClaimsTransformation(registry, tenantResolver: Resolver(defaultTenant, registry));
        return await transformation.TransformAsync(principal);
    }

    private static IdentityRealm Realm(TenantBindingSpecification binding) => new("r1", IdentityRealmKind.GenericOidc, binding);

    // ---- S1/S3/S6: the claims transformation is the one default-assignment ingress ----

    [Fact]
    public async Task S3_Claimless_With_Default_Is_Stamped_Default_Source_Default()
    {
        var result = await TransformAsync(User(null), Default);

        result.GetTenantId().Value.ShouldBe(Default);
        result.FindFirst(TenantSourceClaim.ClaimType)!.Value.ShouldBe(TenantSourceClaim.Default);
    }

    [Fact]
    public async Task S6_Claimless_Without_Default_Stays_Unresolved_Not_Legacy()
    {
        var result = await TransformAsync(User(null), null);

        Should.Throw<TenantUnresolvedException>(() => result.GetTenantId());
        result.FindFirst(TenantSourceClaim.ClaimType).ShouldBeNull();
    }

    [Fact]
    public async Task S1_Claim_Wins_Over_Default()
    {
        var result = await TransformAsync(User("acme"), Default);

        result.GetTenantId().Value.ShouldBe("acme");
        result.FindFirst(TenantSourceClaim.ClaimType)!.Value.ShouldBe(TenantSourceClaim.Claim);
    }

    [Theory]
    [InlineData("bad tenant!!")]
    [InlineData("_platform")]
    [InlineData("_x")]
    public async Task S2_Invalid_Or_Reserved_Claim_Is_Invalid_Never_The_Default(string claim)
    {
        var result = await TransformAsync(User(claim), Default);

        Should.Throw<TenantClaimInvalidException>(() => result.GetTenantId());
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Basic")]
    [InlineData("ForwardAuth")]
    [InlineData("Scim")]
    [InlineData("TestAuth")]
    public async Task Token_Supplied_Tenant_Source_Claim_Is_Stripped_On_Every_Ingress(string scheme)
    {
        var forged = User(null, null, scheme, new Claim(TenantSourceClaim.ClaimType, "realm"), new Claim(TenantSourceClaim.ClaimType, "claim"));

        var result = await TransformAsync(forged, Default);

        result.FindAll(TenantSourceClaim.ClaimType).Select(c => c.Value).ShouldBe([TenantSourceClaim.Default]);
        var noDefault = await TransformAsync(User(null, null, scheme, new Claim(TenantSourceClaim.ClaimType, "claim")), null);
        noDefault.FindFirst(TenantSourceClaim.ClaimType).ShouldBeNull();
    }

    [Fact]
    public async Task DT_SC_06_Unbound_Realm_Token_Tenant_Claim_Is_Ignored()
    {
        var spoofed = User("spoofed-tenant", null, "Bearer", new Claim("tid", "also-spoofed"));

        var result = await TransformAsync(spoofed, Default, Realm(TenantBindingSpecification.Unbound()));

        result.GetTenantId().Value.ShouldBe(Default);
        result.FindAll("tenant_id").Count().ShouldBe(1);
        result.FindFirst("tid").ShouldBeNull();
    }

    [Fact]
    public async Task Unbound_Realm_Without_Default_Is_Unresolved_Even_With_Token_Claim()
    {
        var result = await TransformAsync(User("spoofed-tenant"), null, Realm(TenantBindingSpecification.Unbound()));

        Should.Throw<TenantUnresolvedException>(() => result.GetTenantId());
    }

    [Fact]
    public async Task Fixed_Realm_Binding_Wins_And_Leaves_One_Unambiguous_Tenant_Claim()
    {
        var token = User("other", null, "Bearer", new Claim("tid", "entra-directory-guid"));

        var result = await TransformAsync(token, Default, Realm(TenantBindingSpecification.Fixed(new TenantId("pinned"))));

        result.GetTenantId().Value.ShouldBe("pinned");
        result.FindFirst(TenantSourceClaim.ClaimType)!.Value.ShouldBe(TenantSourceClaim.Realm);
    }

    [Fact]
    public async Task ClaimMapped_Realm_With_Unmapped_Value_Is_Invalid()
    {
        var binding = TenantBindingSpecification.ClaimMapped("grp", new Dictionary<string, TenantId> { ["g1"] = new TenantId("mapped") });
        var token = User(null, null, "Bearer", new Claim("grp", "unknown"));

        var result = await TransformAsync(token, Default, Realm(binding));

        Should.Throw<TenantClaimInvalidException>(() => result.GetTenantId());
    }

    [Fact]
    public void GetTenantId_Absent_Throws_And_Never_Returns_A_Sentinel()
    {
        Should.Throw<TenantUnresolvedException>(() => User(null).GetTenantId());
        Should.Throw<TenantUnresolvedException>(() => new ClaimsPrincipal().GetTenantId());
        Should.Throw<TenantUnresolvedException>(() => ((ClaimsPrincipal?)null).GetTenantId());
        Should.Throw<TenantClaimInvalidException>(() => User("bad tenant!!").GetTenantId());
        var conflicting = User("a", null, "Bearer", new Claim("tid", "b"));
        Should.Throw<TenantClaimInvalidException>(() => conflicting.GetTenantId());
    }

    // ---- SecurityContextFactory (claims only, S7 header rules, DT-SC-09) ----

    private static DefaultHttpContext Http(ClaimsPrincipal user, string? header = null)
    {
        var ctx = new DefaultHttpContext { User = user };
        if (header is not null)
        {
            ctx.Request.Headers["X-Tenant-ID"] = header;
        }

        return ctx;
    }

    [Fact]
    public void SecurityContextFactory_Claimless_Is_Unresolved_Not_Legacy()
    {
        var context = SecurityContextFactory.CreateFromHttpContext(Http(User(null)));

        context.ResolvedTenant.ShouldBeNull();
        context.TenantClaimInvalid.ShouldBeFalse();
        Should.Throw<TenantUnresolvedException>(() => context.TenantId);
    }

    [Fact]
    public void SecurityContextFactory_Invalid_Claim_Is_Flagged_And_Header_Is_Not_Consulted()
    {
        var context = SecurityContextFactory.CreateFromHttpContext(Http(User("bad tenant!!", ["ClusterAdmin"]), "other"));

        context.TenantClaimInvalid.ShouldBeTrue();
        context.ResolvedTenant.ShouldBeNull();
        Should.Throw<TenantClaimInvalidException>(() => context.TenantId);
    }

    [Fact]
    public void NonClusterAdmin_Header_Differing_Tenant_Forbidden()
    {
        Should.Throw<SecurityException>(() => SecurityContextFactory.CreateFromHttpContext(Http(User("acme"), "other")));
        Should.Throw<SecurityException>(() => SecurityContextFactory.CreateFromHttpContext(Http(User(null), "other")));
        SecurityContextFactory.CreateFromHttpContext(Http(User("acme"), "ACME")).TenantId.Value.ShouldBe("acme");
    }

    [Fact]
    public void ClusterAdmin_Header_Selects_Tenant_And_Never_A_Reserved_One()
    {
        var admin = User(null, ["ClusterAdmin"]);

        var selected = SecurityContextFactory.CreateFromHttpContext(Http(admin, "tenant-x"));
        selected.TenantId.Value.ShouldBe("tenant-x");
        selected.TenantSelectedByHeader.ShouldBeTrue();
        SecurityContextFactory.CreateFromHttpContext(Http(admin)).ResolvedTenant.ShouldBeNull();
        Should.Throw<ArgumentException>(() => SecurityContextFactory.CreateFromHttpContext(Http(admin, "_platform")));
        Should.Throw<ArgumentException>(() => SecurityContextFactory.CreateFromHttpContext(Http(admin, "_x")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tenant-x")]
    [InlineData("_platform")]
    [InlineData("bad tenant!!")]
    public void Unauthenticated_Principal_Can_Never_Select_A_Tenant_By_Header(string? header)
    {
        var context = SecurityContextFactory.CreateFromHttpContext(Http(new ClaimsPrincipal(new ClaimsIdentity()), header));

        context.IsAuthenticated.ShouldBeFalse();
        context.ResolvedTenant.ShouldBeNull();
        context.TenantSelectedByHeader.ShouldBeFalse();
    }

    // ---- middleware gate (S2, S6, S8, DT-SC-10) ----

    private static async Task<(int Status, string Body, bool NextCalled, HttpContext Context)> RunGateAsync(
        ClaimsPrincipal user, bool allowAnonymousEndpoint = false, string? header = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<GatewayOptions>>(new Autheris.Tests.Common.StaticOptionsMonitor<GatewayOptions>(new GatewayOptions()))
            .BuildServiceProvider();
        var ctx = Http(user, header);
        ctx.RequestServices = services;
        ctx.Response.Body = new MemoryStream();
        if (allowAnonymousEndpoint)
        {
            ctx.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new AllowAnonymousAttribute()), "anon"));
        }

        var nextCalled = false;
        await new SecurityContextResolutionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }).InvokeAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync(), nextCalled, ctx);
    }

    [Fact]
    public async Task S6_Claimless_Non_ClusterAdmin_Gets_403_TenantUnresolved()
    {
        var (status, body, next, _) = await RunGateAsync(User(null));

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("TENANT_UNRESOLVED");
        next.ShouldBeFalse();
    }

    [Fact]
    public async Task S2_Invalid_Claim_Gets_403_TenantClaimInvalid_Without_Echo()
    {
        var (status, body, next, _) = await RunGateAsync(User("bad tenant!!"));

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("TENANT_CLAIM_INVALID");
        body.ShouldNotContain("bad tenant");
        next.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DT_SC_02_ClusterAdmin_With_Invalid_Claim_Gets_403_TenantClaimInvalid()
    {
        var (status, body, next, _) = await RunGateAsync(User("bad tenant!!", ["ClusterAdmin"]));

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("TENANT_CLAIM_INVALID");
        body.ShouldNotContain("bad tenant");
        next.ShouldBeFalse();
    }

    [Fact]
    public async Task ClusterAdmin_Claimless_Passes_The_Gate_Without_A_Tenant()
    {
        var (status, _, next, ctx) = await RunGateAsync(User(null, ["ClusterAdmin"]));

        status.ShouldBe(StatusCodes.Status200OK);
        next.ShouldBeTrue();
        ctx.Items.ContainsKey(SecurityPrincipalContext.TenantIdItemKey).ShouldBeFalse();
        ((SecurityPrincipalContext)ctx.Items[SecurityPrincipalContext.ItemKey]!).ResolvedTenant.ShouldBeNull();
    }

    [Fact]
    public async Task DT_SC_10_AllowAnonymous_Endpoint_Never_Reads_The_Tenant()
    {
        var (status, _, next, ctx) = await RunGateAsync(User(null), allowAnonymousEndpoint: true);
        status.ShouldBe(StatusCodes.Status200OK);
        next.ShouldBeTrue();
        ctx.Items.ContainsKey(SecurityPrincipalContext.TenantIdItemKey).ShouldBeFalse();

        var invalid = await RunGateAsync(User("bad tenant!!"), allowAnonymousEndpoint: true);
        invalid.Status.ShouldBe(StatusCodes.Status200OK);
        invalid.NextCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task S8_Unauthenticated_Reaches_Next_Without_Tenant_The_Authorization_Layer_Answers_401()
    {
        var (status, _, next, ctx) = await RunGateAsync(new ClaimsPrincipal(new ClaimsIdentity()), header: "tenant-x");

        status.ShouldBe(StatusCodes.Status200OK);
        next.ShouldBeTrue();
        ctx.Items.ContainsKey(SecurityPrincipalContext.TenantIdItemKey).ShouldBeFalse();
    }

    [Fact]
    public async Task S1_Resolved_Tenant_Sets_The_Single_Item_Key()
    {
        var (status, _, next, ctx) = await RunGateAsync(User("acme"));

        status.ShouldBe(StatusCodes.Status200OK);
        next.ShouldBeTrue();
        ctx.Items[SecurityPrincipalContext.TenantIdItemKey].ShouldBe(new TenantId("acme"));
    }

    // ---- GatewayExecutionService (DT-A-12 / DT-SC-09, O-4) ----

    private static GatewayExecutionService Execution(GatewayOptions options)
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        return new GatewayExecutionService(
            metadata,
            Substitute.For<IConsentRepository>(),
            Substitute.For<IAuditLogRepository>(),
            Substitute.For<IConsentResolutionService>(),
            Substitute.For<IConsentCacheService>(),
            Substitute.For<IColumnMaskingProvider>(),
            options: Options.Create(options),
            policyEnforcementService: global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance);
    }

    private static readonly TableIdentifier Table = new("sales", "dbo", "orders");

    private static Dictionary<string, string[]> Header(string value) => new() { ["X-Tenant-ID"] = [value] };

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "tenant-x")]
    [InlineData(true, "tenant-x")]
    [InlineData(true, "_platform")]
    [InlineData(true, "bad tenant!!")]
    public async Task Execution_Unauthenticated_Header_Cannot_Select_Tenant(bool anonymousSwitch, string? header)
    {
        var service = Execution(new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_allow_anonymous_access = anonymousSwitch } });
        var headers = header is null ? null : Header(header);

        if (!anonymousSwitch)
        {
            await Should.ThrowAsync<GatewayUnauthorizedException>(() =>
                service.ResolveTableAccessAsync(new ClaimsPrincipal(new ClaimsIdentity()), Table, null, headers));
            return;
        }

        // The dev-anonymous principal has no default tenant here (S6): unresolved, never the header tenant.
        await Should.ThrowAsync<TenantUnresolvedException>(() =>
            service.ResolveTableAccessAsync(new ClaimsPrincipal(new ClaimsIdentity()), Table, null, headers));
    }

    [Fact]
    public async Task DevAnonymous_Header_Differing_Tenant_Rejected()
    {
        var options = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_allow_anonymous_access = true }, Tenancy = new TenancyOptions { DefaultTenantId = Default } };
        var service = Execution(options);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            service.ResolveTableAccessAsync(new ClaimsPrincipal(new ClaimsIdentity()), Table, null, Header("tenant-x")));
        ex.ErrorCode.ShouldBe("CROSS_TENANT_ACCESS_FORBIDDEN");
        // The default tenant itself is accepted (S3): execution proceeds to the metadata lookup.
        await Should.ThrowAsync<TableNotFoundException>(() =>
            service.ResolveTableAccessAsync(new ClaimsPrincipal(new ClaimsIdentity()), Table, null, Header(Default)));
    }

    [Fact]
    public async Task Execution_NonClusterAdmin_Header_Differing_Tenant_Forbidden_ClusterAdmin_Selects()
    {
        var service = Execution(new GatewayOptions());

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            service.ResolveTableAccessAsync(User("acme"), Table, null, Header("tenant-x")));
        ex.ErrorCode.ShouldBe("CROSS_TENANT_ACCESS_FORBIDDEN");
        await Should.ThrowAsync<TableNotFoundException>(() =>
            service.ResolveTableAccessAsync(User("acme", ["ClusterAdmin"]), Table, null, Header("tenant-x")));
        // A tenant-less ClusterAdmin needs the header for a tenant-scoped operation.
        await Should.ThrowAsync<TenantUnresolvedException>(() =>
            service.ResolveTableAccessAsync(User(null, ["ClusterAdmin"]), Table, null, null));
        await Should.ThrowAsync<TableNotFoundException>(() =>
            service.ResolveTableAccessAsync(User(null, ["ClusterAdmin"]), Table, null, Header("tenant-x")));
    }

    // ---- ext_authz authenticated path ----

    [Fact]
    public async Task ExtAuthz_Authenticated_Claimless_Caller_Is_Denied_Not_Given_A_Tenant()
    {
        var service = new EnvoyExtAuthzService(Substitute.For<IPolicyEnforcementService>(), NullLogger<EnvoyExtAuthzService>.Instance);

        var denied = await service.CheckHttpAsync("GET", "/api/v1/x", new Dictionary<string, string>(), User(null));
        var invalid = await service.CheckHttpAsync("GET", "/api/v1/x", new Dictionary<string, string>(), User("bad tenant!!"));

        denied.HttpResponse.DeniedResponse.ShouldNotBeNull();
        denied.HttpResponse.DeniedResponse!.Status.Code.ShouldBe(403);
        invalid.HttpResponse.DeniedResponse.ShouldNotBeNull();
    }

    // ---- TestAuth is a test-host-only ingress (DT-SC-09) ----

    [Fact]
    public async Task TestAuth_Outside_Development_Rejects_And_Ignores_Header_Tenant()
    {
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var optionsMonitor = Substitute.For<IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions());
        var handler = new TestAuthHandler(optionsMonitor, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, System.Text.Encodings.Web.UrlEncoder.Default, null, env);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Test-User-Sid"] = "S-1-5-21-test";
        context.Request.Headers["X-Test-Tenant"] = "tenant-x";
        context.Request.Headers["X-Tenant-ID"] = "tenant-x";
        await handler.InitializeAsync(new Microsoft.AspNetCore.Authentication.AuthenticationScheme("TestAuth", null, typeof(TestAuthHandler)), context);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse();
        result.Principal.ShouldBeNull();
    }

    // ---- consent request tenant resolution (no sentinel) ----

    [Fact]
    public void ConsentRequest_ResolveTenantId_Uses_Context_Then_Claim_And_Never_A_Legacy_Sentinel()
    {
        var context = new TenantId("ctx");
        Autheris.Application.ConsentRequests.ConsentRequestService.ResolveTenantId(User("ctx"), context, false).ShouldBe(context);
        Autheris.Application.ConsentRequests.ConsentRequestService.ResolveTenantId(User("own"), default, false).Value.ShouldBe("own");
        Should.Throw<Exception>(() => Autheris.Application.ConsentRequests.ConsentRequestService.ResolveTenantId(User("own"), context, false));
        Should.Throw<TenantUnresolvedException>(() => Autheris.Application.ConsentRequests.ConsentRequestService.ResolveTenantId(User(null), default, false));
        Autheris.Application.ConsentRequests.ConsentRequestService.ResolveTenantId(User("own"), context, true).ShouldBe(context);
    }

    // ---- DT-SC-04 (request-path part): the default TenantId never matches ----

    [Fact]
    public void Default_TenantId_Struct_Is_Never_A_Tenant()
    {
        var unset = default(TenantId);

        Should.Throw<TenantUnresolvedException>(() => new TenantResolution(TenantResolutionOutcome.FromClaim, unset).RequireTenant());
        var context = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1"), GroupSids = new HashSet<Sid>(), TenantRoles = new HashSet<string>(), ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "x", ResolvedTenant = unset
        };
        Should.Throw<TenantUnresolvedException>(() => context.TenantId);
    }
}
