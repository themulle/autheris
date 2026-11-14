namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Services;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class ScimEndpointHardeningTests
{
    private const string RealmASecret = "RealmASuperSecretScimKey1234567890ABCDEF";
    private const string RealmBSecret = "RealmBSuperSecretScimKey1234567890ABCDEF";
    private const string GroupSid = "S-1-5-21-100-200-300-5101";
    private static readonly TenantId Tenant = new("tenant-scim");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Missing_ip_allowlist_fails_startup_outside_development()
    {
        var options = CreateRealmOptions(allowedIpRanges: []);

        var prodValidator = new IdentityRealmsScimOptionsValidator(new FakeHostEnvironment("Production"));
        var stagingValidator = new IdentityRealmsScimOptionsValidator(new FakeHostEnvironment("Staging"));
        var devValidator = new IdentityRealmsScimOptionsValidator(new FakeHostEnvironment("Development"));

        prodValidator.Validate(null, options).Failed.ShouldBeTrue();
        stagingValidator.Validate(null, options).Failed.ShouldBeTrue();
        devValidator.Validate(null, options).Succeeded.ShouldBeTrue();

        var withCidr = CreateRealmOptions(allowedIpRanges: ["10.0.0.0/8"]);
        prodValidator.Validate(null, withCidr).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Rate_limit_returns_429_with_retry_after()
    {
        var options = CreateRealmOptions(allowedIpRanges: ["10.0.0.0/8"], globalRpm: 120, realmRpm: 42);
        var rateLimiter = new CapturingRateLimiter(new RateLimitResult(Allowed: false, RetryAfterSeconds: 17));
        var tracker = new SpySyncStateTracker();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IRateLimiterService>(rateLimiter)
            .AddSingleton<IScimSyncStateTracker>(tracker)
            .AddSingleton<IOptionsMonitor<IdentityRealmsOptions>>(new StaticOptionsMonitor<IdentityRealmsOptions>(options))
            .BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/scim/v2/realm-a/Users";
        httpContext.Request.RouteValues["realmId"] = "realm-a";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("autheris:realm", "realm-a"),
            new Claim("tenant_id", Tenant.Value)
        ], GatewayAuthSchemes.Scim));
        httpContext.Response.Body = new MemoryStream();

        var rejected = await ScimEndpointGuard.EnforceBeforeExecutionAsync(httpContext, CancellationToken.None);

        rejected.ShouldNotBeNull();
        rateLimiter.LastSid.ShouldBe("scim-client:realm-a");
        rateLimiter.LastOptions.ShouldNotBeNull();
        rateLimiter.LastOptions!.TokenBucketCapacity.ShouldBe(42);
        rateLimiter.LastOptions.TokensPerSecond.ShouldBe(1);
        tracker.Calls.ShouldBe(0);

        await rejected!.ExecuteAsync(httpContext);
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        httpContext.Response.Headers.RetryAfter.ToString().ShouldBe("17");
        httpContext.Response.ContentType!.ShouldStartWith("application/scim+json");
    }

    [Fact]
    public async Task Every_membership_change_writes_required_audit()
    {
        var store = new ScimDeprovisioningTests.SharedDeprovisionStore();
        var audit = new RecordingAuditWriter();
        var svc = CreateScimService(store, audit);

        var created = await svc.CreateGroupAsync(
            Tenant,
            "realm-a",
            new ScimGroup(
                Id: null,
                ExternalId: "g-1",
                DisplayName: "Analysts",
                Members:
                [
                    new ScimGroupMember("u-1", Type: ScimMemberTypes.User),
                    new ScimGroupMember("u-2", Type: ScimMemberTypes.User)
                ],
                CreatedAt: null,
                UpdatedAt: null,
                ObjectSid: GroupSid));

        audit.RequiredEntries.Count(e => e.EventType == AuditEventTypes.ScimGroupProvisioned).ShouldBe(1);
        audit.RequiredEntries.Count(e => e.EventType == AuditEventTypes.ScimGroupMembershipChanged).ShouldBe(2);

        audit.RequiredEntries.Clear();

        var patch = new ScimPatchRequest(
            [ScimSchemaUris.PatchOp],
            [
                new ScimPatchOperation("add", "members", new[] { new ScimGroupMember("u-3", Type: ScimMemberTypes.User) }),
                new ScimPatchOperation("remove", "members", new[] { new ScimGroupMember("u-1", Type: ScimMemberTypes.User) })
            ]);

        await svc.PatchGroupAsync(Tenant, "realm-a", created.GroupId, patch);

        var membershipEvents = audit.RequiredEntries
            .Where(e => e.EventType == AuditEventTypes.ScimGroupMembershipChanged)
            .ToList();
        membershipEvents.Count.ShouldBe(2);
        membershipEvents.ShouldContain(e => e.DetailsJson.Contains("\"member_key\":\"u-3\"") && e.DetailsJson.Contains("\"operation\":\"add\""));
        membershipEvents.ShouldContain(e => e.DetailsJson.Contains("\"member_key\":\"u-1\"") && e.DetailsJson.Contains("\"operation\":\"remove\""));
    }

    [Fact]
    public async Task Audit_failure_refuses_write()
    {
        var store = new ScimDeprovisioningTests.SharedDeprovisionStore();
        var failingAudit = new RecordingAuditWriter { ThrowOnRecord = true };
        var svc = CreateScimService(store, failingAudit);

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            svc.CreateUserAsync(
                Tenant,
                "realm-a",
                new ScimUser(
                    Id: null,
                    ExternalId: "ext-1",
                    UserName: "alice",
                    DisplayName: "Alice",
                    Email: "alice@example.org",
                    Active: true,
                    DeactivatedAt: null,
                    CreatedAt: null,
                    UpdatedAt: null)));
        store.Users.ShouldBeEmpty();

        store.SeedGroup("g-1", GroupSid);
        await Should.ThrowAsync<AuditUnavailableException>(() =>
            svc.PatchGroupAsync(
                Tenant,
                "realm-a",
                "g-1",
                new ScimPatchRequest(
                    [ScimSchemaUris.PatchOp],
                    [new ScimPatchOperation("add", "members", new[] { new ScimGroupMember("u-1", Type: ScimMemberTypes.User) })])));
        store.Memberships.ShouldBeEmpty();

        await Should.ThrowAsync<AuditUnavailableException>(() => svc.DeleteGroupAsync(Tenant, "realm-a", "g-1"));
        store.Groups.ContainsKey("g-1").ShouldBeTrue();
    }

    [Fact]
    public async Task Oversized_patch_is_rejected()
    {
        var tooManyOps = Enumerable.Range(0, ScimRequestValidator.MaxMembersOrOperationsPerRequest + 1)
            .Select(i => new ScimPatchOperation("add", "members", new[] { new ScimGroupMember($"u-{i}", Type: ScimMemberTypes.User) }))
            .ToArray();

        Should.Throw<ScimTooManyException>(() =>
            ScimRequestValidator.ValidatePatchRequest(new ScimPatchRequest([ScimSchemaUris.PatchOp], tooManyOps), isGroup: true));

        var tooManyMembersInSingleOp = Enumerable.Range(0, ScimRequestValidator.MaxMembersOrOperationsPerRequest + 1)
            .Select(i => new ScimGroupMember($"u-{i}", Type: ScimMemberTypes.User))
            .ToArray();

        Should.Throw<ScimTooManyException>(() =>
            ScimRequestValidator.ValidatePatchRequest(
                new ScimPatchRequest(
                    [ScimSchemaUris.PatchOp],
                    [new ScimPatchOperation("add", "members", tooManyMembersInSingleOp)]),
                isGroup: true));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        httpContext.Request.Method = HttpMethods.Patch;
        httpContext.Request.ContentLength = ScimEndpointGuard.MaxRequestBodyBytes + 1;
        httpContext.Response.Body = new MemoryStream();

        var rejected = await ScimEndpointGuard.EnforceBeforeExecutionAsync(httpContext, CancellationToken.None);
        rejected.ShouldNotBeNull();
        await rejected!.ExecuteAsync(httpContext);
        httpContext.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        httpContext.Response.ContentType!.ShouldStartWith("application/scim+json");
        httpContext.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(httpContext.Response.Body);
        doc.RootElement.GetProperty("scimType").GetString().ShouldBe("tooMany");
    }

    [Theory]
    [InlineData(AuthenticationMode.Direct)]
    [InlineData(AuthenticationMode.Upstream)]
    [InlineData(AuthenticationMode.Both)]
    public void Jwt_or_forwardauth_never_authenticates_scim_routes(AuthenticationMode mode)
    {
        var options = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                Mode = mode,
                ForwardAuth = new ForwardAuthOptions { Enabled = true, UserHeader = "X-Forwarded-User" }
            }
        };

        foreach (var path in new[] { "/scim/v2/realm-a/Users", "/scim/v2/realm-a/Groups", "/scim/v2/realm-a/ServiceProviderConfig" })
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = path;
            ctx.Request.Headers.Authorization = "Bearer eyJhbGciOiJSUzI1NiJ9.payload.sig";
            ctx.Request.Headers["X-Forwarded-User"] = "alice";

            GatewayAuthSchemeSelector.Select(
                    ctx,
                    options,
                    isBasicSessionAllowed: true,
                    basicSessionCookieName: "cookie",
                    isDevAuthAllowed: false)
                .ShouldBe(GatewayAuthSchemes.Scim);
        }
    }

    [Fact]
    public async Task Secret_of_realm_a_cannot_write_realm_b()
    {
        var options = CreateRealmOptions(allowedIpRanges: []);
        var result = await AuthenticateScimAsync(
            options,
            path: "/scim/v2/realm-b/Users",
            bearerToken: RealmASecret,
            forwardedTenantHeader: "rogue-tenant");

        result.Succeeded.ShouldBeFalse();

        var validA = await AuthenticateScimAsync(
            options,
            path: "/scim/v2/realm-a/Users",
            bearerToken: RealmASecret,
            forwardedTenantHeader: "rogue-tenant");

        validA.Succeeded.ShouldBeTrue();
        validA.Principal!.FindFirstValue("autheris:realm").ShouldBe("realm-a");
        validA.Principal!.GetTenantId().ShouldBe(Tenant);
    }

    private static ScimService CreateScimService(ScimDeprovisioningTests.SharedDeprovisionStore store, RecordingAuditWriter audit)
    {
        var time = new StaticTimeProvider(FixedNow);
        var deprov = new IdentityDeprovisioningService(store);
        return new ScimService(store, deprov, timeProvider: time, auditWriter: audit);
    }

    private static IdentityRealmsOptions CreateRealmOptions(string[] allowedIpRanges, int globalRpm = 600, int? realmRpm = null) => new()
    {
        Scim = new ScimOptions
        {
            AllowedIpRanges = [.. allowedIpRanges],
            RequestsPerMinute = globalRpm
        },
        Realms = new Dictionary<string, IdentityRealmConfigOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["realm-a"] = new()
            {
                Provisioning = RealmProvisioningMode.Scim,
                FixedTenantId = Tenant.Value,
                Scim = new ScimRealmSecretOptions
                {
                    PrimarySecret = RealmASecret,
                    RequestsPerMinute = realmRpm
                }
            },
            ["realm-b"] = new()
            {
                Provisioning = RealmProvisioningMode.Scim,
                FixedTenantId = "tenant-b",
                Scim = new ScimRealmSecretOptions
                {
                    PrimarySecret = RealmBSecret
                }
            }
        }
    };

    private static async Task<AuthenticateResult> AuthenticateScimAsync(
        IdentityRealmsOptions options,
        string path,
        string bearerToken,
        string? forwardedTenantHeader = null)
    {
        var handler = new ScimAuthenticationHandler(
            new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new StaticOptionsMonitor<IdentityRealmsOptions>(options));

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Authorization = $"Bearer {bearerToken}";
        if (forwardedTenantHeader is not null)
        {
            context.Request.Headers["X-Tenant-ID"] = forwardedTenantHeader;
        }

        await handler.InitializeAsync(
            new AuthenticationScheme(GatewayAuthSchemes.Scim, GatewayAuthSchemes.Scim, typeof(ScimAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Api";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StaticTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CapturingRateLimiter(RateLimitResult result) : IRateLimiterService
    {
        public string? LastSid { get; private set; }
        public PostAuthSidRateLimitOptions? LastOptions { get; private set; }

        public Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default) =>
            Task.FromResult(result);

        public Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default)
        {
            LastSid = sid;
            LastOptions = options;
            return Task.FromResult(result);
        }

        public Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SpySyncStateTracker : IScimSyncStateTracker
    {
        public int Calls { get; private set; }

        public Task RecordAuthenticatedContactAsync(TenantId tenantId, string realmId, bool isWrite, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }

        public ValueTask<bool> IsSyncStaleAsync(TenantId tenantId, IdentityRealm realm, ScimRealmSyncState? state, CancellationToken ct = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public bool ThrowOnRecord { get; init; }
        public List<AuditLogEntry> RequiredEntries { get; } = [];

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (ThrowOnRecord)
            {
                throw new AuditUnavailableException("Audit store unavailable", new InvalidOperationException("simulated"));
            }

            RequiredEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
    }
}
