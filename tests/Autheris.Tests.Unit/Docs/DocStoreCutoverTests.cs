namespace Autheris.Tests.Unit.Docs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Application.Events;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Events;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DocStoreCutoverTests
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class CapturingAuditWriter : IAuditWriter
    {
        public bool ShouldFail { get; set; }
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (ShouldFail)
            {
                throw new AuditUnavailableException("Simulated audit failure.");
            }
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
    }

    private sealed class BodyDetectionFeature(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    private sealed class NoOpGatewayEventBus : IGatewayEventBus
    {
        private sealed class NoOpDisposable : IDisposable { public void Dispose() { } }
        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : IGatewayEvent => Task.CompletedTask;
        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IGatewayEvent => new NoOpDisposable();
        public IDisposable Subscribe(Type eventType, Func<IGatewayEvent, CancellationToken, Task> handler) => new NoOpDisposable();
    }

    private sealed class PermissiveProvider(IServiceProvider inner) : IServiceProvider, IServiceProviderIsService
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);
        public bool IsService(Type serviceType) => serviceType.IsInterface || inner.GetService(serviceType) != null;
        public bool IsKeyedService(Type serviceType, object? serviceKey) => false;
    }

    private sealed class TestRoutes(IServiceProvider sp) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = sp;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static (ServiceProvider Provider, TestRoutes Routes, InMemoryDocumentRepository Repo, InMemoryDocSearchService Search, GovernanceDocStore Store) BuildRestHarness(DocsOptions? options = null)
    {
        var opts = Options.Create(options ?? new DocsOptions());
        var repo = new InMemoryDocumentRepository();
        var search = new InMemoryDocSearchService();
        var ingestor = new MarkdownDocIngestor();
        var store = new GovernanceDocStore(repo, search, ingestor, NullLogger<GovernanceDocStore>.Instance, opts);

        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        services.AddLogging();
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        services.AddSingleton(opts);
        services.AddSingleton<IDocumentRepository>(repo);
        services.AddSingleton<IDocSearchService>(search);
        services.AddSingleton<IDocIngestor>(ingestor);
        services.AddSingleton<IUniversalDocStore>(store);
        services.AddSingleton<IGatewayEventBus>(new NoOpGatewayEventBus());

        var sp = services.BuildServiceProvider();
        var routes = new TestRoutes(new PermissiveProvider(sp));
        routes.MapDocIngestionEndpoints();
        return (sp, routes, repo, search, store);
    }

    private static async Task<(int Status, string? ETag, string Body)> InvokeEndpointAsync(
        ServiceProvider sp,
        TestRoutes routes,
        string method,
        string routePattern,
        Dictionary<string, string> routeValues,
        ClaimsPrincipal user,
        string? jsonBody = null,
        string? ifMatch = null)
    {
        var endpoint = routes.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == routePattern &&
                         e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase));

        var ctx = new DefaultHttpContext { RequestServices = sp, User = user, TraceIdentifier = "fixed-trace" };
        ctx.Request.Method = method;
        foreach (var (k, v) in routeValues)
        {
            ctx.Request.RouteValues[k] = v;
        }

        if (jsonBody is not null)
        {
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature(true));
            ctx.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            ctx.Request.Body = new MemoryStream(bytes);
            ctx.Request.ContentLength = bytes.Length;
        }

        if (ifMatch is not null)
        {
            ctx.Request.Headers.IfMatch = ifMatch;
        }

        ctx.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(ctx);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, ctx.Response.Headers.ETag.ToString(), body);
    }

    private static ClaimsPrincipal Principal(string? tenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-USER"), new("sub", "S-1-5-21-USER") };
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        if (roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase))
        {
            claims.Add(new Claim("autheris_admin", "true"));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task RestPut_CreateAndIfMatchReplace_EnforcesETagsConflictsAndNotFound()
    {
        var (sp, routes, _, _, store) = BuildRestHarness(new DocsOptions { MaxDocumentBytes = 256 });
        await using var _ = sp;
        await store.CreateAsync(new DocWriter(null, "S-ADMIN"), new DepositDocRequest("payments", DocCategory.Runbook, "Official", "# Official", DocId: "official-doc"));

        var tenantUser = Principal("tenant-a", "GovernanceAdmin");
        const string route = "/api/v1/docs/apps/{appId}/documents/{category}/{docId}";

        var created = await InvokeEndpointAsync(sp, routes, "PUT", route,
            new() { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "t-doc" },
            tenantUser, """{"title":"Runbook","content":"# Step 1"}""");
        created.Status.ShouldBe(StatusCodes.Status201Created, $"Body was: {created.Body}");
        created.ETag.ShouldBe("\"1\"");

        var duplicate = await InvokeEndpointAsync(sp, routes, "PUT", route,
            new() { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "t-doc" },
            tenantUser, """{"title":"Runbook","content":"# Duplicate"}""");
        duplicate.Status.ShouldBe(StatusCodes.Status409Conflict);
        duplicate.Body.ShouldContain("DOC_EXISTS");

        var reserved = await InvokeEndpointAsync(sp, routes, "PUT", route,
            new() { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "official-doc" },
            tenantUser, """{"title":"Shadow","content":"# Shadow"}""");
        reserved.Status.ShouldBe(StatusCodes.Status409Conflict);
        reserved.Body.ShouldContain("DOC_ID_RESERVED");

        var replaced = await InvokeEndpointAsync(sp, routes, "PUT", route,
            new() { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "t-doc" },
            tenantUser, """{"title":"Runbook","content":"# Step 2"}""", ifMatch: "\"1\"");
        replaced.Status.ShouldBe(StatusCodes.Status200OK);
        replaced.ETag.ShouldBe("\"2\"");

        var stale = await InvokeEndpointAsync(sp, routes, "PUT", route,
            new() { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "t-doc" },
            tenantUser, """{"title":"Runbook","content":"# Stale"}""", ifMatch: "\"1\"");
        stale.Status.ShouldBe(StatusCodes.Status412PreconditionFailed);
        stale.Body.ShouldContain("VERSION_MISMATCH");

        var missing = await InvokeEndpointAsync(sp, routes, "PUT", route,
            new() { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "missing" },
            tenantUser, """{"title":"Runbook","content":"# Missing"}""", ifMatch: "\"1\"");
        missing.Status.ShouldBe(StatusCodes.Status404NotFound);

        var tooLarge = await InvokeEndpointAsync(sp, routes, "PUT", route,
            new() { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "big" },
            tenantUser, JsonSerializer.Serialize(new { title = "Big", content = new string('x', 500) }));
        tooLarge.Status.ShouldBe(StatusCodes.Status413PayloadTooLarge);
        tooLarge.Body.ShouldContain("DOC_TOO_LARGE");
    }

    [Fact]
    public async Task RestDelete_Returns204ForOwner_AndIdentical404ForMissingOrCrossTenant()
    {
        var (sp, routes, _, search, store) = BuildRestHarness();
        await using var _ = sp;
        await store.CreateAsync(new DocWriter("tenant-a", "S-A"), new DepositDocRequest("payments", DocCategory.Runbook, "Mine", "# SecretTokenA", DocId: "mine"));

        const string route = "/api/v1/docs/apps/{appId}/documents/{category}/{docId}";
        var rv = new Dictionary<string, string> { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "mine" };

        var crossTenant = await InvokeEndpointAsync(sp, routes, "DELETE", route, rv, Principal("tenant-b", "GovernanceAdmin"));
        var absent = await InvokeEndpointAsync(sp, routes, "DELETE", route,
            new() { ["appId"] = "payments", ["category"] = "Runbook", ["docId"] = "never-existed" },
            Principal("tenant-b", "GovernanceAdmin"));

        crossTenant.Status.ShouldBe(StatusCodes.Status404NotFound);
        absent.Status.ShouldBe(StatusCodes.Status404NotFound);
        crossTenant.Body.ShouldBe(absent.Body);

        var ownedDelete = await InvokeEndpointAsync(sp, routes, "DELETE", route, rv, Principal("tenant-a", "GovernanceAdmin"));
        ownedDelete.Status.ShouldBe(StatusCodes.Status204NoContent);
        (await search.SearchAsync("SecretTokenA", DocAccessScope.ForTenant("tenant-a"))).ShouldBeEmpty();
    }

    [Fact]
    public async Task RestPurgeTenantAndSync_RequireCanonicalClusterAdmin_AndPurgeOnlyTargetTenant()
    {
        var (sp, routes, repo, search, store) = BuildRestHarness();
        await using var _ = sp;
        await store.CreateAsync(new DocWriter("tenant-a", "S-A"), new DepositDocRequest("app", DocCategory.Guide, "A", "# AlphaPurge", DocId: "a"));
        await store.CreateAsync(new DocWriter("tenant-b", "S-B"), new DepositDocRequest("app", DocCategory.Guide, "B", "# BetaKeep", DocId: "b"));

        var spoofedAdmin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-SPOOF"),
            new Claim("tenant_id", "tenant-b"),
            new Claim(ClaimTypes.Role, "ClusterAdmin")
        ], ClusterAdminPolicy.ForwardAuthScheme));

        var deniedPurge = await InvokeEndpointAsync(sp, routes, "DELETE", "/api/v1/docs/tenants/{tenantId}",
            new() { ["tenantId"] = "tenant-a" }, spoofedAdmin);
        deniedPurge.Status.ShouldBe(StatusCodes.Status403Forbidden);

        var deniedSync = await InvokeEndpointAsync(sp, routes, "POST", "/api/v1/docs/sync", new(), spoofedAdmin);
        deniedSync.Status.ShouldBe(StatusCodes.Status403Forbidden);

        var canonical = Principal("system", "ClusterAdmin");
        var purged = await InvokeEndpointAsync(sp, routes, "DELETE", "/api/v1/docs/tenants/{tenantId}",
            new() { ["tenantId"] = "tenant-a" }, canonical);
        purged.Status.ShouldBe(StatusCodes.Status200OK);
        purged.Body.ShouldContain("\"deletedCount\":1");
        (await repo.GetAsync(new DocKey("tenant-b", "app", DocCategory.Guide, "b"))).ShouldNotBeNull();
        (await search.SearchAsync("AlphaPurge", DocAccessScope.ForTenant("tenant-a"))).ShouldBeEmpty();

        var synced = await InvokeEndpointAsync(sp, routes, "POST", "/api/v1/docs/sync", new(), canonical);
        synced.Status.ShouldBe(StatusCodes.Status200OK);
        (await search.SearchAsync("BetaKeep", DocAccessScope.ForTenant("tenant-b"))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task DocIndexRefreshService_FailsClosedUntilInitialLoad_ThenTracksWatermarksAndPurgesTombstones()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var repo = new InMemoryDocumentRepository(clock);
        var search = new InMemoryDocSearchService();
        var ingestor = new MarkdownDocIngestor();
        var options = Options.Create(new DocsOptions { IndexRefreshSeconds = 30, TombstoneRetentionHours = 24 });
        var refresh = new DocIndexRefreshService(repo, search, ingestor, NullLogger<DocIndexRefreshService>.Instance, options, clock);
        var store = new GovernanceDocStore(repo, search, ingestor, NullLogger<GovernanceDocStore>.Instance, options, clock);

        repo.FailNextRead = true;
        await refresh.StartAsync(CancellationToken.None);
        refresh.IsInitialLoadComplete.ShouldBeFalse();

        var notReadyJson = await McpDocTools.ExecuteDocToolAsync(
            McpDocTools.SearchDocs, """{"query":"anything"}""", search, store, sessionContext: null, auditWriter: null, indexReadiness: refresh);
        notReadyJson.ShouldContain("NOT_AVAILABLE");

        await repo.CreateAsync(new DocRecord(
            null, "payments", DocCategory.Runbook, "initial", "Initial Doc", "# InitialMarker", "sha", 14, [], null, "S-1", 1, clock.GetUtcNow()),
            new DocQuota(100, 1024 * 1024));

        (await refresh.RunRefreshTickAsync()).ShouldBeTrue();
        refresh.IsInitialLoadComplete.ShouldBeTrue();
        (await search.SearchAsync("InitialMarker")).Count.ShouldBe(1);

        // Incremental watermark update + soft-delete propagation
        clock.Advance(TimeSpan.FromMinutes(1));
        await repo.CreateAsync(new DocRecord(
            "tenant-a", "payments", DocCategory.Runbook, "delta", "Delta", "# DeltaMarker", "sha", 12, [], null, "S-1", 1, clock.GetUtcNow()),
            new DocQuota(100, 1024 * 1024));
        (await refresh.RunRefreshTickAsync()).ShouldBeTrue();
        repo.ReadWatermarks.Last().ShouldBe(clock.GetUtcNow() - TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(5));
        (await search.SearchAsync("DeltaMarker", DocAccessScope.ForTenant("tenant-a"))).Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(30));
        await repo.DeleteAsync(new DocKey("tenant-a", "payments", DocCategory.Runbook, "delta"), null, clock.GetUtcNow());
        (await refresh.RunRefreshTickAsync()).ShouldBeTrue();
        (await search.SearchAsync("DeltaMarker", DocAccessScope.ForTenant("tenant-a"))).ShouldBeEmpty();

        // Hourly tombstone purge
        clock.Advance(TimeSpan.FromMinutes(60));
        (await refresh.RunRefreshTickAsync()).ShouldBeTrue();
        repo.PurgeCutoffs.Count.ShouldBe(1);
        repo.PurgeCutoffs[0].ShouldBe(clock.GetUtcNow() - TimeSpan.FromHours(24));

        await refresh.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task McpDocTools_TruncatesAt64KiBRuneBoundary_ExcludesDeleteTool_AndAuditsDepositFailClosed()
    {
        McpDocTools.Definitions().ShouldNotContain(d => d.Name.Contains("delete", StringComparison.OrdinalIgnoreCase));

        // Fill > 64 KiB using 4-byte emoji surrogate pairs so naive slicing could split a surrogate/rune
        var pattern = "🚨RuneBoundary";
        var bigBuilder = new StringBuilder("# Large Runbook\n\n");
        while (Encoding.UTF8.GetByteCount(bigBuilder.ToString()) < 70 * 1024)
        {
            bigBuilder.Append(pattern);
        }

        var search = new InMemoryDocSearchService();
        var chunkId = new DocChunkIdentifier("app", "Runbook", "huge", "large-runbook");
        await search.IndexChunkAsync(new DocChunk(
            chunkId, "app", DocCategory.Runbook, "Large Runbook", bigBuilder.ToString(), "Large summary",
            ["runbook"], [], "app/runbook/huge.md", "large-runbook", DateTimeOffset.UtcNow, TenantId: "tenant-a"));

        var procJson = await McpDocTools.ExecuteDocToolAsync(
            McpDocTools.GetProcedure,
            JsonSerializer.Serialize(new { identifier = chunkId.ToString() }),
            search,
            null,
            new McpSessionContext("s1", "client", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Roles: ["DataConsumer"]));

        using var doc = JsonDocument.Parse(procJson);
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("contentNotice").GetString().ShouldBe(McpDocTools.UntrustedContentNotice);
        var returnedContent = doc.RootElement.GetProperty("content").GetString()!;
        Encoding.UTF8.GetByteCount(returnedContent).ShouldBeLessThanOrEqualTo(McpDocTools.MaxReadContentBytes);
        Rune.IsValid(returnedContent[^1]).ShouldBeTrue();

        // Deposit audit metadata check + fail-closed propagation
        var repo = new InMemoryDocumentRepository();
        var store = new GovernanceDocStore(repo, search, new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
        var audit = new CapturingAuditWriter();
        var session = new McpSessionContext("s1", "c1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-A", Roles: ["GovernanceAdmin"]);

        var ok = await McpDocTools.ExecuteDocToolAsync(
            McpDocTools.DepositDocumentation,
            """{"appId":"app","category":"Runbook","docId":"audited","title":"SecretTitleMarker","content":"# Audited\nSecretBodyMarker"}""",
            search, store, session, audit, indexReadiness: null, CancellationToken.None);
        ok.ShouldContain("\"success\": true");
        audit.Entries.Count.ShouldBe(1);
        audit.Entries[0].EventType.ShouldBe(AuditEventTypes.DocIngestionDocumentApplied);
        audit.Entries[0].DetailsJson.ShouldNotContain("SecretTitleMarker");
        audit.Entries[0].DetailsJson.ShouldNotContain("SecretBodyMarker");

        audit.ShouldFail = true;
        await Should.ThrowAsync<AuditUnavailableException>(async () =>
        {
            await McpDocTools.ExecuteDocToolAsync(
                McpDocTools.DepositDocumentation,
                """{"appId":"app","category":"Runbook","docId":"audited-2","title":"T","content":"# T"}""",
                search, store, session, audit, indexReadiness: null, CancellationToken.None);
        });
    }
}
