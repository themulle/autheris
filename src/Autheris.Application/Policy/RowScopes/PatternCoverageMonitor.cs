using Autheris.Application.Governance.Ownership;
using System.Text.Json;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.State;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Autheris.Application.Policy.RowScopes;

/// <summary>
/// Watches catalog epoch changes and checks active Internal-workflow pattern grants against current table ownership.
/// When a newly covered object lacks an approving owner in <c>CONSENT_APPROVING_OWNERS</c>, emits an audit record and
/// files a pending approval request so owner approval is obtained without auto-granting uncovered objects.
/// </summary>
public sealed class PatternCoverageMonitor : BackgroundService
{
    private const string ScanLockKey = "pattern_coverage:scan";
    private const int MaxDedupeEntries = 10_000;
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICatalogVersionProvider _catalogVersionProvider;
    private readonly IDistributedClusterStateProvider _clusterState;
    private readonly ILogger<PatternCoverageMonitor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _scanInterval;
    private readonly PatternEpochMatcherCache _matcherCache = new();
    private readonly HashSet<(Guid ConsentId, TableIdentifier Table)> _processedUncoveredKeys = [];
    private CatalogVersion? _lastProcessedVersion;

    public PatternCoverageMonitor(
        IServiceScopeFactory scopeFactory,
        ICatalogVersionProvider catalogVersionProvider,
        IDistributedClusterStateProvider clusterState,
        ILogger<PatternCoverageMonitor> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? scanInterval = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _catalogVersionProvider = catalogVersionProvider ?? throw new ArgumentNullException(nameof(catalogVersionProvider));
        _clusterState = clusterState ?? throw new ArgumentNullException(nameof(clusterState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        var interval = scanInterval ?? DefaultInterval;
        _scanInterval = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunSafeCycleAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_scanInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunSafeCycleAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var currentVersion = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
        if (_lastProcessedVersion is { } previous && previous == currentVersion)
        {
            return 0;
        }

        IAsyncDisposable? lease = null;
        try
        {
            lease = await _clusterState.TryAcquireLockAsync(ScanLockKey, LockTtl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PatternCoverageMonitor failed to acquire distributed scan lock; deferring cycle.");
            return 0;
        }

        if (lease is null)
        {
            return 0;
        }

        await using (lease.ConfigureAwait(false))
        {
            var now = _timeProvider.GetUtcNow();
            var epoch = currentVersion.StructureVersion + currentVersion.ContentVersion;

            using var scope = _scopeFactory.CreateScope();
            var consentRepo = scope.ServiceProvider.GetRequiredService<IConsentRepository>();
            var patternConsents = await consentRepo.GetActiveInternalPatternConsentsAsync(now, ct).ConfigureAwait(false);
            if (patternConsents.Count == 0)
            {
                _lastProcessedVersion = currentVersion;
                return 0;
            }

            var catalogRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
            var ownershipRepo = scope.ServiceProvider.GetRequiredService<IContainerOwnership>();
            var approvalRepo = scope.ServiceProvider.GetService<IConsentApprovalRepository>();
            var auditRepo = scope.ServiceProvider.GetService<IAuditLogRepository>();
            var profileAssignmentService = scope.ServiceProvider.GetService<IProfileAssignmentRequestService>();

            var tables = await CatalogTablePageReader.ReadAllAsync(
                catalogRepo,
                new CatalogTableQuery(IncludeInactive: false),
                static t => t.Table.IsActive && !t.Table.DiscoveryHidden,
                ct: ct).ConfigureAwait(false);
            var visibleTables = tables;

            int detections = 0;
            foreach (var consent in patternConsents)
            {
                if (!consent.IsPatternGrant ||
                    consent.Effect != ConsentEffect.Allow ||
                    consent.WorkflowMode != ConsentWorkflowMode.Internal)
                {
                    continue;
                }

                var matcher = _matcherCache.GetOrParse(epoch, consent.ObjectPattern);
                if (matcher is null)
                {
                    continue;
                }

                var approvingSet = consent.ApprovingOwners
                    .Where(static o => !string.IsNullOrWhiteSpace(o))
                    .Select(static o => o.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var tableMeta in visibleTables)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!matcher.Matches(tableMeta))
                    {
                        continue;
                    }

                    var authorizedOwners = await ownershipRepo
                        .GetAuthorizedApproverSidsForTableAsync(tableMeta.Identifier, now, ct)
                        .ConfigureAwait(false);
                    if (authorizedOwners.Any(approvingSet.Contains))
                    {
                        continue;
                    }

                    if (authorizedOwners.Count == 0)
                    {
                        continue;
                    }

                    if (_processedUncoveredKeys.Count >= MaxDedupeEntries)
                    {
                        _processedUncoveredKeys.Clear();
                    }

                    if (!_processedUncoveredKeys.Add((consent.Id, tableMeta.Identifier)))
                    {
                        continue;
                    }

                    detections++;
                    await HandleUncoveredObjectAsync(
                        consent, tableMeta, authorizedOwners, epoch, now, approvalRepo, auditRepo, profileAssignmentService, ct).ConfigureAwait(false);
                }
            }

            if (detections > 0)
            {
                _logger.LogInformation(
                    "PatternCoverageMonitor detected {Count} newly matching objects requiring owner approval at catalog epoch {Epoch}.",
                    detections, epoch);
            }

            _lastProcessedVersion = currentVersion;
            return detections;
        }
    }

    private async Task HandleUncoveredObjectAsync(
        Consent consent,
        TableMetadata tableMeta,
        IReadOnlyCollection<string> authorizedOwners,
        long epoch,
        DateTimeOffset now,
        IConsentApprovalRepository? approvalRepo,
        IAuditLogRepository? auditRepo,
        IProfileAssignmentRequestService? profileAssignmentService,
        CancellationToken ct)
    {
        int ownerCount = authorizedOwners.Count;
        if (consent.ProfileAssignmentId.HasValue && profileAssignmentService != null)
        {
            try
            {
                await profileAssignmentService
                    .HandlePatternNewOwnerAsync(consent, tableMeta, authorizedOwners, now, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "PatternCoverageMonitor could not extend profile assignment {AssignmentId} for table {Table} under pattern grant {GrantId}.",
                    consent.ProfileAssignmentId.Value,
                    tableMeta.Identifier.ToString(),
                    consent.Id);
            }
        }
        else if (approvalRepo != null && consent.GranteeSid is { } requesterSid)
        {
            var request = new ConsentRequest
            {
                Id = Guid.NewGuid(),
                TableId = tableMeta.Table.Id,
                TableIdentifier = tableMeta.Identifier,
                RequesterSid = requesterSid,
                RequestedGranteeType = consent.GranteeType,
                RequestedGranteeRef = consent.GranteeSid?.Value ?? string.Empty,
                BusinessJustification = $"Internal pattern grant {consent.Id:D} matched a new object requiring owner approval.",
                Status = "PENDING",
                RequestedAt = now,
                RequestedValidFrom = now,
                RequestedValidTo = consent.ValidTo,
                UpdatedAt = now,
                TenantId = consent.TenantId,
                WorkflowMode = ConsentWorkflowMode.Internal,
                RequestKind = consent.ProfileAssignmentId.HasValue
                    ? ConsentRequestKind.ProfileAssignment
                    : ConsentRequestKind.Data,
                ProfileId = consent.ProfileAssignmentId
            };

            try
            {
                await approvalRepo.CreateConsentRequestAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "PatternCoverageMonitor could not file pending request for table {Table} under pattern grant {GrantId}.",
                    tableMeta.Identifier.ToString(),
                    consent.Id);
            }
        }

        if (auditRepo != null)
        {
            var actorSid = consent.GranteeSid ?? new Sid("SYSTEM");
            var audit = new AuditLogEntry
            {
                TenantId = consent.TenantId,
                EventType = AuditEventTypes.PatternCoverageChanged,
                ActorSid = actorSid,
                TargetTable = tableMeta.Identifier.ToString(),
                Decision = "PENDING",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ConsentId = consent.Id,
                    ObjectPattern = consent.ObjectPattern,
                    CatalogEpoch = epoch,
                    AuthorizedOwnerCount = ownerCount
                })
            };

            try
            {
                await auditRepo.RecordAuditEventAsync(audit, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "PatternCoverageMonitor could not record coverage audit event for pattern grant {GrantId}.",
                    consent.Id);
            }
        }
    }

    private async Task RunSafeCycleAsync(CancellationToken ct)
    {
        try
        {
            await RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PatternCoverageMonitor cycle failed.");
        }
    }
}
