using System.Diagnostics;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Protection;
using Autheris.Application.Products;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

using Microsoft.Extensions.Options;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Options;

namespace Autheris.Application.Governance.Domains;

public enum DomainServiceStatus
{
    Ok = 0,
    Created = 1,
    Deleted = 2,
    Forbidden = 3,
    NotFound = 4,
    Conflict = 5,
    VersionMismatch = 6,
    Invalid = 7
}

public sealed record DomainServiceResult<T>(
    DomainServiceStatus Status,
    T? Value = default,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record CreateGovernanceDomainInput(
    string Name,
    string? Description = null,
    string? Contact = null,
    string? OwnerGroupSid = null,
    ProtectionClass? DefaultClass = null,
    CatalogVisibilityMode? Visibility = null);

public sealed record UpdateGovernanceDomainInput(
    string Name,
    string? Description = null,
    string? Contact = null,
    ProtectionClass? DefaultClass = null,
    CatalogVisibilityMode? Visibility = null);

public sealed record ProtectionWriteOutcome(
    string DatasetId,
    Guid TableId,
    string? Column,
    EffectiveProtection Effective,
    int CoveringGrantsCount = 0,
    bool LooserThanContainer = false);

/// <summary>
/// Authoritative application service for governance domains, owner groups, domain table assignments,
/// and table/column protection classifications.
/// </summary>
public sealed partial class GovernanceDomainService(
    IGovernanceDomainRepository domains,
    ITableMetadataRepository tables,
    IConsentRepository consents,
    IAuditWriter auditWriter,
    IGovernanceCapabilities capabilities,
    IAccessPolicySettingsProvider? policyProvider = null,
    IGovernanceCacheInvalidator? cacheInvalidator = null,
    TimeProvider? timeProvider = null,
    IDataProductRepository? products = null,
    IOptions<GatewayOptions>? gatewayOptions = null,
    IGovernanceWriteInvalidator? writeInvalidator = null)
{
    private static readonly string[] SafeContactSchemes = ["https", "http", "mailto"];
    private static readonly string[] DangerousContactPrefixes = ["javascript:", "vbscript:", "data:", "file:"];

    private readonly IGovernanceDomainRepository _domains = domains ?? throw new ArgumentNullException(nameof(domains));
    private readonly ITableMetadataRepository _tables = tables ?? throw new ArgumentNullException(nameof(tables));
    private readonly IConsentRepository _consents = consents ?? throw new ArgumentNullException(nameof(consents));
    private readonly IAuditWriter _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    private readonly IGovernanceCapabilities _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    private readonly IAccessPolicySettingsProvider? _policyProvider = policyProvider;
    private readonly IGovernanceWriteInvalidator _writeInvalidator = writeInvalidator ?? new GovernanceWriteInvalidator(
        cacheInvalidators: cacheInvalidator is null ? null : () => [cacheInvalidator],
        timeProvider: timeProvider);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IDataProductRepository? _products = products;
    private readonly GatewayOptions? _gatewayOptions = gatewayOptions?.Value;

    internal IAtomicGovernanceDomainRepository? AtomicRepository => _domains as IAtomicGovernanceDomainRepository;

    private Task RunAtomicAsync(Func<CancellationToken, Task> action, CancellationToken ct) =>
        _domains is IAtomicGovernanceDomainRepository atomic ? atomic.ExecuteInTransactionAsync(action, ct) : action(ct);

    private async Task<DomainServiceResult<T>> RunAtomicWriteAsync<T>(
        Func<CancellationToken, Task<(DomainServiceResult<T> Result, Func<CancellationToken, Task>? Audit)>> work,
        CancellationToken ct)
    {
        DomainServiceResult<T> outcome = default!;
        await RunAtomicAsync(async innerCt =>
        {
            var (res, audit) = await work(innerCt).ConfigureAwait(false);
            outcome = res;
            if (audit is not null) await audit(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return outcome;
    }

    public async Task<DomainServiceResult<IReadOnlyList<GovernanceDomain>>> ListDomainsAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return Forbidden<IReadOnlyList<GovernanceDomain>>("Only Data Governance administrators can manage governance domains.");
        }

        if (offset is < 0)
        {
            return Invalid<IReadOnlyList<GovernanceDomain>>("INVALID_PAGING", "Offset must not be negative.");
        }

        if (limit is <= 0)
        {
            return Invalid<IReadOnlyList<GovernanceDomain>>("INVALID_PAGING", "Limit must be positive.");
        }

        var maxPageSize = await ResolveMaxPageSizeAsync(tenant, ct).ConfigureAwait(false);
        var effectiveLimit = Math.Min(limit ?? 100, Math.Min(maxPageSize, IGovernanceDomainRepository.MaxPageSize));

        await _domains.EnsureUnassignedAsync(tenant, ct).ConfigureAwait(false);
        var list = await _domains.ListAsync(tenant, effectiveLimit, offset ?? 0, ct).ConfigureAwait(false);
        return Ok<IReadOnlyList<GovernanceDomain>>(list);
    }

    public async Task<DomainServiceResult<GovernanceDomain>> GetDomainAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        Guid domainId,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return Forbidden<GovernanceDomain>("Only Data Governance administrators can read governance domains.");
        }

        if (domainId == Guid.Empty)
        {
            await _domains.EnsureUnassignedAsync(tenant, ct).ConfigureAwait(false);
        }

        var found = await _domains.GetAsync(tenant, domainId, ct).ConfigureAwait(false);
        return found is null
            ? NotFound<GovernanceDomain>("DOMAIN_NOT_FOUND", "Governance domain was not found.")
            : Ok(found);
    }

    public async Task<DomainServiceResult<GovernanceDomain>> CreateDomainAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        CreateGovernanceDomainInput cmd,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return Forbidden<GovernanceDomain>("Only Data Governance administrators can create governance domains.");
        }

        ArgumentNullException.ThrowIfNull(cmd);
        if (!TryValidateName(cmd.Name, out var name, out var err)
            || !TryValidateBoundedOptional(cmd.Description, 1024, "description", out var description, out err)
            || !TryValidateContact(cmd.Contact, out var contact, out err)
            || !TryValidateOptionalSid(cmd.OwnerGroupSid, out var ownerGroupSid, out err)
            || !TryValidateOptionalClass(cmd.DefaultClass, out var defaultClass, out err)
            || !TryValidateOptionalVisibility(cmd.Visibility, out var parsedVisibility, out err))
        {
            return Invalid<GovernanceDomain>("INVALID_DOMAIN", err!);
        }

        var effectiveVisibility = parsedVisibility ?? await ResolveDefaultVisibilityAsync(tenant, ct).ConfigureAwait(false);
        var domain = new GovernanceDomain(
            Id: Guid.NewGuid(),
            Tenant: tenant,
            Name: name!,
            Description: description,
            Contact: contact,
            OwnerGroupSid: ownerGroupSid,
            DefaultClass: defaultClass,
            Visibility: effectiveVisibility,
            IsSystemUnassigned: false,
            Version: 0,
            CreatedAt: _time.GetUtcNow().ToUniversalTime(),
            UpdatedAt: _time.GetUtcNow().ToUniversalTime());

        var outcome = await RunAtomicWriteAsync<GovernanceDomain>(async innerCt =>
        {
            var result = await _domains.UpsertAsync(domain, expectedVersion: 0, caller.UserSid, innerCt).ConfigureAwait(false);
            if (!result.Succeeded || result.Domain is null)
                return (new(DomainServiceStatus.Conflict, ErrorCode: "DOMAIN_CONFLICT", ErrorMessage: "A governance domain with that name already exists."), null);
            var created = result.Domain;
            return (new(DomainServiceStatus.Created, created), async c =>
            {
                await RecordDomainAuditAsync(tenant, caller.UserSid, created.Id, "created", null, created, c).ConfigureAwait(false);
                if (created.OwnerGroupSid is { } assignedSid)
                    await RecordOwnerGroupAuditAsync(tenant, caller.UserSid, created, null, assignedSid, c).ConfigureAwait(false);
            });
        }, ct).ConfigureAwait(false);

        if (outcome.Status == DomainServiceStatus.Created)
            await _writeInvalidator.InvalidateWideAsync(tenant, ct: ct).ConfigureAwait(false);
        return outcome;
    }

    public async Task<DomainServiceResult<GovernanceDomain>> UpdateDomainAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        Guid domainId,
        int expectedVersion,
        UpdateGovernanceDomainInput cmd,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return Forbidden<GovernanceDomain>("Only Data Governance administrators can update governance domains.");
        }

        if (domainId == Guid.Empty)
        {
            return Invalid<GovernanceDomain>("SYSTEM_DOMAIN_IMMUTABLE", "The system Unassigned domain cannot be modified.");
        }

        ArgumentNullException.ThrowIfNull(cmd);
        if (!TryValidateName(cmd.Name, out var name, out var err)
            || !TryValidateBoundedOptional(cmd.Description, 1024, "description", out var description, out err)
            || !TryValidateContact(cmd.Contact, out var contact, out err)
            || !TryValidateOptionalClass(cmd.DefaultClass, out var defaultClass, out err)
            || !TryValidateOptionalVisibility(cmd.Visibility, out var parsedVisibility, out err))
        {
            return Invalid<GovernanceDomain>("INVALID_DOMAIN", err!);
        }

        var before = await _domains.GetAsync(tenant, domainId, ct).ConfigureAwait(false);
        if (before is null)
        {
            return NotFound<GovernanceDomain>("DOMAIN_NOT_FOUND", "Governance domain was not found.");
        }

        if (before.IsSystemUnassigned)
        {
            return Invalid<GovernanceDomain>("SYSTEM_DOMAIN_IMMUTABLE", "The system Unassigned domain cannot be modified.");
        }

        if (before.Version != expectedVersion)
        {
            return new DomainServiceResult<GovernanceDomain>(
                DomainServiceStatus.VersionMismatch,
                ErrorCode: "VERSION_MISMATCH",
                ErrorMessage: "Governance domain version does not match If-Match.");
        }

        var updatedModel = before with
        {
            Name = name!,
            Description = description,
            Contact = contact,
            DefaultClass = defaultClass,
            Visibility = parsedVisibility ?? before.Visibility
        };

        if (await ValidateDelegatedDomainUpdateAsync(tenant, updatedModel, ct).ConfigureAwait(false) is { } delegatedError)
        {
            return delegatedError;
        }

        var outcome = await RunAtomicWriteAsync<GovernanceDomain>(async innerCt =>
        {
            var result = await _domains.UpsertAsync(updatedModel, expectedVersion, caller.UserSid, innerCt).ConfigureAwait(false);
            if (result.Status == GovernanceDomainWriteStatus.NotFound)
                return (NotFound<GovernanceDomain>("DOMAIN_NOT_FOUND", "Governance domain was not found."), null);
            if (!result.Succeeded || result.Domain is null)
            {
                var current = await _domains.GetAsync(tenant, domainId, innerCt).ConfigureAwait(false);
                return current is not null && current.Version != expectedVersion
                    ? (new(DomainServiceStatus.VersionMismatch, ErrorCode: "VERSION_MISMATCH", ErrorMessage: "Governance domain version does not match If-Match."), null)
                    : (new(DomainServiceStatus.Conflict, ErrorCode: "DOMAIN_CONFLICT", ErrorMessage: "A governance domain with that name already exists."), null);
            }

            var updated = result.Domain;
            return (Ok(updated), c => RecordDomainAuditAsync(tenant, caller.UserSid, updated.Id, "updated", before, updated, c));
        }, ct).ConfigureAwait(false);

        if (outcome.Status == DomainServiceStatus.Ok)
            await _writeInvalidator.InvalidateWideAsync(tenant, ct: ct).ConfigureAwait(false);
        return outcome;
    }

    public async Task<DomainServiceResult<bool>> DeleteDomainAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        Guid domainId,
        int expectedVersion,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return Forbidden<bool>("Only Data Governance administrators can delete governance domains.");
        }

        if (domainId == Guid.Empty)
        {
            return Invalid<bool>("SYSTEM_DOMAIN_IMMUTABLE", "The system Unassigned domain cannot be deleted.");
        }

        var before = await _domains.GetAsync(tenant, domainId, ct).ConfigureAwait(false);
        if (before is null)
        {
            return NotFound<bool>("DOMAIN_NOT_FOUND", "Governance domain was not found.");
        }

        if (before.IsSystemUnassigned)
        {
            return Invalid<bool>("SYSTEM_DOMAIN_IMMUTABLE", "The system Unassigned domain cannot be deleted.");
        }

        if (before.Version != expectedVersion)
        {
            return new DomainServiceResult<bool>(
                DomainServiceStatus.VersionMismatch,
                ErrorCode: "VERSION_MISMATCH",
                ErrorMessage: "Governance domain version does not match If-Match.");
        }

        var outcome = await RunAtomicWriteAsync<bool>(async innerCt =>
        {
            if (!await _domains.DeleteAsync(tenant, domainId, expectedVersion, innerCt).ConfigureAwait(false))
                return (new(DomainServiceStatus.VersionMismatch, ErrorCode: "VERSION_MISMATCH", ErrorMessage: "Governance domain version does not match If-Match."), null);
            return (new(DomainServiceStatus.Deleted, true), c => RecordDomainAuditAsync(tenant, caller.UserSid, domainId, "deleted", before, null, c));
        }, ct).ConfigureAwait(false);

        if (outcome.Status == DomainServiceStatus.Deleted)
            await _writeInvalidator.InvalidateWideAsync(tenant, ct: ct).ConfigureAwait(false);
        return outcome;
    }

    public async Task<DomainServiceResult<GovernanceDomain>> AssignOwnerGroupAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        Guid domainId,
        string? rawOwnerGroupSid,
        int? expectedVersion = null,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return Forbidden<GovernanceDomain>("Only Data Governance administrators can assign domain owner groups.");
        }

        if (domainId == Guid.Empty)
        {
            return Invalid<GovernanceDomain>("SYSTEM_DOMAIN_IMMUTABLE", "The system Unassigned domain cannot have an owner group.");
        }

        if (!TryValidateOptionalSid(rawOwnerGroupSid, out var ownerGroupSid, out var err))
        {
            return Invalid<GovernanceDomain>("INVALID_OWNER_GROUP_SID", err!);
        }

        var before = await _domains.GetAsync(tenant, domainId, ct).ConfigureAwait(false);
        if (before is null)
        {
            return NotFound<GovernanceDomain>("DOMAIN_NOT_FOUND", "Governance domain was not found.");
        }

        var targetVersion = expectedVersion ?? before.Version;
        if (before.Version != targetVersion)
        {
            return new DomainServiceResult<GovernanceDomain>(
                DomainServiceStatus.VersionMismatch,
                ErrorCode: "VERSION_MISMATCH",
                ErrorMessage: "Governance domain version does not match If-Match.");
        }

        var outcome = await RunAtomicWriteAsync<GovernanceDomain>(async innerCt =>
        {
            var result = await _domains.UpsertAsync(before with { OwnerGroupSid = ownerGroupSid }, targetVersion, caller.UserSid, innerCt).ConfigureAwait(false);
            if (result.Status == GovernanceDomainWriteStatus.NotFound)
                return (NotFound<GovernanceDomain>("DOMAIN_NOT_FOUND", "Governance domain was not found."), null);
            if (!result.Succeeded || result.Domain is null)
                return (new(DomainServiceStatus.VersionMismatch, ErrorCode: "VERSION_MISMATCH", ErrorMessage: "Governance domain version conflict while assigning owner group."), null);
            var assigned = result.Domain;
            return (Ok(assigned), c => RecordOwnerGroupAuditAsync(tenant, caller.UserSid, assigned, before.OwnerGroupSid, ownerGroupSid, c));
        }, ct).ConfigureAwait(false);

        if (outcome.Status == DomainServiceStatus.Ok)
            await _writeInvalidator.InvalidateWideAsync(tenant, ct: ct).ConfigureAwait(false);
        return outcome;
    }

    public const int MaxListedDomainTables = 1000;

    public async Task<DomainServiceResult<(IReadOnlyList<string> Tables, bool Truncated)>> ListDomainTablesAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        Guid domainId,
        int? limit = null,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return Forbidden<(IReadOnlyList<string> Tables, bool Truncated)>("Only Data Governance administrators can read governance domain tables.");
        }

        if (limit is <= 0)
        {
            return Invalid<(IReadOnlyList<string> Tables, bool Truncated)>("INVALID_PAGING", "Limit must be positive.");
        }

        if (domainId == Guid.Empty)
        {
            await _domains.EnsureUnassignedAsync(tenant, ct).ConfigureAwait(false);
        }

        var domain = await _domains.GetAsync(tenant, domainId, ct).ConfigureAwait(false);
        if (domain is null)
        {
            return NotFound<(IReadOnlyList<string> Tables, bool Truncated)>("DOMAIN_NOT_FOUND", "Governance domain was not found.");
        }

        var effectiveLimit = Math.Min(limit ?? MaxListedDomainTables, MaxListedDomainTables);
        var tables = await _domains.ListDomainTablesAsync(tenant, domainId, effectiveLimit + 1, ct).ConfigureAwait(false) ?? [];
        var truncated = tables.Count > effectiveLimit;
        var names = (truncated ? tables.Take(effectiveLimit) : tables)
            .Select(static t => t.ToString())
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Ok<(IReadOnlyList<string> Tables, bool Truncated)>((names, truncated));
    }

    public Task<DomainServiceResult<int>> AssignTablesAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        Guid domainId,
        IReadOnlyList<Guid>? tableIds,
        CancellationToken ct = default) =>
        AssignTablesAsync(caller, tenant, domainId, tableIds, datasets: null, ct);

    public async Task<DomainServiceResult<int>> AssignTablesAsync(
        SecurityPrincipalContext caller,
        TenantId tenant,
        Guid domainId,
        IReadOnlyList<Guid>? tableIds,
        IReadOnlyList<string>? datasets,
        CancellationToken ct = default)
    {
        if (!_capabilities.IsGovernance(caller.Principal, tenant))
        {
            return Forbidden<int>("Only Data Governance administrators can assign tables to domains.");
        }

        if (tableIds is null && datasets is null)
        {
            return Invalid<int>("INVALID_TABLE", "TableIds or datasets list is required.");
        }

        var rawIds = tableIds ?? [];
        var rawDatasets = datasets ?? [];

        var maxTables = await ResolveMaxTablesPerAssignmentAsync(tenant, ct).ConfigureAwait(false);
        if (rawIds.Count + rawDatasets.Count > maxTables)
        {
            return Invalid<int>("TABLE_ASSIGNMENT_LIMIT_EXCEEDED", $"At most {maxTables} tables may be assigned per request.");
        }

        if (rawIds.Any(id => id == Guid.Empty))
        {
            return Invalid<int>("INVALID_TABLE", "Table identifiers must not be empty.");
        }

        var parsedDatasets = new List<TableIdentifier>(rawDatasets.Count);
        foreach (var rawDataset in rawDatasets)
        {
            if (string.IsNullOrWhiteSpace(rawDataset) || !TableIdentifier.TryParse(rawDataset.Trim(), out var parsedId))
            {
                return Invalid<int>("INVALID_TABLE", "Dataset identifiers must use 'domain.schema.table' format.");
            }

            parsedDatasets.Add(parsedId);
        }

        if (domainId == Guid.Empty)
        {
            await _domains.EnsureUnassignedAsync(tenant, ct).ConfigureAwait(false);
        }

        var domain = await _domains.GetAsync(tenant, domainId, ct).ConfigureAwait(false);
        if (domain is null)
        {
            return NotFound<int>("DOMAIN_NOT_FOUND", "Governance domain was not found.");
        }

        var distinctIds = rawIds.Distinct().ToList();
        var distinctDatasets = parsedDatasets.Distinct().ToList();
        var resolvedIds = new List<Guid>(distinctIds.Count + distinctDatasets.Count);
        if (distinctIds.Count > 0 || distinctDatasets.Count > 0)
        {
            var wantedIds = distinctIds.ToHashSet();
            var wantedDatasets = distinctDatasets.ToHashSet();
            var matched = await CatalogTablePageReader.ReadAllAsync(
                _tables,
                new CatalogTableQuery(IncludeInactive: true),
                t => wantedIds.Contains(t.Table.Id) || wantedDatasets.Contains(t.Identifier),
                ct: ct).ConfigureAwait(false);
            var visibleMatched = matched.Where(meta => IsVisibleInTenant(meta, tenant)).ToList();
            var visibleIds = visibleMatched.Select(static meta => meta.Table.Id).ToHashSet();
            var visibleByDataset = new Dictionary<TableIdentifier, Guid>();
            foreach (var meta in visibleMatched)
            {
                visibleByDataset.TryAdd(meta.Identifier, meta.Table.Id);
            }

            if (distinctIds.Any(id => !visibleIds.Contains(id))
                || distinctDatasets.Any(ds => !visibleByDataset.ContainsKey(ds)))
            {
                return NotFound<int>("RESOURCE_NOT_FOUND", "One or more requested tables were not found.");
            }

            if (await ValidateDelegatedTableAssignmentsAsync(tenant, domain, visibleMatched, ct).ConfigureAwait(false) is { } delegatedError)
            {
                return delegatedError;
            }

            resolvedIds.AddRange(distinctIds);
            foreach (var ds in distinctDatasets)
            {
                resolvedIds.Add(visibleByDataset[ds]);
            }
        }

        var distinct = resolvedIds.Distinct().ToList();
        await RunAtomicAsync(async innerCt =>
        {
            await _domains.AssignTablesAsync(tenant, domainId, distinct, caller.UserSid, innerCt).ConfigureAwait(false);
            await RecordTableAssignmentAuditAsync(tenant, caller.UserSid, domain, distinct, innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await _writeInvalidator.InvalidateWideAsync(tenant, ct: ct).ConfigureAwait(false);
        return Ok(distinct.Count);
    }

    private Task RecordDomainAuditAsync(
        TenantId tenant,
        Sid actor,
        Guid domainId,
        string action,
        GovernanceDomain? before,
        GovernanceDomain? after,
        CancellationToken ct) =>
        RecordAuditAsync(tenant, actor, AuditEventTypes.GovernanceDomainChanged, $"domain:{domainId:D}", null, new
        {
            action,
            domainId,
            before = before is null ? null : ProjectDomain(before),
            after = after is null ? null : ProjectDomain(after)
        }, ct);

    private Task RecordOwnerGroupAuditAsync(
        TenantId tenant,
        Sid actor,
        GovernanceDomain domain,
        Sid? beforeOwnerGroupSid,
        Sid? afterOwnerGroupSid,
        CancellationToken ct) =>
        RecordAuditAsync(tenant, actor, AuditEventTypes.OwnerGroupAssigned, $"domain:{domain.Id:D}", null, new
        {
            domainId = domain.Id,
            domainName = domain.Name,
            beforeOwnerGroupSid = beforeOwnerGroupSid?.Value,
            afterOwnerGroupSid = afterOwnerGroupSid?.Value
        }, ct);

    private Task RecordTableAssignmentAuditAsync(
        TenantId tenant,
        Sid actor,
        GovernanceDomain domain,
        IReadOnlyList<Guid> tableIds,
        CancellationToken ct) =>
        RecordAuditAsync(tenant, actor, AuditEventTypes.GovernanceDomainChanged, $"domain:{domain.Id:D}", null, new
        {
            action = "tables_assigned",
            domainId = domain.Id,
            domainName = domain.Name,
            tableCount = tableIds.Count,
            tableIds
        }, ct);

    private Task RecordAuditAsync(
        TenantId tenant,
        Sid actor,
        string eventType,
        string targetTable,
        string? targetColumn,
        object details,
        CancellationToken ct) =>
        _auditWriter.RecordRequiredAsync(new AuditLogEntry
        {
            TenantId = tenant,
            OccurredAt = _time.GetUtcNow().ToUniversalTime(),
            EventType = eventType,
            ActorSid = actor,
            TargetTable = targetTable,
            TargetColumn = targetColumn,
            Decision = "ALLOW",
            TraceId = Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(details)
        }, ct);

    private static object ProjectDomain(GovernanceDomain d) => new
    {
        id = d.Id,
        name = d.Name,
        description = d.Description,
        contact = d.Contact,
        ownerGroupSid = d.OwnerGroupSid?.Value,
        defaultClass = d.DefaultClass?.ToCanonicalName(),
        visibility = d.Visibility.ToCanonicalName(),
        version = d.Version
    };

    private async Task<int> ResolveMaxTablesPerAssignmentAsync(TenantId tenant, CancellationToken ct) =>
        _policyProvider is null
            ? IGovernanceDomainRepository.MaxBatchAssignCount
            : Math.Min((await _policyProvider.GetEffectiveAsync(tenant, ct).ConfigureAwait(false)).MaxTablesPerAssignment, IGovernanceDomainRepository.MaxBatchAssignCount);

    private async Task<int> ResolveMaxPageSizeAsync(TenantId tenant, CancellationToken ct) =>
        _policyProvider is null
            ? IGovernanceDomainRepository.MaxPageSize
            : Math.Min((await _policyProvider.GetEffectiveAsync(tenant, ct).ConfigureAwait(false)).MaxPageSize, IGovernanceDomainRepository.MaxPageSize);

    private async Task<CatalogVisibilityMode> ResolveDefaultVisibilityAsync(TenantId tenant, CancellationToken ct) =>
        _policyProvider is null
            ? CatalogVisibilityMode.OnRequest
            : (await _policyProvider.GetEffectiveAsync(tenant, ct).ConfigureAwait(false)).DefaultContainerVisibility;

    private static bool TryValidateName(string? raw, out string? name, out string? error)
    {
        if (!TryValidateBoundedRequired(raw, 128, "name", out name, out error))
        {
            return false;
        }

        if (string.Equals(name, "Unassigned", StringComparison.OrdinalIgnoreCase))
        {
            name = null;
            error = "Domain name 'Unassigned' is reserved.";
            return false;
        }

        return true;
    }

    private static bool TryValidateBoundedRequired(string? raw, int maxLen, string field, out string? normalized, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = null;
            error = $"Domain {field} is required.";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > maxLen || trimmed.Any(char.IsControl))
        {
            normalized = null;
            error = $"Domain {field} exceeds {maxLen} characters or contains control characters.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    private static bool TryValidateBoundedOptional(string? raw, int maxLen, string field, out string? normalized, out string? error)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = null;
            error = null;
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > maxLen || trimmed.Any(char.IsControl))
        {
            normalized = null;
            error = $"Domain {field} exceeds {maxLen} characters or contains control characters.";
            return false;
        }

        normalized = trimmed;
        error = null;
        return true;
    }

    private static bool TryValidateContact(string? raw, out string? normalized, out string? error)
    {
        if (!TryValidateBoundedOptional(raw, 256, "contact", out normalized, out error) || normalized is null)
        {
            return error is null;
        }

        foreach (var prefix in DangerousContactPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = null;
                error = "Contact must not use an unsafe URI scheme.";
                return false;
            }
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && !SafeContactSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            normalized = null;
            error = "Contact URI scheme is not allowed.";
            return false;
        }

        return true;
    }

    private static bool TryValidateOptionalSid(string? raw, out Sid? sid, out string? error)
    {
        if (raw is null)
        {
            sid = null;
            error = null;
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || !SidFormat.IsGroupOrRealmGroupIdentifier(trimmed))
        {
            sid = null;
            error = "Owner group SID must be a canonical SID (S-1-...) or realm group key (realm:...).";
            return false;
        }

        sid = new Sid(trimmed);
        error = null;
        return true;
    }

    private static bool TryValidateOptionalClass(ProtectionClass? value, out ProtectionClass? normalized, out string? error)
    {
        if (value is null)
        {
            normalized = null;
            error = null;
            return true;
        }

        if (!Enum.IsDefined(value.Value))
        {
            normalized = null;
            error = "Unknown protection class.";
            return false;
        }

        normalized = value;
        error = null;
        return true;
    }

    private static bool TryValidateOptionalVisibility(CatalogVisibilityMode? value, out CatalogVisibilityMode? normalized, out string? error)
    {
        if (value is null)
        {
            normalized = null;
            error = null;
            return true;
        }

        if (!Enum.IsDefined(value.Value))
        {
            normalized = null;
            error = "Unknown catalog visibility mode.";
            return false;
        }

        normalized = value;
        error = null;
        return true;
    }

    private static DomainServiceResult<T> Ok<T>(T value) => new(DomainServiceStatus.Ok, value);
    private static DomainServiceResult<T> Forbidden<T>(string message) => new(DomainServiceStatus.Forbidden, ErrorCode: "ACCESS_DENIED", ErrorMessage: message);
    private static DomainServiceResult<T> NotFound<T>(string code, string message) => new(DomainServiceStatus.NotFound, ErrorCode: code, ErrorMessage: message);
    private static DomainServiceResult<T> Invalid<T>(string code, string message) => new(DomainServiceStatus.Invalid, ErrorCode: code, ErrorMessage: message);
}
