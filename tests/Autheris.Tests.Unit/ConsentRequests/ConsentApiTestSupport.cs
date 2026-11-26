namespace Autheris.Tests.Unit.ConsentRequests;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Requests;
using Autheris.Tests.Common.Fakes;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

internal sealed record ExecutedEndpointResponse(
    int StatusCode,
    IHeaderDictionary Headers,
    string Body)
{
    public JsonElement ReadJson() => JsonSerializer.Deserialize<JsonElement>(Body);
}

internal sealed class ConsentApiTestHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services;

    private ConsentApiTestHarness(
        ServiceProvider services,
        InMemoryConsentStore store,
        RecordingDenialRecorder denials,
        Guid registeredSystemId,
        string registeredClientId)
    {
        _services = services;
        Store = store;
        Denials = denials;
        RegisteredSystemId = registeredSystemId;
        RegisteredClientId = registeredClientId;
    }

    public InMemoryConsentStore Store { get; }
    public RecordingDenialRecorder Denials { get; }
    public Guid RegisteredSystemId { get; }
    public string RegisteredClientId { get; }
    public IServiceProvider Services => _services;

    public static ConsentApiTestHarness Create(int decisionsPerMinute = 60)
    {
        var clock = new FakeTimeProvider(FixedNow);
        var signer = EcdsaGrantRecordSigner.CreateEphemeral("test-grant-key-1");
        var store = new InMemoryConsentStore(signer, clock);
        var systemId = store.RegisterExternalSystem("tenant-a", "servicenow-prod", true);
        var denials = new RecordingDenialRecorder();
        var resolver = new IdentitySubjectResolver();
        var guard = new ConsentDecisionGuard(resolver);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IIdempotencyStore, InMemoryTestIdempotencyStore>();
        services.AddSingleton<IRateLimiterService>(new InMemoryRateLimiterService(timeProvider: clock));
        services.AddSingleton(Options.Create(new GovernanceOptions
        {
            ExternalApi = new ExternalApiSettings
            {
                RateLimits = new ExternalApiRateLimitSettings
                {
                    DecisionsAndRevokesPerMinute = decisionsPerMinute
                }
            }
        }));
        services.Configure<ConsentEventsOptions>(opts => opts.RetentionDays = 30);
        services.AddSingleton<IIdentitySubjectResolver>(resolver);
        services.AddSingleton<IConsentDecisionGuard>(guard);
        services.AddSingleton<IConsentDecisionDenialRecorder>(denials);
        services.AddSingleton<IGrantPolicy, GrantPolicy>();
        services.AddSingleton<IGrantValidityPolicy>(GrantValidityPolicy.CreateDefault());
        services.AddSingleton<IGrantRecordSigner>(signer);
        services.AddSingleton<IGrantRecordStore>(store);
        services.AddSingleton<IGrantChainVerifier, GrantChainVerifier>();
        services.AddSingleton<IAuditLogRepository>(store);
        services.AddSingleton<IGrantRecordExportService, GrantRecordExportService>();
        services.AddSingleton<IConsentApprovalRepository>(store);
        services.AddSingleton<IConsentRepository>(store);
        services.AddSingleton<IConsentWorkflowConfigRepository>(store);
        services.AddSingleton<IConsentEventOutbox>(store);
        services.AddSingleton<ITableMetadataRepository>(store);
        services.AddSingleton<IContainerOwnership>(store);
        services.AddSingleton<IAuditWriter>(new InMemoryAuditWriter());
        services.AddSingleton<IExternalDecisionService>(new HarnessExternalDecisionService(store, clock));
        services.AddScoped<IPermissionRequestEngine>(sp => new PermissionRequestEngine(
            store,
            sp.GetRequiredService<IAuditWriter>(),
            new ApproverResolver(),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            sp.GetRequiredService<IGrantPolicy>(),
            sp.GetRequiredService<IGrantValidityPolicy>(),
            Array.Empty<IGrantEffectApplier>(),
            metadataRepository: store,
            subjectResolver: resolver,
            timeProvider: clock,
            consentRepository: store));
        services.AddSingleton<IAuthorizationHandler, ExternalSystemRequirementHandler>();
        services.AddAuthorization(options => options.AddGatewayAuthorizationPolicies());

        return new ConsentApiTestHarness(services.BuildServiceProvider(), store, denials, systemId, "servicenow-prod");
    }

    public Guid RegisterExternalSystem(
        string tenantId, string clientId, bool enabled, ExternalWorkflowSystemRole role = ExternalWorkflowSystemRole.Workflow) =>
        Store.RegisterExternalSystem(tenantId, clientId, enabled, role);

    public ConsentRequest SeedExternalRequest(string tenantId, Guid externalSystemId, string externalRef) =>
        Store.SeedExternalRequest(tenantId, externalSystemId, externalRef);

    public static ClaimsPrincipal BuildAppPrincipal(string tenantId, string clientId, params string[] roles) =>
        BuildPrincipal("app", tenantId, clientId, roles);

    public static ClaimsPrincipal BuildUserPrincipal(string tenantId, string userSid, params string[] roles) =>
        BuildPrincipal("user", tenantId, userSid, roles);

    public static ClaimsPrincipal BuildDelegatedUserPrincipal(string tenantId, string userSid, string scope, string? roles = null)
    {
        var roleList = string.IsNullOrWhiteSpace(roles) ? null : roles.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return BuildPrincipal("user", tenantId, userSid, roleList, [scope]);
    }

    public static ClaimsPrincipal BuildPrincipal(
        string mode,
        string tenantId,
        string subjectOrClient,
        string[]? roles = null,
        string[]? scopes = null)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId) };
        if (mode == "app")
        {
            claims.Add(new Claim("idtyp", "app"));
            claims.Add(new Claim("azp", subjectOrClient));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subjectOrClient));
        }
        else
        {
            claims.Add(new Claim(ClaimTypes.PrimarySid, subjectOrClient));
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subjectOrClient));
        }

        if (roles is not null)
        {
            foreach (var r in roles)
            {
                claims.Add(new Claim("roles", r));
                claims.Add(new Claim(ClaimTypes.Role, r));
            }
        }

        if (scopes is not null && scopes.Length > 0)
        {
            claims.Add(new Claim("scp", string.Join(' ', scopes)));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.NameIdentifier, ClaimTypes.Role));
    }

    public async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal principal, string policyName)
    {
        await using var scope = _services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        return await auth.AuthorizeAsync(principal, resource: null, policyName: policyName);
    }

    public async Task<ExecutedEndpointResponse> DecideAsync(
        ClaimsPrincipal caller,
        Guid requestId,
        string decision,
        string? idempotencyKey,
        string? ifMatch,
        string? reason = "Approved via external ticket",
        string? approverSid = null,
        int? expectedVersion = null)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.ConsentDecide)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        if (idempotencyKey is not null)
        {
            ctx.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        if (ifMatch is not null)
        {
            ctx.Request.Headers["If-Match"] = ifMatch;
        }

        var body = new ConsentDecisionBody(
            Decision: decision,
            Reason: reason,
            ApproverSid: approverSid,
            ExpectedVersion: expectedVersion,
            DecidedAt: FixedNow,
            Nonce: "nonce-" + (idempotencyKey ?? "none"),
            Signature: "00");
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.DecideRequestAsync(
            requestId,
            body,
            ctx,
            sp.GetRequiredService<IPermissionRequestEngine>(),
            sp.GetRequiredService<IConsentApprovalRepository>(),
            sp.GetRequiredService<IConsentWorkflowConfigRepository>(),
            sp.GetService<IExternalDecisionService>(),
            sp.GetService<IIdempotencyStore>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>(),
            sp.GetService<IConsentDecisionDenialRecorder>(),
            sp.GetService<TimeProvider>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> CreateRequestAsync(ClaimsPrincipal caller, CreateConsentRequestBody body)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.AuthenticatedCaller)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.CreateRequestAsync(
            body,
            ctx,
            sp.GetRequiredService<IPermissionRequestEngine>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> ListRequestsAsync(ClaimsPrincipal caller)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.ConsentRead)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.ListRequestsAsync(
            ctx,
            sp.GetRequiredService<IConsentApprovalRepository>(),
            sp.GetRequiredService<IConsentWorkflowConfigRepository>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> ListGrantsAsync(
        ClaimsPrincipal caller,
        string? table = null,
        string? grantee = null,
        bool? includeInactive = null,
        string? kind = null)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.ConsentRead)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.ListGrantsAsync(
            ctx,
            sp.GetRequiredService<IConsentRepository>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>(),
            sp.GetService<TimeProvider>(),
            table: table,
            grantee: grantee,
            includeInactive: includeInactive,
            kind: kind);
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> GetGrantByIdAsync(ClaimsPrincipal caller, Guid grantId)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.ConsentRead)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.GetGrantByIdAsync(
            grantId,
            ctx,
            sp.GetRequiredService<IConsentRepository>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> GetRequestByIdAsync(ClaimsPrincipal caller, Guid requestId)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.ConsentRead)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.GetRequestByIdAsync(
            requestId,
            ctx,
            sp.GetRequiredService<IConsentApprovalRepository>(),
            sp.GetRequiredService<IConsentWorkflowConfigRepository>(),
            sp.GetService<IGrantEffectPreview>(),
            sp.GetService<IProfileAssignmentStore>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> WithdrawRequestAsync(ClaimsPrincipal caller, Guid requestId)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.AuthenticatedCaller)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.WithdrawRequestAsync(
            requestId,
            ctx,
            sp.GetRequiredService<IPermissionRequestEngine>(),
            sp.GetRequiredService<IConsentApprovalRepository>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> GetEventsAsync(ClaimsPrincipal caller, long since = 0, int limit = 100)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.ConsentRead)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.GetEventsAsync(
            ctx,
            sp.GetRequiredService<IConsentEventOutbox>(),
            sp.GetRequiredService<IConsentWorkflowConfigRepository>(),
            sp.GetService<IOptions<ConsentEventsOptions>>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>(),
            sp.GetService<TimeProvider>(),
            since,
            limit);
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> RevokeGrantAsync(
        ClaimsPrincipal caller,
        Guid grantId,
        string reason,
        string? idempotencyKey)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.GrantRevoke)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        if (idempotencyKey is not null)
        {
            ctx.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.RevokeGrantAsync(
            grantId,
            new RevokeConsentGrantBody(reason),
            ctx,
            sp.GetRequiredService<IConsentRepository>(),
            sp.GetRequiredService<IConsentApprovalRepository>(),
            sp.GetRequiredService<IConsentWorkflowConfigRepository>(),
            sp.GetService<IIdempotencyStore>(),
            sp.GetService<IConsentEventOutbox>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>(),
            sp.GetService<TimeProvider>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> EvaluateProofAsync(ClaimsPrincipal caller)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.GrantVerify)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.EvaluateProofAsync(
            ctx,
            sp.GetRequiredService<IGrantRecordExportService>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> GetGrantRecordBySeqAsync(ClaimsPrincipal caller, long seq)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.GrantVerify)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.GetGrantRecordBySeqAsync(
            seq,
            ctx,
            sp.GetRequiredService<IGrantRecordExportService>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> VerifyGrantRecordsRangeAsync(ClaimsPrincipal caller, GrantRecordVerifyRequest body)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.GrantVerify)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.VerifyGrantRecordsRangeAsync(
            body,
            ctx,
            sp.GetRequiredService<IGrantRecordExportService>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedEndpointResponse> GetGrantRecordKeysAsync(ClaimsPrincipal caller)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.GrantVerify)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller);
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var result = await ConsentApiEndpoints.GetGrantRecordKeysAsync(
            ctx,
            sp.GetRequiredService<IGrantRecordExportService>(),
            sp.GetService<IRateLimiterService>(),
            sp.GetService<IOptions<GovernanceOptions>>());
        return await ExecuteResultAsync(ctx, result);
    }

    public IReadOnlyList<RouteEndpoint> GetMappedRouteEndpoints()
    {
        var routeBuilder = new TestEndpointRouteBuilder(_services);
        routeBuilder.MapConsentApiEndpoints();
        return routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private DefaultHttpContext CreateHttpContext(ClaimsPrincipal principal) =>
        new()
        {
            RequestServices = _services,
            User = principal,
            Response = { Body = new MemoryStream() }
        };

    private static ExecutedEndpointResponse ForbiddenResponse() =>
        new(StatusCodes.Status403Forbidden, new HeaderDictionary(), string.Empty);

    private static async Task<ExecutedEndpointResponse> ExecuteResultAsync(DefaultHttpContext ctx, IResult result)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        var body = await reader.ReadToEndAsync();
        return new ExecutedEndpointResponse(ctx.Response.StatusCode, ctx.Response.Headers, body);
    }
}

internal sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider { get; } = serviceProvider;
    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
}

internal sealed class RecordingDenialRecorder : IConsentDecisionDenialRecorder
{
    public List<(string Reason, string TenantId)> Denials { get; } = new();
    public int Count => Denials.Count;

    public Task RecordAsync(ClaimsPrincipal? principal, string reasonClass, TenantId tenantId, CancellationToken ct = default)
    {
        Denials.Add((reasonClass, tenantId.Value));
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryTestIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class
    {
        if (!_entries.TryGetValue(key, out var json))
        {
            return Task.FromResult(default(T));
        }

        return Task.FromResult(JsonSerializer.Deserialize<T>(json, Opts));
    }

    public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class
    {
        var json = JsonSerializer.Serialize(value, Opts);
        return Task.FromResult(_entries.TryAdd(key, json));
    }
}

/// <summary>External decision double: the evidence/engine path is covered by ExternalDecisionEngineTests; this only applies the CAS and one grant.</summary>
internal sealed class HarnessExternalDecisionService(InMemoryConsentStore store, TimeProvider clock) : IExternalDecisionService
{
    public async Task<PermissionDecisionResult> DecideAsync(ExternalDecisionEnvelope envelope, TenantId tenant, CancellationToken ct = default)
    {
        var existing = store.Requests.FirstOrDefault(r => r.Id == envelope.RequestId);
        if (existing is null || existing.TenantId != tenant)
        {
            throw new ConsentDecisionException("NOT_FOUND", "Permission request was not found.", 404);
        }

        if (existing.Version != envelope.ExpectedVersion || !existing.Status.StartsWith("PENDING", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConsentDecisionException("REQUEST_ALREADY_DECIDED", "Permission request has already been decided.", 409);
        }

        var now = clock.GetUtcNow();
        var approver = new Sid(envelope.Approver.Subject);
        var step = new ApprovalStep { Id = Guid.NewGuid(), StepNumber = 1, ApproverSid = approver, Decision = envelope.Approve ? "APPROVED" : "REJECTED" };
        if (!envelope.Approve)
        {
            var rejected = await store.RejectExternalConsentRequestAsync(
                existing.Id, new ExternalDecisionAttribution(envelope.ExternalSystemId, envelope.ExternalReference, "REJECT", envelope.Approver, now), approver, envelope.Reason ?? "rejected", ct);
            return new PermissionDecisionResult(rejected, []);
        }

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = existing.TenantId,
            TableIdentifier = existing.TableIdentifier,
            ConsentRequestId = existing.Id,
            GranteeType = existing.RequestedGranteeType,
            GranteeSid = existing.RequesterSid,
            ValidFrom = now,
            ValidTo = existing.RequestedValidTo
        };
        await store.ExecuteExternalAtomicApprovalWithConsentAsync(
            existing.Id, new ExternalDecisionAttribution(envelope.ExternalSystemId, envelope.ExternalReference, "APPROVE", envelope.Approver, now), step, consent, [], [], ct);
        var updated = store.Requests.First(r => r.Id == existing.Id);
        return new PermissionDecisionResult(updated, [new GrantRef(consent.Id, PermissionGrantKind.Data, ValidFrom: now, ValidTo: consent.ValidTo)]);
    }
}
