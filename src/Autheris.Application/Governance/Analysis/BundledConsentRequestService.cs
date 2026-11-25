namespace Autheris.Application.Governance.Analysis;

using Autheris.Application.Governance.Ownership;
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
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Requests;
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
/// Issue #79: Derives the items of a bundled multi-table consent request (visibility, canonical dataset, owner grouping) and
/// submits every item as its own request through <see cref="IPermissionRequestEngine.SubmitBundleAsync"/>; each item is routed and
/// decided individually. The per-Data-Owner tickets only group the created requests for the caller.
/// </summary>
public sealed class BundledConsentRequestService : IBundledConsentRequestService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IContainerOwnership? _dataOwnershipRepository;
    private readonly IGrantValidityPolicy _grantValidityPolicy;
    private readonly IConsentMaterializationGuard? _materializationGuard;
    private readonly IOptions<GatewayOptions>? _gatewayOptions;
    private readonly IIdentitySubjectResolver? _subjectResolver;
    private readonly TimeProvider _timeProvider;
    private readonly IPermissionRequestEngine? _engine;
    private readonly IAccessPolicySettingsProvider? _policyProvider;

    public BundledConsentRequestService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IContainerOwnership? dataOwnershipRepository = null,
        IConsentMaterializationGuard? materializationGuard = null,
        IGrantValidityPolicy? grantValidityPolicy = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IIdentitySubjectResolver? subjectResolver = null,
        TimeProvider? timeProvider = null,
        IPermissionRequestEngine? engine = null,
        IAccessPolicySettingsProvider? policyProvider = null)
    {
        _engine = engine;
        _policyProvider = policyProvider;
        _materializationGuard = materializationGuard;
        _grantValidityPolicy = grantValidityPolicy ?? GrantValidityPolicy.CreateDefault();
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? throw new ArgumentNullException(nameof(consentRepository));
        _dataOwnershipRepository = dataOwnershipRepository;
        _gatewayOptions = gatewayOptions;
        _subjectResolver = subjectResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        ConsentSubmissionGuards.EnsurePersonSubject(requester, _subjectResolver);
        ConsentSubmissionGuards.EnsureSelfServiceEnabled(_gatewayOptions?.Value);

        if (request.Items == null || request.Items.Count == 0)
        {
            return new BundledConsentRequestResult(Array.Empty<OwnerConsentTicket>(), request.ConsumerReference);
        }

        var engine = _engine ?? throw new InvalidOperationException("The permission request engine is not configured; bundled requests are refused (fail closed).");
        var isAdmin = QueryAnalysisService.IsAdminPrincipal(requester);
        var roles = requester.GetUserRoles();
        var tenantId = requester.GetTenantId();

        // The bundle cap is checked before any table lookup so an oversized bundle costs nothing.
        var maxBundle = _policyProvider is null
            ? _gatewayOptions?.Value.Governance.AccessPolicy.MaxBundleItems ?? new AccessPolicyOptions().MaxBundleItems
            : (await _policyProvider.GetEffectiveAsync(tenantId, ct).ConfigureAwait(false)).MaxBundleItems;
        if (request.Items.Count > maxBundle)
        {
            throw new ConsentRequestException("BUNDLE_LIMIT_EXCEEDED", $"Bundle exceeds maximum of {maxBundle} items.");
        }

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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fail-safe: an unreadable ownership lookup grants nothing.
                    isOwner = false;
                }
            }

            if (table.Table.DiscoveryHidden && !isAdmin && !isOwner && !hasGrant)
            {
                throw new KeyNotFoundException($"Dataset '{item.Dataset}' was not found or is not visible.");
            }

            var (ownerKey, ownerDisplay) = await ResolveTableOwnerAsync(table, ct).ConfigureAwait(false);
            if (!itemsByOwner.TryGetValue(ownerKey, out var bucket))
            {
                bucket = new OwnerBucket(ownerKey, ownerDisplay);
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

        // A table that cannot carry consents (caller attribute source) is refused for the whole bundle before any request is stored.
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

        var durationDays = request.RequestedUntilUtc.HasValue
            ? (int?)null
            : Math.Max(1, (int)Math.Ceiling(_grantValidityPolicy.DefaultDuration.TotalDays));
        var submissions = new List<PermissionRequestSubmission>(request.Items.Count);
        foreach (var bucket in orderedBuckets)
        {
            foreach (var (table, item) in bucket.Items)
            {
                var effectivePurpose = !string.IsNullOrWhiteSpace(request.Purpose) ? request.Purpose : item.SuggestedPurpose;
                var justification = string.IsNullOrWhiteSpace(request.ConsumerReference)
                    ? effectivePurpose
                    : $"[Ref:{request.ConsumerReference}] {effectivePurpose}";

                // A masked-column exception is an Unmask request over that column; a table gap is a Data request.
                var isColumn = !string.IsNullOrWhiteSpace(item.Column);
                submissions.Add(new PermissionRequestSubmission(
                    requester,
                    tenantId,
                    isColumn ? ConsentRequestKind.Unmask : ConsentRequestKind.Data,
                    PermissionTarget.ForTable(table.Identifier, isColumn ? [item.Column!.Trim()] : null),
                    GranteeType.User,
                    requesterSid.Value.Value,
                    justification,
                    ValidTo: request.RequestedUntilUtc,
                    DurationDays: durationDays,
                    IncludeReadAccess: !isColumn));
            }
        }

        var submitted = await engine.SubmitBundleAsync(requester, tenantId, submissions, callerContext: null, ct).ConfigureAwait(false);
        var created = submitted.BundleRequests ?? [submitted.Request];
        if (created.Count != submissions.Count)
        {
            // Every item must come back as exactly one request; a short or long result cannot be mapped to owners safely.
            throw new InvalidOperationException("The permission request engine did not create one request per bundle item; the bundle is refused (fail closed).");
        }

        var tickets = new List<OwnerConsentTicket>(orderedBuckets.Count);
        var offset = 0;
        foreach (var bucket in orderedBuckets)
        {
            var bucketRequests = created.Skip(offset).Take(bucket.Items.Count).ToList();
            offset += bucket.Items.Count;
            var external = bucketRequests.Any(r => r.WorkflowMode == ConsentWorkflowMode.External);
            tickets.Add(new OwnerConsentTicket(
                TicketId: Guid.NewGuid(),
                DataOwnerKey: bucket.OwnerKey,
                DataOwnerDisplay: bucket.OwnerDisplay,
                Datasets: bucket.Datasets.ToList(),
                Items: bucket.Items.Select(x => x.Item).ToList(),
                ConsumerReference: request.ConsumerReference,
                Status: external ? "PENDING_EXTERNAL_APPROVAL" : "PENDING",
                UnderlyingConsentRequestIds: bucketRequests.Select(r => r.Id).ToList()));
        }

        return new BundledConsentRequestResult(
            Tickets: tickets,
            ConsumerReference: request.ConsumerReference);
    }

    private async Task<(string OwnerKey, string OwnerDisplay)> ResolveTableOwnerAsync(
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

                return (key, display);
            }
        }

        if (!string.IsNullOrWhiteSpace(table.OwnerTenantId))
        {
            return ($"owner:{table.OwnerTenantId}", table.OwnerTenantId);
        }

        return ($"domain:{table.Identifier.Domain}", $"Domain Owner ({table.Identifier.Domain})");
    }

    private sealed class OwnerBucket(string ownerKey, string ownerDisplay)
    {
        public string OwnerKey { get; } = ownerKey;
        public string OwnerDisplay { get; } = ownerDisplay;
        public List<string> Datasets { get; } = new();
        public List<(TableMetadata Table, RequestableConsentItem Item)> Items { get; } = new();
    }
}
