namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.OData;
using Autheris.Application.OData.Services;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security tests for Issue #506: Cache and header hardening.
/// Validates global no-store on data responses, metadata cache keys, CDN Vary, visibility keys, and rate-limit bucket isolation.
/// </summary>
[Trait("Category", "Security")]
public sealed class CacheSecurityTests
{
    private static IHostEnvironment CreateMockEnvironment(string environmentName = "Production")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        return env;
    }

    [Theory]
    [InlineData("/api/v1/datasets")]
    [InlineData("/odata/v4/corp/sales/orders")]
    [InlineData("/v1/statement")]
    [InlineData("/api/v1/sql")]
    [InlineData("/mcp")]
    [InlineData("/api/v1/cdc/changes")]
    [InlineData("/api/v1/catalog/datasets")]
    public async Task DataResponses_DefaultToNoStoreAndVaryAuthCookie(string path)
    {
        var options = new GatewayOptions();
        var env = CreateMockEnvironment();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask, options, env);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        context.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        var vary = context.Response.Headers.Vary.ToString();
        vary.ShouldContain("Authorization");
        vary.ShouldContain("Cookie");
    }

    [Theory]
    [InlineData("/ui/swagger/assets/swagger-ui.css")]
    [InlineData("/docs")]
    [InlineData("/swagger")]
    [InlineData("/odata/v4/$swagger")]
    [InlineData("/.well-known/oauth-protected-resource")]
    public async Task PublicStaticDocuments_OptOutFromNoStoreByDefault(string path)
    {
        var options = new GatewayOptions();
        var env = CreateMockEnvironment();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask, options, env);

        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        context.Response.Headers.CacheControl.ToString().ShouldNotBe("no-store");
    }

    [Fact]
    public async Task EndpointWithPublicDocumentMetadata_OptOutFromNoStore()
    {
        var options = new GatewayOptions();
        var env = CreateMockEnvironment();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask, options, env);

        var context = new DefaultHttpContext();
        context.Request.Path = "/custom/public-doc";
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(PublicDocumentMetadata.Instance), "PublicDoc");
        context.SetEndpoint(endpoint);

        await middleware.InvokeAsync(context);

        context.Response.Headers.CacheControl.ToString().ShouldNotBe("no-store");
    }

    [Fact]
    public async Task ODataMetadata_IncludesCatalogVersionInCacheKey_AndShortensTtl()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("corp", "sales", "orders"),
            Table = new Table { SchemaName = "sales", TableName = "orders", SourceName = "corp" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }],
            PrimaryKeyColumns = ["id"]
        };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));

        var exec = Substitute.For<IGatewayExecutionService>();
        exec.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)));

        var versionProvider = Substitute.For<ICatalogVersionProvider>();
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)));

        var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new ODataHandler(repo, exec, NullLogger<ODataHandler>.Instance, memoryCache: cache, versionProvider: versionProvider);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim("tenant_id", "corp")
        ], "TestAuth"));

        // First call with version (1, 1)
        var csdl1 = await handler.GetMetadataCsdlAsync(principal);
        csdl1.ShouldNotBeNull();
        await exec.Received(1).CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>());

        // Second call with same version (1, 1) -> served from cache, no new check
        var csdl2 = await handler.GetMetadataCsdlAsync(principal);
        csdl2.ShouldBe(csdl1);
        await exec.Received(1).CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>());

        // Version bumps to (1, 2) (content change / policy update) -> cache misses
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 2)));

        var csdl3 = await handler.GetMetadataCsdlAsync(principal);
        csdl3.ShouldNotBeNull();
        await exec.Received(2).CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenApiCacheManager_IncludesCatalogVersionInCacheKey()
    {
        var versionProvider = Substitute.For<ICatalogVersionProvider>();
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)));

        var cacheManager = new OpenApiCacheManager(versionProvider: versionProvider);
        int factoryCount = 0;
        Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryCount);
            return Task.FromResult("{\"openapi\": \"3.1.0\"}");
        }

        var bytes1 = await cacheManager.GetOrAddAsync("sales", isYaml: false, Factory);
        var bytes2 = await cacheManager.GetOrAddAsync("sales", isYaml: false, Factory);
        bytes1.ShouldBe(bytes2);
        factoryCount.ShouldBe(1);

        // Catalog version changes -> key changes and factory is invoked again
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(2, 1)));

        var bytes3 = await cacheManager.GetOrAddAsync("sales", isYaml: false, Factory);
        bytes3.ShouldNotBeNull();
        factoryCount.ShouldBe(2);
    }

    [Fact]
    public void CatalogVisibilityCacheKey_DistinguishesTenantsGroupsRolesAndScopes()
    {
        var keyBase = new CatalogVisibilityCacheKey(
            SubjectSid: "S-1-5-21-1",
            DomainFilter: "sales",
            StructureVersion: 1,
            ContentVersion: 1,
            PolicyEpoch: 1,
            RebacGeneration: 1,
            TenantId: "tenant-a",
            Groups: "g1,g2",
            Roles: "Analyst",
            Scopes: "read:data");

        var keyDifferentTenant = keyBase with { TenantId = "tenant-b" };
        var keyDifferentGroups = keyBase with { Groups = "g1,g3" };
        var keyDifferentRoles = keyBase with { Roles = "Admin" };
        var keyDifferentScopes = keyBase with { Scopes = "write:data" };

        keyBase.ShouldNotBe(keyDifferentTenant);
        keyBase.ShouldNotBe(keyDifferentGroups);
        keyBase.ShouldNotBe(keyDifferentRoles);
        keyBase.ShouldNotBe(keyDifferentScopes);

        var cache = new CatalogVisibilityLruCache(ttl: TimeSpan.FromSeconds(30), maxEntries: 100);
        var bitset = new CatalogVisibleBitSet(5);
        bitset.Set(1);

        cache.Set(keyBase, bitset);

        cache.TryGet(keyBase, out var retrieved).ShouldBeTrue();
        retrieved.ShouldNotBeNull();

        cache.TryGet(keyDifferentTenant, out _).ShouldBeFalse();
        cache.TryGet(keyDifferentRoles, out _).ShouldBeFalse();
    }

    [Fact]
    public void CatalogVisibilityCacheKey_FromPrincipal_ExtractsIdentityAttributes()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-42"),
            new Claim("tenant_id", "tenant-alpha"),
            IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-32-544"),
            new Claim(ClaimTypes.Role, "CatalogViewer"),
            new Claim("scp", "catalog.read")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        var key = CatalogVisibilityCacheKey.From(principal, "domain1", 1, 1, 1, 1);

        key.SubjectSid.ShouldBe("S-1-5-21-42");
        key.TenantId.ShouldBe("tenant-alpha");
        key.Groups!.ShouldContain("S-1-5-32-544");
        key.Roles!.ShouldContain("CatalogViewer");
        key.Scopes!.ShouldContain("catalog.read");
    }

    [Fact]
    public async Task ClientTierResolver_PrincipalsWithoutSid_DoNotCollideInSharedBucket()
    {
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance);

        var principalA = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", "tenant-x"),
            new Claim("email", "alice@example.com")
        ], "TestAuth"));

        var principalB = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", "tenant-x"),
            new Claim("email", "bob@example.com")
        ], "TestAuth"));

        var ctxA = await resolver.ResolveAsync(principalA, apiKey: null, clientIp: null);
        var ctxB = await resolver.ResolveAsync(principalB, apiKey: null, clientIp: null);

        ctxA.SubjectId.ShouldNotBe(ctxB.SubjectId);
        ctxA.SubjectId.ShouldNotContain("authenticated_user");
        ctxB.SubjectId.ShouldNotContain("authenticated_user");
    }

    [Fact]
    public async Task ClientTierResolver_UnauthenticatedCallersWithoutIp_DoNotCollideInSharedBucket()
    {
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance);

        var ctx1 = await resolver.ResolveAsync(principal: null, apiKey: null, clientIp: null);
        var ctx2 = await resolver.ResolveAsync(principal: null, apiKey: null, clientIp: null);

        ctx1.SubjectId.ShouldNotBe(ctx2.SubjectId);
        ctx1.SubjectId.ShouldNotBe("anon_anonymous");
        ctx2.SubjectId.ShouldNotBe("anon_anonymous");
    }

    [Fact]
    public void CatalogVary_WithoutSchemaContractHeader_VariesOnAcceptLanguageOnly()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "de-DE, de;q=0.9, en;q=0.8";

        Autheris.Api.Endpoints.CatalogApiEndpoints.ApplyCatalogLanguageAndVary(context);

        var vary = context.Response.Headers.Vary.ToString();
        vary.ShouldContain("Accept-Language");
        vary.ShouldNotContain("X-Gateway-Contract");
    }

    [Fact]
    public void CatalogVary_WithSchemaContractHeader_VariesOnAcceptLanguageAndSchemaContract()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = "en-US";
        context.Request.Headers["X-Gateway-Contract"] = "partner-v2";

        Autheris.Api.Endpoints.CatalogApiEndpoints.ApplyCatalogLanguageAndVary(context);

        var vary = context.Response.Headers.Vary.ToString();
        vary.ShouldContain("Accept-Language");
        vary.ShouldContain("X-Gateway-Contract");
    }

    [Fact]
    public void CatalogVary_WithContractItemInContext_VariesOnAcceptLanguageAndSchemaContract()
    {
        var context = new DefaultHttpContext();
        context.Items[Autheris.Application.Governance.Contracts.SchemaContractConstants.ContractItemKey] = "enterprise-v1";

        Autheris.Api.Endpoints.CatalogApiEndpoints.ApplyCatalogLanguageAndVary(context);

        var vary = context.Response.Headers.Vary.ToString();
        vary.ShouldContain("Accept-Language");
        vary.ShouldContain("X-Gateway-Contract");
    }
}
