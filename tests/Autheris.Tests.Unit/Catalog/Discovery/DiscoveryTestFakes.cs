namespace Autheris.Tests.Unit.Catalog.Discovery;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

internal static class DiscoveryTestBuilder
{
    public static ClaimsPrincipal User(
        string sid,
        string? tenant,
        string[]? roles = null,
        string? scopes = null,
        string authType = "Bearer")
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new(ClaimTypes.NameIdentifier, sid) };
        if (tenant != null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        if (!string.IsNullOrWhiteSpace(scopes))
        {
            claims.Add(new Claim("scp", scopes));
        }

        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.Add(Autheris.Domain.Identity.GroupResolutionClaims.CreateMarker());
        claims.Add(Autheris.Domain.Identity.GroupResolutionClaims.CreateStatus(Autheris.Domain.Identity.GroupResolutionStatus.Complete));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authType));
    }

    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    public static CatalogCaller Caller(ClaimsPrincipal principal, TenantId? tenant = null, CatalogChannel channel = CatalogChannel.Rest)
        => new(principal, tenant ?? new TenantId("tenant-a"), channel);

    public static TableMetadata MakeTable(
        string domain,
        string name,
        string? ownerTenantId = "tenant-a",
        bool hidden = false,
        bool samplesDisabled = false,
        bool virtualTable = false,
        DiscoveryObjectKind objectKind = DiscoveryObjectKind.Table,
        string schema = "dbo",
        params (string Name, bool Sensitive, bool Hidden)[] columns)
    {
        var cols = columns.Length == 0
            ? new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "int" },
                new() { ColumnName = "email", DataType = "varchar" }
            }
            : columns.Select(c => new TableColumn
            {
                ColumnName = c.Name,
                DataType = "varchar",
                DiscoveryHidden = c.Hidden
            }).ToList();

        var effectiveSchema = virtualTable && ownerTenantId != null
            ? SemanticVirtualTables.SchemaFor(new TenantId(ownerTenantId))
            : schema;
        var effectiveDomain = virtualTable ? SemanticVirtualTables.Domain : domain;

        return new TableMetadata
        {
            Identifier = new TableIdentifier(effectiveDomain, effectiveSchema, name),
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = effectiveDomain,
                SchemaName = effectiveSchema,
                TableName = name,
                DisplayName = name,
                Description = $"Description for {name}",
                DataSourceType = DataSourceType.Sql,
                IsActive = true,
                DiscoveryHidden = hidden,
                SamplesDisabled = samplesDisabled,
                DocumentationSource = virtualTable ? SemanticVirtualTables.SourceMarker : null,
                ObjectKind = objectKind
            },
            Columns = cols,
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(
                columns.Length == 0 ? new[] { "email" } : columns.Where(c => c.Sensitive).Select(c => c.Name).ToArray()),
            OwnerTenantId = ownerTenantId,
            CatalogTags = ["core"]
        };
    }

    public static DiscoveryService CreateService(
        FakeDiscoveryTableRepository? tables = null,
        FakeAccessDecisionPoint? pdp = null,
        FakeDiscoveryConsentRepository? consents = null,
        FakeDiscoveryApprovalRepository? approvals = null,
        IContainerOwnership? owners = null,
        FakeDiscoveryArtifactStore? artifacts = null,
        FakeAuditWriter? audit = null,
        GatewayOptions? gatewayOptions = null,
        CatalogDiscoveryOptions? discoveryOptions = null,
        IRateLimiterService? rateLimiter = null)
    {
        return new DiscoveryService(
            tables ?? new FakeDiscoveryTableRepository(),
            pdp ?? FakeAccessDecisionPoint.AllowAll(),
            consents ?? new FakeDiscoveryConsentRepository(),
            approvals ?? new FakeDiscoveryApprovalRepository(),
            owners ?? new FakeDiscoveryOwnershipRepository(),
            artifacts ?? new FakeDiscoveryArtifactStore(),
            audit ?? new FakeAuditWriter(),
            Options.Create(gatewayOptions ?? new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(discoveryOptions ?? new CatalogDiscoveryOptions()),
            rateLimiter,
            logger: NullLogger<DiscoveryService>.Instance);
    }
}

internal sealed class FakeDiscoveryTableRepository : ITableMetadataRepository
{
    public List<TableMetadata> Tables { get; } = [];
    public Dictionary<TableIdentifier, DatasetCatalogSettings> SettingsByTable { get; } = [];
    public Exception? FailWith { get; set; }
    public bool ThrowOnSettingsRead { get; set; }

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
    {
        if (FailWith != null) throw FailWith;
        return Task.FromResult(Tables.FirstOrDefault(t => t.Identifier.Equals(table)));
    }

    /// <summary>Number of whole-catalog reads (issue #1050: a single-object read must not need one).</summary>
    public int AllTablesReads { get; private set; }

    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
    {
        if (FailWith != null) throw FailWith;
        AllTablesReads++;
        return Task.FromResult<IReadOnlyList<TableMetadata>>(Tables.ToList());
    }

    public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
    public ValueTask<long> FlushCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(1L);

    public Task<DatasetCatalogSettings?> GetDatasetCatalogSettingsAsync(TableIdentifier table, bool strict = true, CancellationToken ct = default)
    {
        if (ThrowOnSettingsRead) throw new InvalidOperationException("Settings read failed.");
        return Task.FromResult(SettingsByTable.TryGetValue(table, out var s) ? s : null);
    }
}

internal sealed class FakeAccessDecisionPoint : IAccessDecisionPoint
{
    private readonly Func<AccessRequest, TableAccessDecision> _decide;

    public FakeAccessDecisionPoint(Func<AccessRequest, TableAccessDecision> decide) => _decide = decide;
    public int CallCount { get; private set; }
    public List<AccessRequest> Requests { get; } = [];

    public static FakeAccessDecisionPoint AllowAll() => new(r =>
        TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

    public static FakeAccessDecisionPoint DenyAll() => new(r =>
        TableAccessDecision.Denied(r.Object.Identifier, "Denied"));

    public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
    {
        CallCount++;
        Requests.Add(request);
        return ValueTask.FromResult(_decide(request));
    }

    public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default)
    {
        var results = objects.Select(o =>
        {
            var req = new AccessRequest(subject, o, operation, transport);
            CallCount++;
            Requests.Add(req);
            return _decide(req);
        }).ToList();
        return ValueTask.FromResult<IReadOnlyList<TableAccessDecision>>(results);
    }

    public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeDiscoveryConsentRepository : IConsentRepository
{
    public IReadOnlySet<TableIdentifier> RecentlyExpired { get; set; } = new HashSet<TableIdentifier>();
    public bool ThrowOnRecentlyExpired { get; set; }

    public Task<IReadOnlySet<TableIdentifier>> GetRecentlyExpiredTableIdsAsync(Sid userSid, TenantId tenantId, DateTimeOffset since, CancellationToken ct = default)
    {
        if (ThrowOnRecentlyExpired) throw new InvalidOperationException("DB down");
        return Task.FromResult(RecentlyExpired);
    }

    /// <summary>As the SQL repository: the subjects' consents on <paramref name="table"/> plus their pattern consents (the policy narrows them).</summary>
    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        var sids = subjects.ToHashSet();
        return Task.FromResult<IReadOnlyList<Consent>>(Active
            .Where(c => (c.IsPatternGrant || c.TableIdentifier == table) && IsSubjectConsent(c, sids, tenantId, atTime))
            .ToList());
    }
    /// <summary>Active consents of every subject; the subject reads return those granted to one of the requested SIDs (and tenant).</summary>
    public List<Consent> Active { get; } = [];
    public int AllActiveReads { get; private set; }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        AllActiveReads++;
        var sids = subjects.ToHashSet();
        var at = atTime ?? DateTimeOffset.UtcNow;
        return Task.FromResult<IReadOnlyList<Consent>>(Active.Where(c => IsSubjectConsent(c, sids, tenantId, at)).ToList());
    }

    public Task<DateTimeOffset?> GetNextScheduledAllowAsync(IEnumerable<Sid> subjects, DateTimeOffset atTime, TenantId tenantId, CancellationToken ct = default)
    {
        var sids = subjects.ToHashSet();
        var next = Active
            .Where(c => !c.IsRevoked && c.Effect == ConsentEffect.Allow && c.GrantKind == PermissionGrantKind.Data &&
                        c.TenantId == tenantId && c.ValidFrom > atTime && c.EffectiveValidTo > c.ValidFrom &&
                        c.GranteeSid is { } g && sids.Contains(g))
            .Select(static c => (DateTimeOffset?)c.ValidFrom)
            .OrderBy(static d => d)
            .FirstOrDefault();
        return Task.FromResult(next);
    }

    // As the SQL repository: active consents of the subjects, plus not-yet-valid Deny consents (fail closed).
    private static bool IsSubjectConsent(Consent c, HashSet<Sid> sids, TenantId? tenantId, DateTimeOffset at) =>
        c.GranteeSid is { } g && sids.Contains(g) && (tenantId is null || c.TenantId == tenantId.Value) &&
        (c.IsActive(at) || (c.Effect == ConsentEffect.Deny && c.ValidFrom > at));
    public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
    public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
    public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
    public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
    public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeDiscoveryApprovalRepository : IConsentApprovalRepository
{
    public List<ConsentRequest> Requests { get; } = [];
    public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ConsentRequest>>(Requests.Where(r => r.RequesterSid == requesterSid && (r.TenantId == default || r.TenantId == tenantId) && (status == null || r.Status == status)).ToList());
    public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ConsentRequest?> GetConsentRequestByExternalReferenceAsync(Guid externalSystemId, string externalReference, TenantId tenantId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeDiscoveryOwnershipRepository : IContainerOwnership
{
    public Dictionary<TableIdentifier, List<ContainerOwnerEntry>> Owners { get; } = [];
    public HashSet<TableIdentifier> OwnedTables { get; } = [];
    public Dictionary<TableIdentifier, HashSet<string>> AuthorizedApproversByTable { get; } = [];
    public int BatchApproverCalls { get; private set; }
    public int SingleApproverCalls { get; private set; }

    public Task<IReadOnlyList<ContainerOwnerEntry>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContainerOwnerEntry>>(Owners.TryGetValue(table, out var list) ? list : []);

    public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default)
    {
        SingleApproverCalls++;
        if (AuthorizedApproversByTable.TryGetValue(table, out var configured))
        {
            return Task.FromResult(configured.Contains(approverSid.Value));
        }

        return Task.FromResult(OwnedTables.Contains(table));
    }

    public Task<IReadOnlySet<TableIdentifier>> GetOwnedOrDelegatedTablesAsync(Sid actorSid, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, CancellationToken ct = default)
        => Task.FromResult<IReadOnlySet<TableIdentifier>>(OwnedTables.Where(tables.Contains).ToHashSet());

    public Task<IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>>> FilterAuthorizedApproversForTablesAsync(
        IReadOnlyCollection<TableMetadata> requested,
        IReadOnlyCollection<string> candidateApproverSids,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        BatchApproverCalls++;
        var result = new Dictionary<TableIdentifier, IReadOnlySet<string>>();
        foreach (var meta in requested)
        {
            if (result.ContainsKey(meta.Identifier))
            {
                continue;
            }

            if (AuthorizedApproversByTable.TryGetValue(meta.Identifier, out var configured))
            {
                result[meta.Identifier] = candidateApproverSids
                    .Where(configured.Contains)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else if (OwnedTables.Contains(meta.Identifier))
            {
                result[meta.Identifier] = candidateApproverSids
                    .Where(static s => !string.IsNullOrWhiteSpace(s))
                    .Select(static s => s.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                result[meta.Identifier] = System.Collections.Frozen.FrozenSet<string>.Empty;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>>>(result);
    }
}

internal sealed class FakeDiscoveryArtifactStore : IDiscoveryArtifactStore
{
    public Dictionary<Guid, DiscoveryArtifacts> Artifacts { get; } = [];
    public Dictionary<(Guid TableId, ArtifactProvenance Provenance), DiscoveryArtifacts> ScopedArtifacts { get; } = [];
    public bool FailOnRead { get; set; }

    public Task<DiscoveryArtifacts?> GetAsync(Guid tableId, CancellationToken ct = default)
    {
        if (FailOnRead) throw new InvalidOperationException("Artifact read failed.");
        return Task.FromResult(
            ScopedArtifacts.TryGetValue((tableId, ArtifactProvenance.Upload), out var up) ? up :
            ScopedArtifacts.TryGetValue((tableId, ArtifactProvenance.Unknown), out var unk) ? unk :
            ScopedArtifacts.TryGetValue((tableId, ArtifactProvenance.Import), out var imp) ? imp :
            Artifacts.TryGetValue(tableId, out var a) ? a : null);
    }

    public Task<DiscoveryArtifacts?> GetAsync(Guid tableId, ArtifactProvenance provenance, CancellationToken ct = default)
    {
        if (FailOnRead) throw new InvalidOperationException("Artifact read failed.");
        if (ScopedArtifacts.TryGetValue((tableId, provenance), out var a)) return Task.FromResult<DiscoveryArtifacts?>(a);
        return Task.FromResult(Artifacts.TryGetValue(tableId, out var legacy) && legacy.Provenance == provenance ? legacy : null);
    }

    public Task<IReadOnlyList<DiscoveryArtifacts>> GetAllAsync(Guid tableId, CancellationToken ct = default)
    {
        if (FailOnRead) throw new InvalidOperationException("Artifact read failed.");
        var items = ScopedArtifacts.Where(kv => kv.Key.TableId == tableId).Select(kv => kv.Value).ToList();
        if (items.Count == 0 && Artifacts.TryGetValue(tableId, out var legacy)) items.Add(legacy);
        return Task.FromResult<IReadOnlyList<DiscoveryArtifacts>>(items);
    }

    public Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default)
    {
        if (artifacts.Provenance == ArtifactProvenance.Import && !string.IsNullOrWhiteSpace(artifacts.StatisticsJson))
        {
            throw new ArgumentException("Import discovery artifacts must not carry statistics.", nameof(artifacts));
        }

        ScopedArtifacts[(artifacts.TableId, artifacts.Provenance)] = artifacts;
        Artifacts[artifacts.TableId] = artifacts;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid tableId, Sid actor, CancellationToken ct = default)
    {
        var keys = ScopedArtifacts.Keys.Where(k => k.TableId == tableId).ToList();
        var removed = Artifacts.Remove(tableId);
        foreach (var key in keys) removed |= ScopedArtifacts.Remove(key);
        return Task.FromResult(removed);
    }

    public Task<bool> DeleteAsync(Guid tableId, ArtifactProvenance provenance, Sid actor, CancellationToken ct = default)
    {
        var removed = ScopedArtifacts.Remove((tableId, provenance));
        if (Artifacts.TryGetValue(tableId, out var legacy) && legacy.Provenance == provenance)
        {
            removed |= Artifacts.Remove(tableId);
        }

        return Task.FromResult(removed);
    }
}

internal sealed class FakeAuditWriter : IAuditWriter
{
    public List<AuditLogEntry> Entries { get; } = [];
    public bool FailWithUnavailable { get; set; }

    public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        if (FailWithUnavailable) throw new AuditUnavailableException("Audit offline");
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        if (FailWithUnavailable) throw new AuditUnavailableException("Audit offline");
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDiscoveryRateLimiter : IRateLimiterService
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    private int Increment(string key)
    {
        var count = _counts.TryGetValue(key, out var current) ? current + 1 : 1;
        _counts[key] = count;
        return count;
    }

    public Task<RateLimitResult> CheckPostAuthSidAsync(string sid, PostAuthSidRateLimitOptions options, CancellationToken ct = default)
    {
        var count = Increment(sid);
        return Task.FromResult(count > options.TokenBucketCapacity
            ? new RateLimitResult(false, 60)
            : new RateLimitResult(true, 0));
    }

    public Task<RateLimitResult> CheckPreAuthIpAsync(string ip, PreAuthIpRateLimitOptions options, CancellationToken ct = default) => Task.FromResult(new RateLimitResult(true, 0));

    public Task<CostQuotaResult> CheckCostQuotaAsync(string key, int requestedCost, ClientQuotaPolicy policy, CancellationToken ct = default)
    {
        var count = Increment(key);
        return Task.FromResult(count > policy.MaxTokensCapacity
            ? new CostQuotaResult(false, 0, 60)
            : new CostQuotaResult(true, Math.Max(0, policy.MaxTokensCapacity - count), 0));
    }
    public Task<CostQuotaResult> ReserveCostQuotaAsync(string key, int reservedCost, ClientQuotaPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CostQuotaResult> SettleCostQuotaAsync(string key, int reservedCost, int actualCost, ClientQuotaPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CallerBucketState?> GetBucketStateAsync(string key, ClientQuotaPolicy policy, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CallerBucketState?> GetBucketStateAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();
}
