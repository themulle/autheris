namespace Autheris.Application.Governance.Analysis;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

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

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    private readonly IGrantPolicy _grantPolicy;

    public BundledConsentRequestService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IAuditLogRepository auditLogRepository,
        IConsentApprovalRepository? approvalRepository = null,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IGrantPolicy? grantPolicy = null)
    {
        _grantPolicy = grantPolicy ?? new GrantPolicy();
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _approvalRepository = approvalRepository;
        _dataOwnershipRepository = dataOwnershipRepository;
    }

    public BundledConsentRequestService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IConsentApprovalRepository? approvalRepository = null,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IGrantPolicy? grantPolicy = null)
    {
        _grantPolicy = grantPolicy ?? new GrantPolicy();
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _approvalRepository = approvalRepository;
        _dataOwnershipRepository = dataOwnershipRepository;
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

        if (request.Items == null || request.Items.Count == 0)
        {
            return new BundledConsentRequestResult(Array.Empty<OwnerConsentTicket>(), request.ConsumerReference);
        }

        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var tableLookup = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in allTables)
        {
            var fullId = QueryAnalysisService.FormatDatasetId(table.Identifier);
            tableLookup[fullId] = table;
            tableLookup.TryAdd($"{table.Identifier.Schema}.{table.Identifier.TableName}", table);
            tableLookup.TryAdd(table.Identifier.TableName, table);
        }

        var isAdmin = QueryAnalysisService.IsAdminPrincipal(requester);
        var roles = requester.GetUserRoles();
        var tenantId = requester.GetTenantId();
        var subjects = requester.GetGroupSids().Append(requesterSid.Value).Distinct().ToList();
        var activeConsents = await _consentRepository.GetAllActiveConsentsForSubjectsAsync(
            subjects,
            roles,
            DateTimeOffset.UtcNow,
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

            if (table.Table.Visibility == DatasetVisibility.Hidden && !isAdmin && !isOwner && !hasGrant)
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

        var tickets = new List<OwnerConsentTicket>();

        foreach (var bucket in itemsByOwner.Values.OrderBy(b => b.OwnerKey, StringComparer.OrdinalIgnoreCase))
        {
            var ticketId = Guid.NewGuid();
            var consentIds = new List<Guid>();

            foreach (var (table, item) in bucket.Items)
            {
                var effectivePurpose = !string.IsNullOrWhiteSpace(request.Purpose)
                    ? request.Purpose
                    : item.SuggestedPurpose;

                // WP-SC-16: bundled requests are individual grants for the requester; the default duration (90 days) and any
                // requested expiry must respect the class maximum of the table.
                var bundledNow = DateTimeOffset.UtcNow;
                GrantPolicyViolationException.ThrowIfDenied(_grantPolicy.Evaluate(new GrantRequest(
                    GranteeType.User,
                    requesterSid.Value.Value,
                    GrantKind.Consent,
                    request.RequestedUntilUtc ?? bundledNow.AddDays(Math.Min(90, (int)IndividualGrantLimits.RegularMax.TotalDays)),
                    bundledNow,
                    GrantClassifier.Classify([table]),
                    Requester: requesterSid.Value.Value,
                    Justification: effectivePurpose)));

                var requestRecord = new ConsentRequest
                {
                    Id = Guid.NewGuid(),
                    TableId = table.Table.Id,
                    TableIdentifier = table.Identifier,
                    RequesterSid = requesterSid.Value,
                    RequestedGranteeType = GranteeType.User,
                    RequestedGranteeRef = requesterSid.Value.Value,
                    BusinessJustification = string.IsNullOrWhiteSpace(request.ConsumerReference)
                        ? effectivePurpose
                        : $"[Ref:{request.ConsumerReference}] {effectivePurpose}",
                    Status = "PENDING",
                    RequestedAt = DateTimeOffset.UtcNow,
                    RequestedValidTo = request.RequestedUntilUtc ?? bundledNow.AddDays(Math.Min(90, (int)IndividualGrantLimits.RegularMax.TotalDays)),
                    ItsmTicketId = request.ConsumerReference ?? ticketId.ToString("N"),
                    TenantId = tenantId,
                    ApprovalSteps = bucket.PrimaryOwnerSid is { } approverSid
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
                };

                if (_approvalRepository != null)
                {
                    var persisted = await _approvalRepository.CreateConsentRequestAsync(requestRecord, ct).ConfigureAwait(false);
                    consentIds.Add(persisted.Id);
                }
                else
                {
                    consentIds.Add(requestRecord.Id);
                }
            }

            tickets.Add(new OwnerConsentTicket(
                TicketId: ticketId,
                DataOwnerKey: bucket.OwnerKey,
                DataOwnerDisplay: bucket.OwnerDisplay,
                Datasets: bucket.Datasets.ToList(),
                Items: bucket.Items.Select(x => x.Item).ToList(),
                ConsumerReference: request.ConsumerReference,
                Status: "PENDING",
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
