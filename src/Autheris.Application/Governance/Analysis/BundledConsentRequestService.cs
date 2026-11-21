namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #79: Splits a bundled multi-table consent request by Data Owner so each Data Owner receives an approval ticket
/// containing only their own tables while sharing the caller's <c>ConsumerReference</c>.
/// </summary>
public sealed class BundledConsentRequestService : IBundledConsentRequestService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IConsentApprovalRepository? _approvalRepository;
    private readonly IDataOwnershipRepository? _dataOwnershipRepository;
    private readonly IAuditLogRepository? _auditLogRepository;

    private readonly IGrantPolicy _grantPolicy;
    private readonly IGrantValidityPolicy _grantValidityPolicy;
    private readonly IConsentMaterializationGuard? _materializationGuard;
    private readonly IOptions<GatewayOptions>? _gatewayOptions;
    private readonly IConsentWorkflowRouter? _workflowRouter;
    private readonly IConsentWorkflowConfigRepository? _workflowConfigRepository;
    private readonly IConsentEventOutbox? _eventOutbox;
    private readonly IIdentitySubjectResolver? _subjectResolver;
    private readonly IRateLimiterService? _rateLimiter;
    private readonly IOptions<CatalogDiscoveryOptions>? _discoveryOptions;
    private readonly TimeProvider _timeProvider;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public BundledConsentRequestService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IAuditLogRepository auditLogRepository,
        IConsentApprovalRepository? approvalRepository = null,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IGrantPolicy? grantPolicy = null,
        IConsentMaterializationGuard? materializationGuard = null,
        IGrantValidityPolicy? grantValidityPolicy = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IConsentWorkflowRouter? workflowRouter = null,
        IConsentWorkflowConfigRepository? workflowConfigRepository = null,
        IConsentEventOutbox? eventOutbox = null,
        IIdentitySubjectResolver? subjectResolver = null,
        IRateLimiterService? rateLimiter = null,
        IOptions<CatalogDiscoveryOptions>? discoveryOptions = null,
        TimeProvider? timeProvider = null)
    {
        _materializationGuard = materializationGuard;
        _grantValidityPolicy = grantValidityPolicy ?? GrantValidityPolicy.CreateDefault();
        _grantPolicy = grantPolicy ?? new GrantPolicy(validityPolicy: _grantValidityPolicy);
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _approvalRepository = approvalRepository;
        _dataOwnershipRepository = dataOwnershipRepository;
        _gatewayOptions = gatewayOptions;
        _workflowRouter = workflowRouter;
        _workflowConfigRepository = workflowConfigRepository ?? (approvalRepository as IConsentWorkflowConfigRepository);
        _eventOutbox = eventOutbox ?? (approvalRepository as IConsentEventOutbox);
        _subjectResolver = subjectResolver;
        _rateLimiter = rateLimiter;
        _discoveryOptions = discoveryOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BundledConsentRequestService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IConsentApprovalRepository? approvalRepository = null,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IGrantPolicy? grantPolicy = null,
        IConsentMaterializationGuard? materializationGuard = null,
        IGrantValidityPolicy? grantValidityPolicy = null)
    {
        _materializationGuard = materializationGuard;
        _grantValidityPolicy = grantValidityPolicy ?? GrantValidityPolicy.CreateDefault();
        _grantPolicy = grantPolicy ?? new GrantPolicy(validityPolicy: _grantValidityPolicy);
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _approvalRepository = approvalRepository;
        _dataOwnershipRepository = dataOwnershipRepository;
        _workflowConfigRepository = approvalRepository as IConsentWorkflowConfigRepository;
        _eventOutbox = approvalRepository as IConsentEventOutbox;
        _timeProvider = TimeProvider.System;
    }

    private async Task<Dictionary<string, TableMetadata>> ResolveRequestedTablesAsync(
        IReadOnlyList<RequestableConsentItem> items,
        CancellationToken ct)
    {
        var tableLookup = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        var distinctDatasets = items
            .Select(i => i.Dataset)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var parsedIdentifiers = new List<TableIdentifier>();
        var unqualified = new List<string>();
        foreach (var dataset in distinctDatasets)
        {
            if (TableIdentifier.TryParse(dataset, out var parsedId))
            {
                parsedIdentifiers.Add(parsedId);
            }
            else
            {
                unqualified.Add(dataset);
            }
        }

        if (parsedIdentifiers.Count > 0)
        {
            var batch = await _metadataRepository.GetTablesForRequestAsync(
                parsedIdentifiers.Distinct().ToArray(),
                ct).ConfigureAwait(false);
            foreach (var table in batch)
            {
                var fullId = QueryAnalysisService.FormatDatasetId(table.Identifier);
                tableLookup[fullId] = table;
            }
        }

        foreach (var dataset in unqualified)
        {
            if (dataset.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
            {
                continue;
            }

            var dotIndex = dataset.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex >= 0)
            {
                if (dataset.IndexOf('.', dotIndex + 1, StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                var schema = dataset[..dotIndex];
                var tableName = dataset[(dotIndex + 1)..];
                if (string.IsNullOrWhiteSpace(schema) ||
                    string.IsNullOrWhiteSpace(tableName) ||
                    schema.Length > Autheris.Application.Catalog.CatalogTableQuery.MaxFilterLength ||
                    tableName.Length > Autheris.Application.Catalog.CatalogTableQuery.MaxFilterLength)
                {
                    continue;
                }

                var match = await Autheris.Application.Catalog.CatalogTablePageReader.FindFirstAsync(
                    _metadataRepository,
                    new Autheris.Application.Catalog.CatalogTableQuery(
                        Schema: schema,
                        NamePrefix: tableName,
                        Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
                    t => string.Equals($"{t.Identifier.Schema}.{t.Identifier.TableName}", dataset, StringComparison.OrdinalIgnoreCase),
                    ct: ct).ConfigureAwait(false);
                if (match != null)
                {
                    tableLookup[dataset] = match;
                }
            }
            else if (dataset.Length <= Autheris.Application.Catalog.CatalogTableQuery.MaxFilterLength)
            {
                var match = await Autheris.Application.Catalog.CatalogTablePageReader.FindFirstAsync(
                    _metadataRepository,
                    new Autheris.Application.Catalog.CatalogTableQuery(
                        NamePrefix: dataset,
                        Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
                    t => string.Equals(t.Identifier.TableName, dataset, StringComparison.OrdinalIgnoreCase),
                    ct: ct).ConfigureAwait(false);
                if (match != null)
                {
                    tableLookup[dataset] = match;
                }
            }
        }

        return tableLookup;
    }

    public async Task<BundledConsentRequestResult> SubmitBundledRequestAsync(
        ClaimsPrincipal requester,
        BundledConsentRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(request);

        var requesterSid = QueryAnalysisService.ResolvePrincipalSid(requester);
        if (requesterSid == null)
        {
            throw new UnauthorizedAccessException("Authenticated principal is required to submit a bundled consent request.");
        }

        ConsentSubmissionPipeline.EnsurePersonSubject(requester, _subjectResolver);
        ConsentSubmissionPipeline.EnsureSelfServiceEnabled(_gatewayOptions?.Value);

        if (request.Items == null || request.Items.Count == 0)
        {
            return new BundledConsentRequestResult(Array.Empty<OwnerConsentTicket>(), request.ConsumerReference);
        }

        var isAdmin = QueryAnalysisService.IsAdminPrincipal(requester);
        var roles = requester.GetUserRoles();
        var tenantId = requester.GetTenantId();
        await ConsentSubmissionPipeline.CheckAccessRequestRateAsync(
            tenantId,
            requesterSid.Value,
            _rateLimiter,
            _gatewayOptions?.Value,
            _discoveryOptions?.Value,
            ct).ConfigureAwait(false);

        var tableLookup = await ResolveRequestedTablesAsync(request.Items, ct).ConfigureAwait(false);
        var subjects = requester.GetGroupSids().Append(requesterSid.Value).Distinct().ToList();
        var activeConsents = await _consentRepository.GetAllActiveConsentsForSubjectsAsync(
            subjects,
            roles,
            _timeProvider.GetUtcNow(),
            tenantId,
            ct).ConfigureAwait(false);

        var itemsByOwner = new Dictionary<string, OwnerBucket>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in request.Items)
        {
            if (!tableLookup.TryGetValue(item.Dataset, out var table))
            {
                throw new KeyNotFoundException($"Dataset '{item.Dataset}' was not found or is not visible.");
            }

            var accessStatus = CatalogVisibility.ComputeAccessStatus(table.Identifier, activeConsents, pendingRequests: null, isAdmin: isAdmin);
            var hasGrant = string.Equals(accessStatus, "GRANTED", StringComparison.OrdinalIgnoreCase);
            var isOwner = false;
            if (_dataOwnershipRepository != null)
            {
                try
                {
                    isOwner = await _dataOwnershipRepository.IsAuthorizedApproverForTableAsync(table.Identifier, requesterSid.Value, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Fail-safe
                }
            }

            if (table.Table.DiscoveryHidden && !isAdmin && !isOwner && !hasGrant)
            {
                throw new KeyNotFoundException($"Dataset '{item.Dataset}' was not found or is not visible.");
            }

            var (ownerKey, ownerDisplay, primaryOwnerSid) = await ResolveTableOwnerAsync(table, ct).ConfigureAwait(false);
            if (!itemsByOwner.TryGetValue(ownerKey, out var bucket))
            {
                bucket = new OwnerBucket(ownerKey, ownerDisplay, primaryOwnerSid);
                itemsByOwner[ownerKey] = bucket;
            }

            var canonicalDataset = QueryAnalysisService.FormatDatasetId(table.Identifier);
            var normalizedItem = item with { Dataset = canonicalDataset };
            bucket.Items.Add((table, normalizedItem));
            if (!bucket.Datasets.Contains(canonicalDataset, StringComparer.OrdinalIgnoreCase))
            {
                bucket.Datasets.Add(canonicalDataset);
            }
        }

        // GOV-12: a table that cannot carry consents (caller attribute source, A-10) is refused for the whole bundle before any request
        // is stored, so no partial bundle remains.
        if (_materializationGuard != null)
        {
            foreach (var bucket in itemsByOwner.Values)
            {
                foreach (var (table, _) in bucket.Items)
                {
                    await _materializationGuard.EnsureMayMaterializeAsync(tenantId, table.Identifier, Array.Empty<ConsentRowFilter>(), ct).ConfigureAwait(false);
                }
            }
        }

        var orderedBuckets = itemsByOwner.Values
            .OrderBy(b => b.OwnerKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requesterIdentifiers = ConsentSubmissionPipeline.ExtractRequesterIdentifiers(requester, requesterSid.Value.Value);
        var plannedBuckets = new List<(OwnerBucket Bucket, List<ConsentRequest> Requests)>(orderedBuckets.Count);

        foreach (var bucket in orderedBuckets)
        {
            var plannedRequests = new List<ConsentRequest>(bucket.Items.Count);
            foreach (var (table, item) in bucket.Items)
            {
                var effectivePurpose = !string.IsNullOrWhiteSpace(request.Purpose)
                    ? request.Purpose
                    : item.SuggestedPurpose;
                var validatedPurpose = ConsentSubmissionPipeline.ValidateJustification(effectivePurpose);
                var businessJustification = string.IsNullOrWhiteSpace(request.ConsumerReference)
                    ? validatedPurpose
                    : ConsentSubmissionPipeline.ValidateJustification($"[Ref:{request.ConsumerReference}] {validatedPurpose}");

                var bundledNow = _timeProvider.GetUtcNow();
                var resolvedValidTo = ConsentSubmissionPipeline.ResolveRequestedValidTo(
                    _grantValidityPolicy,
                    bundledNow,
                    requestedValidFrom: null,
                    requestedValidTo: request.RequestedUntilUtc);
                var (workflowMode, externalSystemId, effectiveValidTo) = await ConsentSubmissionPipeline.ResolveWorkflowRouteAsync(
                    tenantId,
                    [table.Identifier],
                    bundledNow,
                    resolvedValidTo,
                    hasExplicitDuration: request.RequestedUntilUtc.HasValue,
                    _workflowRouter,
                    _workflowConfigRepository,
                    ct).ConfigureAwait(false);

                GrantPolicyViolationException.ThrowIfDenied(_grantPolicy.Evaluate(new GrantRequest(
                    GranteeType.User,
                    requesterSid.Value.Value,
                    GrantKind.Consent,
                    effectiveValidTo,
                    bundledNow,
                    GrantClassifier.Classify([table]),
                    Requester: requesterSid.Value.Value,
                    Justification: validatedPurpose)));

                var initialStatus = workflowMode == ConsentWorkflowMode.External
                    ? "PENDING_EXTERNAL_APPROVAL"
                    : "PENDING";

                plannedRequests.Add(new ConsentRequest
                {
                    Id = Guid.NewGuid(),
                    TableId = table.Table.Id,
                    TableIdentifier = table.Identifier,
                    RequesterSid = requesterSid.Value,
                    RequestedGranteeType = GranteeType.User,
                    RequestedGranteeRef = requesterSid.Value.Value,
                    BusinessJustification = businessJustification,
                    Status = initialStatus,
                    WorkflowMode = workflowMode,
                    ExternalSystemId = externalSystemId,
                    RequestedAt = bundledNow,
                    RequestedValidFrom = bundledNow,
                    RequestedValidTo = effectiveValidTo,
                    TenantId = tenantId,
                    RequesterIdentifiers = requesterIdentifiers,
                    ApprovalSteps = workflowMode == ConsentWorkflowMode.Internal && bucket.PrimaryOwnerSid is { } approverSid
                        ? new List<ApprovalStep>
                        {
                            new()
                            {
                                Id = Guid.NewGuid(),
                                StepNumber = 1,
                                ApproverSid = approverSid,
                                Decision = "PENDING"
                            }
                        }
                        : new List<ApprovalStep>()
                });
            }

            plannedBuckets.Add((bucket, plannedRequests));
        }

        var tickets = new List<OwnerConsentTicket>(plannedBuckets.Count);

        foreach (var (bucket, plannedRequests) in plannedBuckets)
        {
            var ticketId = Guid.NewGuid();
            var consentIds = new List<Guid>(plannedRequests.Count);
            var primaryMode = ConsentWorkflowMode.Internal;

            foreach (var requestRecord in plannedRequests)
            {
                if (requestRecord.WorkflowMode == ConsentWorkflowMode.External)
                {
                    primaryMode = ConsentWorkflowMode.External;
                }

                ConsentRequest effectiveRecord = requestRecord;
                if (_approvalRepository != null)
                {
                    effectiveRecord = await _approvalRepository.CreateConsentRequestAsync(requestRecord, ct).ConfigureAwait(false);
                }

                consentIds.Add(effectiveRecord.Id);

                if (_eventOutbox != null)
                {
                    var eventNow = effectiveRecord.RequestedAt == default ? _timeProvider.GetUtcNow() : effectiveRecord.RequestedAt;
                    var payload = ConsentEventPayloads.ForRequest(effectiveRecord, eventNow);
                    await _eventOutbox.AppendAsync(
                        new ConsentEventWrite(
                            effectiveRecord.TenantId.Value,
                            Guid.NewGuid(),
                            ConsentEventTypes.RequestCreated,
                            effectiveRecord.Id.ToString("D"),
                            ConsentEventPayloads.Serialize(payload),
                            eventNow,
                            effectiveRecord.ExternalSystemId),
                        transaction: null,
                        ct: ct).ConfigureAwait(false);
                }
            }

            tickets.Add(new OwnerConsentTicket(
                TicketId: ticketId,
                DataOwnerKey: bucket.OwnerKey,
                DataOwnerDisplay: bucket.OwnerDisplay,
                Datasets: bucket.Datasets.ToList(),
                Items: bucket.Items.Select(x => x.Item).ToList(),
                ConsumerReference: request.ConsumerReference,
                Status: primaryMode == ConsentWorkflowMode.External ? "PENDING_EXTERNAL_APPROVAL" : "PENDING",
                UnderlyingConsentRequestIds: consentIds));
        }

        if (_auditLogRepository != null)
        {
            await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = AuditEventTypes.ConsentRequested,
                ActorSid = requesterSid.Value,
                TargetTable = string.Join(",", tickets.SelectMany(t => t.Datasets).Distinct(StringComparer.OrdinalIgnoreCase)),
                Query = $"submit_bundled_consent(tickets={tickets.Count}, ref={request.ConsumerReference ?? "none"})",
                Decision = "ALLOW",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ticketCount = tickets.Count,
                    consumerReference = request.ConsumerReference,
                    datasets = tickets.SelectMany(t => t.Datasets).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                })
            }, ct).ConfigureAwait(false);
        }

        return new BundledConsentRequestResult(
            Tickets: tickets,
            ConsumerReference: request.ConsumerReference);
    }

    private async Task<(string OwnerKey, string OwnerDisplay, Sid? PrimaryOwnerSid)> ResolveTableOwnerAsync(
        TableMetadata table,
        CancellationToken ct)
    {
        if (_dataOwnershipRepository != null)
        {
            var owners = await _dataOwnershipRepository.GetDataOwnersForTableAsync(table.Identifier, ct).ConfigureAwait(false);
            var activeOwners = owners.Where(o => o.IsActive).ToList();
            if (activeOwners.Count > 0)
            {
                var primary = activeOwners[0];
                var key = !string.IsNullOrWhiteSpace(primary.AdSid.Value)
                    ? $"owner:{primary.AdSid.Value}"
                    : !string.IsNullOrWhiteSpace(primary.AdAccount)
                        ? $"owner:{primary.AdAccount}"
                        : $"owner:{primary.Id:N}";

                var display = !string.IsNullOrWhiteSpace(primary.DisplayName)
                    ? primary.DisplayName
                    : !string.IsNullOrWhiteSpace(primary.Email)
                        ? primary.Email
                        : primary.AdAccount;

                return (key, display, primary.AdSid);
            }
        }

        if (!string.IsNullOrWhiteSpace(table.OwnerTenantId))
        {
            return ($"owner:{table.OwnerTenantId}", table.OwnerTenantId, new Sid(table.OwnerTenantId));
        }

        return ($"domain:{table.Identifier.Domain}", $"Domain Owner ({table.Identifier.Domain})", null);
    }

    private sealed class OwnerBucket(string ownerKey, string ownerDisplay, Sid? primaryOwnerSid)
    {
        public string OwnerKey { get; } = ownerKey;
        public string OwnerDisplay { get; } = ownerDisplay;
        public Sid? PrimaryOwnerSid { get; } = primaryOwnerSid;
        public List<string> Datasets { get; } = new();
        public List<(TableMetadata Table, RequestableConsentItem Item)> Items { get; } = new();
    }
}
