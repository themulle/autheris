namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Interfaces;
using Autheris.Application.ResourceGroups;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security review 2026-10-02, work package D1 (hosting, authentication, middleware, GraphQL server).
/// </summary>
public sealed class SecurityReview20261002HostingTests
{
    private static readonly Func<string, string?> NoEnvironmentVariables = _ => null;

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static DataMaskingOptions ProdMasking() => new() { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" };

    private static ClaimsPrincipal User(string sid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new(ClaimTypes.Name, sid),
            new(ClaimTypes.NameIdentifier, sid)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    // =========================================================================
    // C-04: Container defaults to Production, Development in container needs opt-in
    // =========================================================================

    [Fact]
    public void C04_DevelopmentInContainer_WithoutOptIn_AbortsStartup()
    {
        var options = new GatewayOptions();
        Func<string, string?> env = name => name == "DOTNET_RUNNING_IN_CONTAINER" ? "true" : null;

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Development), env));

        ex.Message.ShouldContain("AllowDevelopmentInContainer");
    }

    [Fact]
    public void C04_DevelopmentInContainer_WithConfigOptIn_IsAllowed()
    {
        var options = new GatewayOptions { AllowDevelopmentInContainer = true };
        Func<string, string?> env = name => name == "DOTNET_RUNNING_IN_CONTAINER" ? "true" : null;

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Development), env));
    }

    [Fact]
    public void C04_DevelopmentInContainer_WithEnvironmentVariableOptIn_IsAllowed()
    {
        var options = new GatewayOptions();
        Func<string, string?> env = name => name switch
        {
            "DOTNET_RUNNING_IN_CONTAINER" => "true",
            "AUTHERIS_ALLOW_DEV_IN_CONTAINER" => "true",
            _ => null
        };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Development), env));
    }

    [Fact]
    public void C04_DevelopmentOutsideContainer_IsAllowed()
    {
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(new GatewayOptions(), Env(Environments.Development), NoEnvironmentVariables));
    }

    [Fact]
    public void C04_Dockerfile_DefaultsToProduction_AndBaseComposeIsProduction()
    {
        var root = FindRepoRoot();
        root.ShouldNotBeNull("Repository root (Autheris.sln) not found");

        var dockerfile = File.ReadAllText(Path.Combine(root!, "Dockerfile"));
        dockerfile.ShouldContain("ASPNETCORE_ENVIRONMENT=Production");
        dockerfile.ShouldNotContain("ASPNETCORE_ENVIRONMENT=Development");

        var compose = File.ReadAllText(Path.Combine(root!, "docker-compose.yml"));
        compose.ShouldContain("ASPNETCORE_ENVIRONMENT=Production");
        compose.ShouldNotContain("ASPNETCORE_ENVIRONMENT=Development");

        var composeDev = File.ReadAllText(Path.Combine(root!, "docker-compose.dev.yml"));
        composeDev.ShouldContain("ASPNETCORE_ENVIRONMENT=Development");
        composeDev.ShouldContain("AUTHERIS_ALLOW_DEV_IN_CONTAINER=true");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Autheris.sln")) &&
                File.Exists(Path.Combine(dir.FullName, "Dockerfile")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    // =========================================================================
    // M-01: Kestrel limits
    // =========================================================================

    [Fact]
    public void M01_HostingLimits_DefaultsAreRestrictive()
    {
        var limits = new GatewayOptions().Hosting;
        limits.MaxRequestBodySizeBytes.ShouldBe(2 * 1024 * 1024);
        limits.MaxConcurrentUpgradedConnections.ShouldBe(1000);
    }

    // =========================================================================
    // H-02: OpenSchema is a DANGER bypass and no longer opens GraphQL / introspection
    // =========================================================================

    [Fact]
    public void H02_OpenSchema_IsListedAsDangerBypass()
    {
        var options = new GatewayOptions { OpenSchema = true };

        options.HasAnySecurityBypassActive.ShouldBeTrue();
        options.GetAllActiveBypasses().ShouldContain(b => b.StartsWith("DANGER:open_schema", StringComparison.Ordinal));
    }

    [Fact]
    public void H02_CatalogOpenSchema_IsListedAsDangerBypass()
    {
        var options = new GatewayOptions { Catalog = new DataCatalogOptions { OpenSchema = true } };

        options.GetAllActiveBypasses().ShouldContain(b => b.StartsWith("DANGER:open_schema", StringComparison.Ordinal));
    }

    [Fact]
    public void H02_OpenSchema_InProduction_AbortsStartup()
    {
        var options = new GatewayOptions { OpenSchema = true, DataMasking = ProdMasking() };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));

        ex.Message.ShouldContain("open_schema");
    }

    [Fact]
    public void H02_OpenSchema_InDevelopment_IsAllowed()
    {
        var options = new GatewayOptions { OpenSchema = true };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Development), NoEnvironmentVariables));
    }

    [Fact]
    public void H02_DefaultProductionConfiguration_HasNoBypass()
    {
        var options = new GatewayOptions { DataMasking = ProdMasking() };

        options.GetAllActiveBypasses().ShouldBeEmpty();
        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    // =========================================================================
    // M-02: JWT fail-closed
    // =========================================================================

    [Fact]
    public void M02_EntraIdWithoutAudienceAndClientId_InProduction_AbortsStartup()
    {
        var options = new GatewayOptions
        {
            DataMasking = ProdMasking(),
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "contoso-tenant" }
            }
        };

        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void M02_EntraIdWithoutTenantId_InProduction_AbortsStartup()
    {
        var options = new GatewayOptions
        {
            DataMasking = ProdMasking(),
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = true, Audience = "api://gateway" }
            }
        };

        Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void M02_EntraIdWithClientIdAndTenant_InProduction_IsAllowed()
    {
        var options = new GatewayOptions
        {
            DataMasking = ProdMasking(),
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "contoso-tenant", ClientId = "11111111-2222-3333-4444-555555555555" }
            }
        };

        Should.NotThrow(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Production), NoEnvironmentVariables));
    }

    [Fact]
    public void M02_AddGatewayAuth_EntraWithoutAudience_StillValidatesAudience()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                EntraId = new EntraIdAuthOptions { Enabled = true, TenantId = "contoso-tenant" }
            }
        };

        services.AddGatewayAuth(options, Env(Environments.Development));
        using var sp = services.BuildServiceProvider();

        var jwt = sp.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(GatewayAuthSchemes.JwtBearer);

        jwt.TokenValidationParameters.ValidateAudience.ShouldBeTrue();
        jwt.TokenValidationParameters.ValidAudiences.ShouldBeNull();
        jwt.TokenValidationParameters.ValidateIssuer.ShouldBeTrue();
    }

    // =========================================================================
    // M-03: FallbackPolicy and named policies
    // =========================================================================

    [Fact]
    public void M03_FallbackPolicy_RequiresAuthenticatedUser_AndNamedPoliciesExist()
    {
        var authz = new AuthorizationOptions();
        GatewayPolicies.Configure(authz);

        authz.FallbackPolicy.ShouldNotBeNull();
        authz.FallbackPolicy!.Requirements.ShouldContain(r => r is DenyAnonymousAuthorizationRequirement);

        authz.GetPolicy(GatewayPolicies.GovernanceAdmin).ShouldNotBeNull();
        authz.GetPolicy(GatewayPolicies.Approver).ShouldNotBeNull();
        authz.GetPolicy(GatewayPolicies.ClusterAdmin).ShouldNotBeNull();
        authz.GetPolicy(GatewayPolicies.PrivacyAdmin).ShouldNotBeNull();
        authz.GetPolicy(GatewayPolicies.SchemaAdmin).ShouldNotBeNull();
    }

    [Fact]
    public async Task M03_NamedPolicies_EnforceRoles()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(GatewayPolicies.Configure);
        using var sp = services.BuildServiceProvider();
        var authz = sp.GetRequiredService<IAuthorizationService>();

        var developer = User("S-1-5-21-DEV", "Developer");
        var steward = User("S-1-5-21-STEWARD", "DataSteward");
        var clusterAdmin = User("S-1-5-21-ADMIN", "ClusterAdmin");
        var anonymousWithRoleClaim = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "ClusterAdmin")]));

        (await authz.AuthorizeAsync(developer, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(developer, GatewayPolicies.Approver)).Succeeded.ShouldBeFalse();
        (await authz.AuthorizeAsync(anonymousWithRoleClaim, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeFalse();

        (await authz.AuthorizeAsync(steward, GatewayPolicies.Approver)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(clusterAdmin, GatewayPolicies.ClusterAdmin)).Succeeded.ShouldBeTrue();
        (await authz.AuthorizeAsync(clusterAdmin, GatewayPolicies.GovernanceAdmin)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void M03_HasAnyRole_AcceptsRawRolesClaim()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "PrivacyAdmin")], "Bearer"));

        GatewayPolicies.HasAnyRole(principal, GatewayPolicies.PrivacyAdminRoles).ShouldBeTrue();
        GatewayPolicies.HasAnyRole(principal, GatewayPolicies.ClusterAdminRoles).ShouldBeFalse();
        GatewayPolicies.HasAnyRole(null, GatewayPolicies.ClusterAdminRoles).ShouldBeFalse();
    }

    // =========================================================================
    // H-08: PersistedQueriesOnly enforced with a trusted document store
    // =========================================================================

    [Fact]
    public void H08_PersistedQueriesOnly_WithoutTrustedDocumentsDirectory_FailsFast()
    {
        var options = new GatewayOptions { GraphQL = new GraphQLOptions { PersistedQueriesOnly = true } };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayServiceCollectionExtensions.ValidateGatewayOptions(options, Env(Environments.Development), NoEnvironmentVariables));

        ex.Message.ShouldContain("TrustedDocumentsDirectory");
    }

    [Fact]
    public void H08_TrustedDocumentStore_AcceptsOnlyAllowlistedDocuments()
    {
        var store = new TrustedDocumentStore(["query GetOrders { orders { id total } }"]);

        // Positive: same operation, different whitespace/formatting
        store.IsTrusted("query GetOrders{orders{id total}}").ShouldBeTrue();

        // Negative: attacker adds a field / sends an arbitrary query
        store.IsTrusted("query GetOrders { orders { id total customerEmail } }").ShouldBeFalse();
        store.IsTrusted("{ __schema { types { name } } }").ShouldBeFalse();
        store.IsTrusted("this is not graphql {{{").ShouldBeFalse();
    }

    [Fact]
    public void H08_TrustedDocumentStore_LoadFromDirectory_EmptyDirectory_FailsFast()
    {
        var dir = Directory.CreateTempSubdirectory("gql-trusted-docs-").FullName;
        try
        {
            Should.Throw<InvalidOperationException>(() => TrustedDocumentStore.LoadFromDirectory(dir));

            File.WriteAllText(Path.Combine(dir, "orders.graphql"), "query GetOrders { orders { id } }");
            var store = TrustedDocumentStore.LoadFromDirectory(dir);
            store.Count.ShouldBe(1);
            store.IsTrusted("query GetOrders { orders { id } }").ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // =========================================================================
    // H-07: Persistent connections do not occupy slots, limited per SID / tenant
    // =========================================================================

    [Fact]
    public void H07_PersistentConnectionLimiter_EnforcesPerPrincipalAndPerTenantLimits()
    {
        var limiter = new PersistentConnectionLimiter(new ResourceGroupsOptions
        {
            MaxPersistentConnectionsPerPrincipal = 2,
            MaxPersistentConnectionsPerTenant = 3
        });

        var a1 = limiter.TryAcquire("sid:A", "t1");
        var a2 = limiter.TryAcquire("sid:A", "t1");
        a1.ShouldNotBeNull();
        a2.ShouldNotBeNull();

        // Negative: third connection of the same principal is rejected
        limiter.TryAcquire("sid:A", "t1").ShouldBeNull();

        var b1 = limiter.TryAcquire("sid:B", "t1");
        b1.ShouldNotBeNull();

        // Negative: tenant limit (3) reached for a fresh principal
        limiter.TryAcquire("sid:C", "t1").ShouldBeNull();

        // Other tenants are unaffected
        using var other = limiter.TryAcquire("sid:C", "t2");
        other.ShouldNotBeNull();

        // Releasing frees the slot again
        a1!.Dispose();
        a1.Dispose(); // idempotent
        limiter.GetPrincipalCount("sid:A").ShouldBe(1);
        using var a3 = limiter.TryAcquire("sid:A", "t1");
        a3.ShouldNotBeNull();

        a2!.Dispose();
        b1!.Dispose();
    }

    [Fact]
    public async Task H07_ResourceGroupMiddleware_SseRequest_BypassesSaturatedSlots()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                Interactive = new ResourceGroupTierConfigOptions(MaxConcurrency: 1, MaxQueueDepth: 0, TimeoutSeconds: 1)
            }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);
        var slot = await manager.TryAcquireLeaseAsync(ResourceGroupTier.Interactive, "t1");
        slot.Success.ShouldBeTrue();

        var nextInvoked = false;
        var middleware = new ResourceGroupMiddleware(
            _ => { nextInvoked = true; return Task.CompletedTask; },
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/graphql";
        httpContext.Request.Headers.Accept = "text/event-stream";
        httpContext.User = User("S-1-5-21-SSE");
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        nextInvoked.ShouldBeTrue();
        httpContext.Response.StatusCode.ShouldNotBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task H07_ResourceGroupMiddleware_TooManyPersistentConnections_Returns429()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Enabled = true,
                MaxPersistentConnectionsPerPrincipal = 1
            }
        });
        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);
        var limiter = new PersistentConnectionLimiter(options.Value.ResourceGroups);

        // An already open SSE stream of the same principal/tenant
        using var openStream = limiter.TryAcquire("sid:S-1-5-21-SSE", "tenant-a");
        openStream.ShouldNotBeNull();

        var nextInvoked = false;
        var middleware = new ResourceGroupMiddleware(
            _ => { nextInvoked = true; return Task.CompletedTask; },
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance,
            limiter);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "GET";
        httpContext.Request.Path = "/mcp/sse";
        httpContext.Items[TenantResolutionMiddleware.TenantIdItemKey] = new TenantId("tenant-a");
        httpContext.User = User("S-1-5-21-SSE");
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        nextInvoked.ShouldBeFalse();
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public void H07_ResolveTenantKey_UsesTenantIdObjectFromItems()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[TenantResolutionMiddleware.TenantIdItemKey] = new TenantId("tenant-a");

        ResourceGroupMiddleware.ResolveTenantKey(httpContext).ShouldBe("tenant-a");
    }

    [Fact]
    public void H07_IsPersistentConnectionRequest_ClassifiesRequests()
    {
        var ws = new DefaultHttpContext();
        ws.Request.Headers.Upgrade = "websocket";
        ResourceGroupMiddleware.IsPersistentConnectionRequest(ws).ShouldBeTrue();

        var mcpToolCall = new DefaultHttpContext();
        mcpToolCall.Request.Method = "POST";
        mcpToolCall.Request.Path = "/mcp";
        mcpToolCall.Request.Headers.Accept = "application/json, text/event-stream";
        ResourceGroupMiddleware.IsPersistentConnectionRequest(mcpToolCall).ShouldBeFalse();

        var query = new DefaultHttpContext();
        query.Request.Method = "POST";
        query.Request.Path = "/graphql";
        ResourceGroupMiddleware.IsPersistentConnectionRequest(query).ShouldBeFalse();
    }

    // =========================================================================
    // M-04: Bounded response buffering
    // =========================================================================

    [Fact]
    public void M04_BoundedStream_ExceedingLimit_SwitchesToPassThrough_WithoutCollectingEverything()
    {
        using var inner = new MemoryStream();
        using var bounded = new BoundedResponseBufferStream(inner, maxBufferBytes: 10);

        bounded.Write(new byte[6], 0, 6);
        bounded.IsPassThrough.ShouldBeFalse();
        inner.Length.ShouldBe(0);

        bounded.Write(new byte[6], 0, 6);
        bounded.IsPassThrough.ShouldBeTrue();
        bounded.BufferedLength.ShouldBe(0);
        inner.Length.ShouldBe(12);

        bounded.Write(new byte[100], 0, 100);
        bounded.BufferedLength.ShouldBe(0);
        inner.Length.ShouldBe(112);
    }

    [Fact]
    public async Task M04_BoundedStream_EventStreamContentType_IsNeverBuffered()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.ContentType = "text/event-stream";
        using var inner = new MemoryStream();
        await using var bounded = new BoundedResponseBufferStream(inner, 1024 * 1024, httpContext.Response);

        await bounded.WriteAsync(Encoding.UTF8.GetBytes("data: 1\n\n"));

        bounded.IsPassThrough.ShouldBeTrue();
        inner.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task M04_GatewayExtensibilityMiddleware_SseRequest_IsNotBuffered()
    {
        var options = Options.Create(new GatewayOptions());
        var pipeline = new ExtensibilityPipeline([], [], NullLogger<ExtensibilityPipeline>.Instance);
        Type? observedBodyType = null;

        var middleware = new GatewayExtensibilityMiddleware(
            ctx =>
            {
                observedBodyType = ctx.Response.Body.GetType();
                return Task.CompletedTask;
            },
            pipeline,
            options,
            NullLogger<GatewayExtensibilityMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/graphql";
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers.Accept = "text/event-stream";
        var originalBody = new MemoryStream();
        httpContext.Response.Body = originalBody;

        await middleware.InvokeAsync(httpContext);

        observedBodyType.ShouldBe(typeof(MemoryStream));
        httpContext.Response.Body.ShouldBeSameAs(originalBody);
    }

    [Fact]
    public async Task M04_GatewayExtensibilityMiddleware_RegularResponse_IsStillProcessed()
    {
        var options = Options.Create(new GatewayOptions());
        var pipeline = new ExtensibilityPipeline([], [], NullLogger<ExtensibilityPipeline>.Instance);

        var middleware = new GatewayExtensibilityMiddleware(
            async ctx =>
            {
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("{\"data\":{}}");
            },
            pipeline,
            options,
            NullLogger<GatewayExtensibilityMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/graphql";
        httpContext.Request.Method = "POST";
        var originalBody = new MemoryStream();
        httpContext.Response.Body = originalBody;

        await middleware.InvokeAsync(httpContext);

        Encoding.UTF8.GetString(originalBody.ToArray()).ShouldBe("{\"data\":{}}");
        httpContext.Response.Body.ShouldBeSameAs(originalBody);
    }

    // =========================================================================
    // M-05: MCP requires application/json
    // =========================================================================

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/x-www-form-urlencoded", false)]
    [InlineData("multipart/form-data; boundary=x", false)]
    [InlineData(null, false)]
    public void M05_McpContentTypeCheck_OnlyAcceptsJson(string? contentType, bool expected)
    {
        GatewayApplicationBuilderExtensions.IsJsonContentType(contentType).ShouldBe(expected);
    }

    [Fact]
    public void M05_McpBasePath_IsNormalized()
    {
        GatewayApplicationBuilderExtensions.ResolveMcpBasePath(new GatewayOptions()).ShouldBe("/mcp");
    }

    // =========================================================================
    // M-06: Break-glass hardening
    // =========================================================================

    [Fact]
    public void M06_RequireRoleForBreakGlass_DefaultsToTrue()
    {
        new GatewayOptions().Extensibility.RequireRoleForBreakGlass.ShouldBeTrue();
    }

    [Fact]
    public async Task M06_AnonymousBreakGlass_IsDenied_AndNotAudited()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        var interceptor = new JustificationAndBreakGlassInterceptor(
            Options.Create(new GatewayOptions
            {
                Extensibility = new ExtensibilityOptions { RequireRoleForBreakGlass = false }
            }),
            NullLogger<JustificationAndBreakGlassInterceptor>.Instance,
            repo);

        var context = new IngressContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Break-Glass"] = "true",
                ["X-Access-Justification"] = "INC-12345",
                ["X-Forwarded-For"] = "6.6.6.6"
            }
        };

        var result = await interceptor.OnIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Deny);
        result.StatusCode.ShouldBe(401);
        context.Items.ContainsKey("IsBreakGlass").ShouldBeFalse();
        await repo.DidNotReceive().RecordAuditEventAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task M06_AuthenticatedWithoutBreakGlassRole_IsDeniedByDefault()
    {
        var interceptor = new JustificationAndBreakGlassInterceptor(
            Options.Create(new GatewayOptions()),
            NullLogger<JustificationAndBreakGlassInterceptor>.Instance);

        var context = new IngressContext
        {
            User = User("S-1-5-21-DEV", "Developer"),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Break-Glass"] = "true",
                ["X-Access-Justification"] = "INC-12345"
            }
        };

        var result = await interceptor.OnIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Deny);
        result.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task M06_BreakGlassAudit_UsesConnectionIp_NotXForwardedForHeader()
    {
        var repo = Substitute.For<IGovernanceRepository>();
        AuditLogEntry? recorded = null;
        repo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recorded = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var interceptor = new JustificationAndBreakGlassInterceptor(
            Options.Create(new GatewayOptions()),
            NullLogger<JustificationAndBreakGlassInterceptor>.Instance,
            repo);

        var context = new IngressContext
        {
            User = User("S-1-5-21-OPERATOR", "BreakGlassOperator"),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Break-Glass"] = "true",
                ["X-Access-Justification"] = "INC-12345",
                ["X-Forwarded-For"] = "6.6.6.6"
            }
        };
        context.Items[JustificationAndBreakGlassInterceptor.ClientIpItemKey] = "10.1.2.3";

        var result = await interceptor.OnIngressAsync(context);

        result.Decision.ShouldBe(IngressDecision.Continue);
        recorded.ShouldNotBeNull();
        recorded!.DetailsJson.ShouldContain("10.1.2.3");
        recorded.DetailsJson.ShouldNotContain("6.6.6.6");
    }

    [Fact]
    public async Task M06_GatewayExtensibilityMiddleware_PassesConnectionIpToIngress()
    {
        var options = Options.Create(new GatewayOptions());
        var capture = new CapturingInterceptor();
        var pipeline = new ExtensibilityPipeline([capture], [], NullLogger<ExtensibilityPipeline>.Instance);

        var middleware = new GatewayExtensibilityMiddleware(
            _ => Task.CompletedTask,
            pipeline,
            options,
            NullLogger<GatewayExtensibilityMiddleware>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/test";
        httpContext.Request.Headers["X-Forwarded-For"] = "6.6.6.6";
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("10.9.8.7");
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        capture.ClientIp.ShouldBe("10.9.8.7");
        GatewayExtensibilityMiddleware.ClientIpItemKey.ShouldBe(JustificationAndBreakGlassInterceptor.ClientIpItemKey);
    }

    private sealed class CapturingInterceptor : IIngressInterceptor
    {
        public int Order => 1;
        public string? ClientIp { get; private set; }

        public ValueTask<IngressResult> OnIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
        {
            ClientIp = context.Items.TryGetValue(GatewayExtensibilityMiddleware.ClientIpItemKey, out var ip) ? ip as string : null;
            return ValueTask.FromResult(IngressResult.Continue());
        }
    }

    // =========================================================================
    // M-07: Body limit helper (chunked transfer cannot bypass the limit)
    // =========================================================================

    [Fact]
    public async Task M07_ReadBody_ChunkedRequestOverLimit_Throws413()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(new byte[5000]);
        httpContext.Request.ContentLength = null; // chunked: no Content-Length

        var ex = await Should.ThrowAsync<BadHttpRequestException>(() =>
            httpContext.Request.ReadBodyAsStringAsync(1000, CancellationToken.None));

        ex.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task M07_ReadBody_DeclaredContentLengthOverLimit_Throws413BeforeReading()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(new byte[10]);
        httpContext.Request.ContentLength = 50_000;

        var ex = await Should.ThrowAsync<BadHttpRequestException>(() =>
            httpContext.Request.ReadBodyAsStringAsync(1000, CancellationToken.None));

        ex.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        httpContext.Request.Body.Position.ShouldBe(0);
    }

    [Fact]
    public async Task M07_ReadBody_WithinLimit_ReturnsText_AndTightensServerLimit()
    {
        var feature = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = 100 * 1024 * 1024 };
        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ok\":true}"));

        var body = await httpContext.Request.ReadBodyAsStringAsync(1000, CancellationToken.None);

        body.ShouldBe("{\"ok\":true}");
        feature.MaxRequestBodySize.ShouldBe(1000);
    }

    private sealed class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    // =========================================================================
    // M-08: Rate limiter key (/64) and eviction instead of global lockout
    // =========================================================================

    [Fact]
    public void M08_Ipv6Key_IsAggregatedTo64_AndIpv4MappedIsNormalized()
    {
        ClientIpRateLimitKey.Normalize("2001:db8:1:2:aaaa::1")
            .ShouldBe(ClientIpRateLimitKey.Normalize("2001:db8:1:2:bbbb:cccc:dddd:2"));
        ClientIpRateLimitKey.Normalize("2001:db8:1:2::1")
            .ShouldNotBe(ClientIpRateLimitKey.Normalize("2001:db8:1:3::1"));
        ClientIpRateLimitKey.Normalize("::ffff:192.168.1.10").ShouldBe("192.168.1.10");
        ClientIpRateLimitKey.Normalize("192.168.1.10").ShouldBe("192.168.1.10");
    }

    [Fact]
    public async Task M08_Ipv6RotationWithinSame64_IsRateLimited()
    {
        var limiter = new InMemoryRateLimiterService();
        var options = new PreAuthIpRateLimitOptions { PermitLimit = 3, WindowSeconds = 60 };

        for (var i = 1; i <= 3; i++)
        {
            (await limiter.CheckPreAuthIpAsync($"2001:db8:1:2::{i:x}", options)).Allowed.ShouldBeTrue();
        }

        (await limiter.CheckPreAuthIpAsync("2001:db8:1:2::ffff", options)).Allowed.ShouldBeFalse();

        // A different /64 is an independent client
        (await limiter.CheckPreAuthIpAsync("2001:db8:1:3::1", options)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task M08_FullTable_EvictsStaleEntries_InsteadOfBlockingNewClients()
    {
        var limiter = new InMemoryRateLimiterService();
        var options = new PreAuthIpRateLimitOptions { PermitLimit = 100, WindowSeconds = 60 };

        for (var i = 0; i < InMemoryRateLimiterService.MaxEntries; i++)
        {
            await limiter.CheckPreAuthIpAsync($"10.{(i >> 16) & 0xFF}.{(i >> 8) & 0xFF}.{i & 0xFF}", options);
        }

        limiter.TrackedIpCount.ShouldBe(InMemoryRateLimiterService.MaxEntries);

        var fresh = await limiter.CheckPreAuthIpAsync("172.16.0.1", options);

        fresh.Allowed.ShouldBeTrue();
        limiter.TrackedIpCount.ShouldBeLessThanOrEqualTo(InMemoryRateLimiterService.MaxEntries);
    }
}
