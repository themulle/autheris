namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.External;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Autheris.Tests.Unit.ConsentRequests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

internal sealed record ExecutedCatalogResponse(
    int StatusCode,
    IHeaderDictionary Headers,
    string Body)
{
    public JsonElement ReadJson() => JsonSerializer.Deserialize<JsonElement>(Body);
}

internal sealed class ExternalCatalogTestHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services;

    private ExternalCatalogTestHarness(
        ServiceProvider services,
        FakeTimeProvider clock,
        FakeDiscoveryTableRepository tables,
        MutableAccessDecisionPoint pdp,
        InMemoryExternalConsentRepository consents,
        InMemoryExternalApprovalRepository approvals,
        PerUserOwnershipRepository owners,
        FakeDiscoveryArtifactStore artifacts,
        InMemoryExternalWorkflowRepository workflowRepo,
        InMemoryCatalogChangeStore changeStore,
        CatalogChangedSignal changedSignal,
        GatewayOptions gatewayOptions)
    {
        _services = services;
        Clock = clock;
        Tables = tables;
        Pdp = pdp;
        Consents = consents;
        Approvals = approvals;
        Owners = owners;
        Artifacts = artifacts;
        WorkflowRepo = workflowRepo;
        ChangeStore = changeStore;
        ChangedSignal = changedSignal;
        GatewayOptions = gatewayOptions;
    }

    public FakeTimeProvider Clock { get; }
    public FakeDiscoveryTableRepository Tables { get; }
    public MutableAccessDecisionPoint Pdp { get; }
    public InMemoryExternalConsentRepository Consents { get; }
    public InMemoryExternalApprovalRepository Approvals { get; }
    public PerUserOwnershipRepository Owners { get; }
    public FakeDiscoveryArtifactStore Artifacts { get; }
    public InMemoryExternalWorkflowRepository WorkflowRepo { get; }
    public InMemoryCatalogChangeStore ChangeStore { get; }
    public CatalogChangedSignal ChangedSignal { get; }
    public GatewayOptions GatewayOptions { get; }

    public static ExternalCatalogTestHarness Create(bool multiTenant = true)
    {
        var clock = new FakeTimeProvider(FixedNow);
        var tables = new FakeDiscoveryTableRepository();
        var pdp = new MutableAccessDecisionPoint();
        var consents = new InMemoryExternalConsentRepository();
        var approvals = new InMemoryExternalApprovalRepository();
        var owners = new PerUserOwnershipRepository();
        var artifacts = new FakeDiscoveryArtifactStore();
        var audit = new FakeAuditWriter();
        var workflowRepo = new InMemoryExternalWorkflowRepository();
        var changeStore = new InMemoryCatalogChangeStore();
        var changedSignal = new CatalogChangedSignal();

        var gatewayOptions = new GatewayOptions
        {
            Tenancy = multiTenant ? TenancyShape.Multi.Options() : TenancyShape.Single.Options()
        };

        var discoveryService = new DiscoveryService(
            tables,
            pdp,
            consents,
            approvals,
            owners,
            artifacts,
            audit,
            Options.Create(gatewayOptions),
            Options.Create(new CatalogDiscoveryOptions()),
            rateLimiter: null,
            logger: NullLogger<DiscoveryService>.Instance);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<ITableMetadataRepository>(tables);
        services.AddSingleton<IAccessDecisionPoint>(pdp);
        services.AddSingleton<IConsentRepository>(consents);
        services.AddSingleton<IConsentApprovalRepository>(approvals);
        services.AddSingleton<IDataOwnershipRepository>(owners);
        services.AddSingleton<IDiscoveryArtifactStore>(artifacts);
        services.AddSingleton<IConsentWorkflowConfigRepository>(workflowRepo);
        services.AddSingleton<ICatalogChangeStore>(changeStore);
        services.AddSingleton<ICatalogChangedSignal>(changedSignal);
        services.AddSingleton<IDiscoveryService>(discoveryService);
        services.AddSingleton(Options.Create(gatewayOptions));
        services.AddSingleton<IIdentitySubjectResolver, IdentitySubjectResolver>();
        services.AddSingleton<IConsentDecisionGuard>(sp =>
            new ConsentDecisionGuard(sp.GetRequiredService<IIdentitySubjectResolver>()));
        services.AddSingleton<IRateLimiterService>(new FakeDiscoveryRateLimiter());
        services.AddSingleton<ExternalCatalogReader>();
        services.AddSingleton<IAuthorizationHandler, ExternalSystemRequirementHandler>();
        services.AddAuthorization(options => options.AddGatewayAuthorizationPolicies());

        return new ExternalCatalogTestHarness(
            services.BuildServiceProvider(),
            clock,
            tables,
            pdp,
            consents,
            approvals,
            owners,
            artifacts,
            workflowRepo,
            changeStore,
            changedSignal,
            gatewayOptions);
    }

    public CatalogChangeIndexerHostedService CreateIndexer() =>
        new(
            Tables,
            Consents,
            Owners,
            ChangeStore,
            Options.Create(GatewayOptions),
            Artifacts,
            ChangedSignal,
            eventBus: null,
            Clock,
            NullLogger<CatalogChangeIndexerHostedService>.Instance);

    public Guid RegisterExternalSystem(string tenantId, string clientId, bool enabled)
    {
        var id = Guid.NewGuid();
        WorkflowRepo.Systems.Add(new ExternalWorkflowSystem(
            id,
            tenantId,
            clientId,
            clientId,
            "https://example.invalid/hook",
            "secret-ref",
            enabled,
            "admin",
            Clock.GetUtcNow(),
            enabled ? null : Clock.GetUtcNow()));
        return id;
    }

    public TableMetadata AddTable(
        string domain,
        string name,
        string? ownerTenantId = "tenant-a",
        bool hidden = false,
        string? schema = "dbo",
        DiscoveryObjectKind kind = DiscoveryObjectKind.Table,
        string sensitivity = "NORMAL",
        params (string Name, bool Sensitive, bool Hidden)[] columns)
    {
        var baseTable = DiscoveryTestBuilder.MakeTable(
            domain,
            name,
            ownerTenantId: ownerTenantId,
            hidden: hidden,
            objectKind: kind,
            schema: schema ?? "dbo",
            columns: columns);
        var table = new TableMetadata
        {
            Identifier = baseTable.Identifier,
            OwnerTenantId = baseTable.OwnerTenantId,
            CatalogTags = baseTable.CatalogTags,
            Columns = baseTable.Columns,
            Table = new Table
            {
                Id = baseTable.Table.Id,
                SourceName = baseTable.Table.SourceName,
                SchemaName = baseTable.Table.SchemaName,
                TableName = baseTable.Table.TableName,
                DisplayName = baseTable.Table.DisplayName,
                Description = baseTable.Table.Description,
                DataSourceType = baseTable.Table.DataSourceType,
                IsActive = true,
                DiscoveryHidden = hidden,
                ObjectKind = kind,
                Sensitivity = sensitivity
            }
        };
        Tables.Tables.Add(table);
        return table;
    }

    public static ClaimsPrincipal BuildAppPrincipal(string? tenantId, string clientId, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("idtyp", "app"),
            new("azp", clientId),
            new(ClaimTypes.NameIdentifier, clientId)
        };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.NameIdentifier, ClaimTypes.Role));
    }

    public static ClaimsPrincipal BuildDelegatedUserPrincipal(
        string? tenantId,
        string userSid,
        string? scopes = null,
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, userSid),
            new(ClaimTypes.NameIdentifier, userSid),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker(),
            Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(
                Autheris.Domain.Identity.GroupResolutionStatus.Complete)
        };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        if (!string.IsNullOrWhiteSpace(scopes))
        {
            claims.Add(new Claim("scp", scopes));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.NameIdentifier, ClaimTypes.Role));
    }

    public async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal principal, string policyName)
    {
        await using var scope = _services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        return await auth.AuthorizeAsync(principal, resource: null, policyName: policyName);
    }

    public async Task<ExecutedCatalogResponse> ListObjectsAsync(
        ClaimsPrincipal caller,
        string? kind = null,
        string? domain = null,
        string? schema = null,
        string? owner = null,
        string? tag = null,
        string? sensitivity = null,
        bool? pii = null,
        long? changedSince = null,
        string? cursor = null,
        int? pageSize = null,
        string? ifNoneMatch = null,
        string? accessStatus = null)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.CatalogRead)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller, ifNoneMatch);
        var reader = _services.GetRequiredService<ExternalCatalogReader>();
        var result = await ExternalCatalogApiEndpoints.ListObjectsAsync(
            ctx,
            reader,
            kind,
            domain,
            schema,
            owner,
            tag,
            sensitivity,
            pii,
            changedSince,
            cursor,
            pageSize,
            accessStatus,
            CancellationToken.None);
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedCatalogResponse> GetObjectByIdAsync(
        ClaimsPrincipal caller,
        string id,
        string? ifNoneMatch = null)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.CatalogRead)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller, ifNoneMatch);
        var reader = _services.GetRequiredService<ExternalCatalogReader>();
        var result = await ExternalCatalogApiEndpoints.GetObjectByIdAsync(
            id,
            ctx,
            reader,
            CancellationToken.None);
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedCatalogResponse> GetChangesAsync(
        ClaimsPrincipal caller,
        long since = 0,
        int limit = 100,
        string? ifNoneMatch = null)
    {
        if (!(await AuthorizeAsync(caller, GatewayPolicies.CatalogRead)).Succeeded)
        {
            return ForbiddenResponse();
        }

        var ctx = CreateHttpContext(caller, ifNoneMatch);
        var reader = _services.GetRequiredService<ExternalCatalogReader>();
        var result = await ExternalCatalogApiEndpoints.GetChangesAsync(
            ctx,
            reader,
            since,
            limit,
            CancellationToken.None);
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<ExecutedCatalogResponse> GetExternalOpenApiSpecAsync(
        ClaimsPrincipal caller,
        string? ifNoneMatch = null)
    {
        if (caller.Identity?.IsAuthenticated != true)
        {
            return new ExecutedCatalogResponse(StatusCodes.Status401Unauthorized, new HeaderDictionary(), string.Empty);
        }

        var ctx = CreateHttpContext(caller, ifNoneMatch);
        var result = ExternalCatalogApiEndpoints.GetExternalOpenApiSpec(ctx);
        return await ExecuteResultAsync(ctx, result);
    }

    public async Task<IReadOnlyList<RouteEndpoint>> GetAllExternalV1RouteEndpointsAsync()
    {
        await using var consentHarness = ConsentApiTestHarness.Create();
        var endpoints = new List<RouteEndpoint>();
        endpoints.AddRange(consentHarness.GetMappedRouteEndpoints());

        var routeBuilder = new CatalogTestEndpointRouteBuilder(_services);
        routeBuilder.MapExternalCatalogApiEndpoints();
        endpoints.AddRange(routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>());
        return endpoints;
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private DefaultHttpContext CreateHttpContext(ClaimsPrincipal principal, string? ifNoneMatch)
    {
        var ctx = new DefaultHttpContext
        {
            RequestServices = _services,
            User = principal,
            Response = { Body = new MemoryStream() }
        };
        if (ifNoneMatch is not null)
        {
            ctx.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        return ctx;
    }

    private static ExecutedCatalogResponse ForbiddenResponse() =>
        new(StatusCodes.Status403Forbidden, new HeaderDictionary(), string.Empty);

    private static async Task<ExecutedCatalogResponse> ExecuteResultAsync(DefaultHttpContext ctx, IResult result)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        var body = await reader.ReadToEndAsync();
        return new ExecutedCatalogResponse(ctx.Response.StatusCode, ctx.Response.Headers, body);
    }
}

internal sealed class CatalogTestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
{
    public IServiceProvider ServiceProvider { get; } = serviceProvider;
    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
}

internal sealed class MutableAccessDecisionPoint : IAccessDecisionPoint
{
    public Func<AccessRequest, TableAccessDecision> Decide { get; set; } = req =>
        TableAccessDecision.Allowed(req.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

    public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) =>
        ValueTask.FromResult(Decide(request));

    public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
        SecurityPrincipalContext subject,
        IReadOnlyList<TableMetadata> objects,
        AccessOperation operation,
        AccessTransport transport,
        CancellationToken ct = default)
    {
        var results = objects.Select(o => Decide(new AccessRequest(subject, o, operation, transport))).ToList();
        return ValueTask.FromResult<IReadOnlyList<TableAccessDecision>>(results);
    }

    public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class InMemoryExternalConsentRepository : IConsentRepository, IConsentDescriptionRepository
{
    public List<Consent> Consents { get; } = [];
    public int DescriptionLoadCalls { get; private set; }
    public int ConsentReadCalls { get; private set; }
    public bool FailDescriptionLoad { get; set; }

    public Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default)
    {
        DescriptionLoadCalls++;
        return FailDescriptionLoad ? throw new InvalidOperationException("description store down") : Task.CompletedTask;
    }

    public Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates, CancellationToken ct) =>
        throw new NotSupportedException();
    public Dictionary<string, HashSet<TableIdentifier>> RecentlyExpiredByUser { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlySet<TableIdentifier>> GetRecentlyExpiredTableIdsAsync(
        Sid userSid,
        TenantId tenantId,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlySet<TableIdentifier>>(
            RecentlyExpiredByUser.TryGetValue(userSid.Value, out var set)
                ? set
                : new HashSet<TableIdentifier>());
    }

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
        => GetActiveConsentsForSubjectsAsync(subjects, table, atTime, null, ct);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset atTime,
        TenantId? tenantId,
        CancellationToken ct = default)
    {
        var sidSet = subjects.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = Consents.Where(c =>
                c.TableIdentifier.Equals(table)
                && !c.IsRevoked
                && c.ValidFrom <= atTime
                && c.ValidTo > atTime
                && (!tenantId.HasValue || c.TenantId == tenantId.Value)
                && (c.GranteeSid is not null && sidSet.Contains(c.GranteeSid.Value)))
            .ToList();
        return Task.FromResult<IReadOnlyList<Consent>>(list);
    }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default)
        => GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, null, ct);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        IEnumerable<string>? roles,
        DateTimeOffset? atTime,
        TenantId? tenantId,
        CancellationToken ct = default)
    {
        ConsentReadCalls++;
        var sidSet = subjects.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = Consents.Where(c =>
                !c.IsRevoked
                && (!atTime.HasValue || (c.ValidFrom <= atTime.Value && c.ValidTo > atTime.Value))
                && (!tenantId.HasValue || c.TenantId == tenantId.Value)
                && c.GranteeSid is not null && sidSet.Contains(c.GranteeSid.Value))
            .ToList();
        return Task.FromResult<IReadOnlyList<Consent>>(list);
    }

    public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
    {
        var filtered = Consents.Where(c => c.TenantId == query.Tenant && (query.IncludeInactive || !c.IsRevoked)).ToList();
        return Task.FromResult(new ConsentPage(filtered, null));
    }

    public Task<IReadOnlySet<TableIdentifier>> GetTablesWithConsentsForTenantAsync(TenantId tenantId, CancellationToken ct = default)
    {
        var set = Consents.Where(c => c.TenantId == tenantId && !c.IsRevoked).Select(c => c.TableIdentifier).ToHashSet();
        return Task.FromResult<IReadOnlySet<TableIdentifier>>(set);
    }

    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult(Consents.FirstOrDefault(c => c.Id == consentId));
    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class InMemoryExternalApprovalRepository : IConsentApprovalRepository
{
    public List<ConsentRequest> Requests { get; } = [];

    public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, string? status = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConsentRequest>>(Requests.Where(r => r.RequesterSid == requesterSid && (status is null || r.Status == status)).ToList());

    public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConsentRequest?> GetConsentRequestByExternalReferenceAsync(Guid externalSystemId, string externalReference, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class PerUserOwnershipRepository : IDataOwnershipRepository
{
    private readonly Dictionary<TableIdentifier, List<DataOwner>> _owners = [];
    private readonly Dictionary<string, HashSet<TableIdentifier>> _ownedBySid = new(StringComparer.OrdinalIgnoreCase);

    public void AssignOwner(TableIdentifier table, string ownerSid, string displayName, string? email = null)
    {
        if (!_owners.TryGetValue(table, out var list))
        {
            list = [];
            _owners[table] = list;
        }

        list.Add(new DataOwner
        {
            Id = Guid.NewGuid(),
            AdSid = new Sid(ownerSid),
            AdAccount = ownerSid,
            DisplayName = displayName,
            Email = email ?? $"{ownerSid}@example.test"
        });

        if (!_ownedBySid.TryGetValue(ownerSid, out var owned))
        {
            owned = [];
            _ownedBySid[ownerSid] = owned;
        }

        owned.Add(table);
    }

    public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DataOwner>>(_owners.TryGetValue(table, out var list) ? list : []);

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
        Task.FromResult(_ownedBySid.TryGetValue(approverSid.Value, out var owned) && owned.Contains(table));

    public Task<IReadOnlySet<TableIdentifier>> GetOwnedOrDelegatedTablesAsync(
        Sid actorSid,
        IReadOnlyCollection<TableIdentifier> tables,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        if (!_ownedBySid.TryGetValue(actorSid.Value, out var owned))
        {
            return Task.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>());
        }

        return Task.FromResult<IReadOnlySet<TableIdentifier>>(owned.Where(tables.Contains).ToHashSet());
    }

    public Task<DataOwner> RegisterDataOwnerAsync(DataOwner owner, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AssignTableOwnerAsync(TableIdentifier table, Guid ownerId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<TableIdentifier>> GetTablesOwnedOrDelegatedAsync(Sid userSid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TableIdentifier>>(_ownedBySid.TryGetValue(userSid.Value, out var owned) ? owned.ToList() : []);
    public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class InMemoryExternalWorkflowRepository : IConsentWorkflowConfigRepository
{
    public List<ExternalWorkflowSystem> Systems { get; } = [];

    public Task<ConsentWorkflowRoute?> ResolveWorkflowRouteAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default) =>
        Task.FromResult<ConsentWorkflowRoute?>(null);

    public Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemAsync(TenantId tenantId, Guid systemId, CancellationToken ct = default) =>
        Task.FromResult(Systems.FirstOrDefault(s => s.TenantId == tenantId && s.Id == systemId));

    public Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemByClientIdAsync(TenantId tenantId, string clientId, CancellationToken ct = default) =>
        Task.FromResult(Systems.FirstOrDefault(s =>
            s.TenantId == tenantId && string.Equals(s.ClientId, clientId, StringComparison.OrdinalIgnoreCase)));
}

internal sealed class InMemoryCatalogChangeStore : ICatalogChangeStore
{
    private readonly Dictionary<(string TenantId, string ObjectId), CatalogObjectVersionRow> _versions = [];
    private readonly Dictionary<string, List<CatalogChangeRecord>> _changesByTenant = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _nextSeqByTenant = new(StringComparer.Ordinal);

    public HashSet<string> ExtraTenantIds { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, CatalogObjectVersionRow>> GetObjectVersionsAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        var dict = _versions
            .Where(kv => string.Equals(kv.Key.TenantId, tenantId.Value, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key.ObjectId, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyDictionary<string, CatalogObjectVersionRow>>(dict);
    }

    public Task<long> ApplyVersionChangesAsync(
        TenantId tenantId,
        IReadOnlyList<CatalogVersionUpsert> upserts,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
    {
        if (!_changesByTenant.TryGetValue(tenantId.Value, out var list))
        {
            list = [];
            _changesByTenant[tenantId.Value] = list;
        }

        var seq = _nextSeqByTenant.TryGetValue(tenantId.Value, out var existingSeq) ? existingSeq : 0L;
        foreach (var change in upserts)
        {
            seq++;
            _versions[(tenantId.Value, change.ObjectId)] = new CatalogObjectVersionRow(
                change.ObjectId,
                change.ContentHash,
                seq,
                change.Removed);
            list.Add(new CatalogChangeRecord(
                change.ObjectId,
                change.Removed ? "removed" : "upsert",
                seq,
                changedAt));
        }

        _nextSeqByTenant[tenantId.Value] = seq;
        return Task.FromResult(seq);
    }

    public Task<CatalogChangesFeedResult> ReadCatalogChangesAsync(
        TenantId tenantId,
        long sinceSeq,
        int limit,
        CancellationToken ct = default)
    {
        if (!_changesByTenant.TryGetValue(tenantId.Value, out var all))
        {
            return Task.FromResult(new CatalogChangesFeedResult([], 0));
        }

        var headSeq = _nextSeqByTenant.TryGetValue(tenantId.Value, out var s) ? s : 0L;
        var items = all.Where(c => c.Seq > sinceSeq).OrderBy(c => c.Seq).Take(limit).ToList();
        return Task.FromResult(new CatalogChangesFeedResult(items, headSeq));
    }

    public Task<IReadOnlyList<TenantId>> ListKnownTenantIdsAsync(CancellationToken ct = default)
    {
        var set = _versions.Keys
            .Select(k => k.TenantId)
            .Concat(ExtraTenantIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(static id => new TenantId(id))
            .ToList();
        return Task.FromResult<IReadOnlyList<TenantId>>(set);
    }
}
