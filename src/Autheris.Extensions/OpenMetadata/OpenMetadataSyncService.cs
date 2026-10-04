using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Extensions.OpenMetadata;

public sealed class OpenMetadataSyncService : IOpenMetadataSyncService
{
    private readonly IOpenMetadataClient _client;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IConsentRepository _consentRepo;
    private readonly IPolicyEpochRepository _epochRepo;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<OpenMetadataSyncService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// SEC H-19: Marker stored in <see cref="Consent.ConsentRequestId"/> for consents created by this sync (used for reconcile).
    /// </summary>
    internal static readonly Guid OpenMetadataSyncConsentMarker = new("0e3d5c1a-7b2f-4c8e-9a61-5f0d2b7c4e19");
    private static readonly Sid SyncActorSid = new("system:openmetadata-sync");
    private static readonly HashSet<string> AllowDataOperations = new(StringComparer.OrdinalIgnoreCase) { "ViewAll", "ViewSampleData" };
    private static readonly HashSet<string> DenyDataOperations = new(StringComparer.OrdinalIgnoreCase) { "All", "ViewAll", "ViewSampleData" };

    private static readonly SemaphoreSlim SyncLock = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> ProcessedWebhookEvents = new();

    public OpenMetadataSyncService(
        IOpenMetadataClient client,
        ITableMetadataRepository metadataRepo,
        IConsentRepository consentRepo,
        IPolicyEpochRepository epochRepo,
        IOptions<GatewayOptions> options,
        ILogger<OpenMetadataSyncService> logger)
    {
        _client = client;
        _metadataRepo = metadataRepo;
        _consentRepo = consentRepo;
        _epochRepo = epochRepo;
        _options = options;
        _logger = logger;
    }

    public async Task<OpenMetadataSyncResult> SyncPermissionsAsync(bool dryRun = false, CancellationToken ct = default)
    {
        if (!await SyncLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _logger.LogWarning("OpenMetadata sync is already in progress. Skipping concurrent request.");
            return new OpenMetadataSyncResult(0, 0, 0, Array.Empty<TableIdentifier>(), ["Sync already in progress."], false);
        }

        try
        {
            var omOptions = _options.Value.OpenMetadata;
            var warnings = new List<string>();
            var affectedTables = new HashSet<TableIdentifier>();

            int syncedTablesCount = 0;
            int syncedMaskingRulesCount = 0;

            _logger.LogInformation("Starting OpenMetadata synchronization. DryRun: {DryRun}", dryRun);

            // 1. Fetch tables
            IReadOnlyList<OpenMetadataTable> omTables;
            try
            {
                var serviceFilter = string.IsNullOrWhiteSpace(omOptions.ServiceFilter) ? null : omOptions.ServiceFilter;
                omTables = await _client.GetTablesAsync(serviceFilter, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch tables from OpenMetadata.");
                return new OpenMetadataSyncResult(0, 0, 0, Array.Empty<TableIdentifier>(), [ex.Message], false);
            }

            // SEC E-09 / EX-06: ServiceFilter per table, database-aware identity (explicit map) and collision rejection.
            var candidates = new List<(OpenMetadataTable Item, OpenMetadataTableIdentity.ResolvedTable Resolved)>();
            foreach (var omTable in omTables)
            {
                if (OpenMetadataTableIdentity.TryResolve(omTable, omOptions, out var resolved, out var reason))
                {
                    candidates.Add((omTable, resolved));
                }
                else
                {
                    var warning = $"Skipped OpenMetadata table {omTable.FullyQualifiedName}: {reason}.";
                    _logger.LogWarning("{Warning}", warning);
                    warnings.Add(warning);
                }
            }

            if (omOptions.ServiceDatabaseToDomainMap.Count == 0)
            {
                candidates = OpenMetadataTableIdentity.RejectDatabaseCollisions(candidates, id =>
                {
                    var warning = $"Rejected OpenMetadata tables mapping to {id}: same service.schema.table in different databases (configure OpenMetadata.ServiceDatabaseToDomainMap).";
                    _logger.LogWarning("{Warning}", warning);
                    warnings.Add(warning);
                });
            }

            var tableMetadataMap = new Dictionary<TableIdentifier, TableMetadata>();
            var tableTenantMap = new Dictionary<TableIdentifier, TenantId>();

            foreach (var (omTable, resolved) in candidates)
            {
                try
                {
                    var tableId = resolved.Identifier;
                    tableTenantMap[tableId] = resolved.TenantId;
                    var existing = dryRun ? null : await _metadataRepo.GetTableMetadataAsync(tableId, ct);
                    var (tableMeta, maskingCount) = MapToTableMetadata(omTable, tableId, omOptions, isNewTable: existing == null);

                    tableMetadataMap[tableId] = tableMeta;
                    syncedTablesCount++;
                    syncedMaskingRulesCount += maskingCount;
                    affectedTables.Add(tableId);

                    if (!dryRun)
                    {
                        // SEC M-32: merge with persisted state – sync may only tighten governance flags.
                        var merged = Autheris.Application.DataCatalog.Services.CatalogGovernanceRatchet.Merge(tableMeta, existing);
                        tableMetadataMap[tableId] = merged;
                        await _metadataRepo.UpsertTableMetadataAsync(merged, ct);
                    }
                }
                catch (Exception ex)
                {
                    var warning = $"Failed to map or upsert table {omTable.FullyQualifiedName}: {ex.Message}";
                    _logger.LogWarning(ex, "{Warning}", warning);
                    warnings.Add(warning);
                }
            }

            // 2. Consents (SEC E-05 / EX-02: fail-safe – a failure while reading OM principals or the persisted sync
            // consents neither creates nor revokes anything in this run and is reported as Success=false).
            bool success = true;
            int syncedConsentsCount = 0;
            try
            {
                var outcome = await ReconcileConsentsAsync(omOptions, tableMetadataMap, tableTenantMap, affectedTables, warnings, dryRun, ct);
                syncedConsentsCount = outcome.SyncedConsents;
                success = outcome.Success;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                success = false;
                var warn = $"Failed during policy/role/team synchronization: {ex.Message}";
                _logger.LogError(ex, "OpenMetadata consent synchronization failed; no consent was created or revoked in this run.");
                warnings.Add(warn);
            }

            // 3. Increment policy epochs for affected tables
            if (!dryRun)
            {
                foreach (var table in affectedTables)
                {
                    try
                    {
                        await _epochRepo.IncrementTableEpochAsync(table, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to increment epoch for table {Table}", table);
                    }
                }
            }

            _logger.LogInformation(
                "OpenMetadata synchronization completed. Tables: {Tables}, Consents: {Consents}, MaskingRules: {Masks}, DryRun: {DryRun}, Success: {Success}",
                syncedTablesCount, syncedConsentsCount, syncedMaskingRulesCount, dryRun, success);

            return new OpenMetadataSyncResult(
                syncedTablesCount,
                syncedConsentsCount,
                syncedMaskingRulesCount,
                affectedTables.ToList(),
                warnings,
                success);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private readonly record struct ReconcileOutcome(int SyncedConsents, bool Success);

    /// <summary>
    /// SEC E-05 / EX-02 / H-19: Computes the consents the sync would actually hold (after AutoCreateConsents,
    /// RoleToGatewayRoleMap, explicit SID maps and the four-eyes / Art. 9 / sensitivity exclusion), then reconciles them
    /// against ALL active consents carrying <see cref="OpenMetadataSyncConsentMarker"/> (no subject or table filter):
    /// matching consents are refreshed to the short validity window, everything else with the marker is revoked
    /// (deleted roles/users, filtered tables, former auto-grants after AutoCreateConsents was switched off), and missing
    /// ones are created. All reads happen before the first write; an exception in that phase aborts without changes.
    /// </summary>
    private async Task<ReconcileOutcome> ReconcileConsentsAsync(
        OpenMetadataOptions omOptions,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> tableMetadataMap,
        IReadOnlyDictionary<TableIdentifier, TenantId> tableTenantMap,
        HashSet<TableIdentifier> affectedTables,
        List<string> warnings,
        bool dryRun,
        CancellationToken ct)
    {
        // ---- Phase 1: read everything (no writes) ----
        var policies = await _client.GetPoliciesAsync(ct);
        var roles = await _client.GetRolesAsync(ct);
        var teams = await _client.GetTeamsAsync(ct);
        var users = await _client.GetUsersAsync(ct);

        var policyById = new Dictionary<Guid, OpenMetadataPolicy>();
        var policyByName = new Dictionary<string, OpenMetadataPolicy>(StringComparer.OrdinalIgnoreCase);
        var ambiguousPolicyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in policies)
        {
            policyById.TryAdd(policy.Id, policy);
            if (string.IsNullOrEmpty(policy.Name) || ambiguousPolicyNames.Contains(policy.Name))
            {
                continue;
            }

            if (!policyByName.TryAdd(policy.Name, policy))
            {
                // Names differing only in case are ambiguous – resolve such references by id only.
                policyByName.Remove(policy.Name);
                ambiguousPolicyNames.Add(policy.Name);
            }
        }

        var teamIndex = TeamSidIndex.Build(omOptions.TeamToGroupSidMap, _logger);
        var userIndex = UserSidIndex.Build(omOptions.UserToUserSidMap, users, _logger);

        // SEC H-19: consents are only derived from unconditional rules with explicit data-read operations,
        // rules on resource "all" no longer apply to every table.
        var desired = new Dictionary<SyncConsentKey, Consent>();
        var validFrom = DateTimeOffset.UtcNow.AddMinutes(-5);
        // Allow: short validity (fail-closed when the sync stops). Deny only tightens access, so it keeps a long validity
        // (a failing sync must not let a deny expire); it is removed by the reconcile once OM no longer defines it.
        var validTo = DateTimeOffset.UtcNow.Add(SyncConsentValidity(omOptions));
        var denyValidTo = DateTimeOffset.UtcNow.Add(DenyConsentValidity);

        void AddForPolicies(IEnumerable<OpenMetadataPolicy> sourcePolicies, Func<TableMetadata, TenantId, ConsentEffect, Consent> factory)
        {
            foreach (var policy in sourcePolicies.Where(p => p.Enabled).DistinctBy(p => p.Id))
            {
                foreach (var rule in policy.Rules)
                {
                    if (!TryGetDataAccessEffect(rule, policy.Name, out var effect))
                    {
                        continue;
                    }

                    foreach (var (tableIdent, tableMeta) in tableMetadataMap)
                    {
                        if (IsRuleApplicableToTable(rule, tableIdent))
                        {
                            var tenantId = tableTenantMap.TryGetValue(tableIdent, out var tid) ? tid : TenantId.LegacySingleTenant;
                            var consent = factory(tableMeta, tenantId, effect);
                            desired.TryAdd(SyncConsentKey.From(consent), consent);
                        }
                    }
                }
            }
        }

        // 2a. Role policies (EX-01: only roles in RoleToGatewayRoleMap allowlist are mapped to Gateway roles)
        foreach (var role in roles)
        {
            if (!omOptions.RoleToGatewayRoleMap.TryGetValue(role.Name, out var gatewayRole) ||
                string.IsNullOrWhiteSpace(gatewayRole))
            {
                _logger.LogDebug("OpenMetadata role '{Role}' is not mapped in RoleToGatewayRoleMap allowlist. Skipping consent generation.", role.Name);
                continue;
            }

            string mappedRoleName = gatewayRole;
            AddForPolicies(ResolvePolicies(role.Policies, policyById, policyByName),
                (meta, tenantId, effect) => CreateConsentForRole(mappedRoleName, meta, tenantId, effect, validFrom, effect == ConsentEffect.Deny ? denyValidTo : validTo));
        }

        // 2b. Team policies (SEC E-07: grantee taken directly from the resolved explicit mapping)
        foreach (var team in teams)
        {
            if (!teamIndex.TryResolve(team, out var teamSid))
            {
                continue;
            }

            AddForPolicies(ResolvePolicies(team.Policies, policyById, policyByName),
                (meta, tenantId, effect) => CreateConsentForSid(GranteeType.Group, teamSid, meta, tenantId, effect, validFrom, effect == ConsentEffect.Deny ? denyValidTo : validTo));
        }

        // 2c. User policies (EX-01 / E-07: resolved only via stable id or normalized, unambiguous email, never by name)
        foreach (var user in users)
        {
            if (!userIndex.TryResolve(user, out var userSid))
            {
                continue;
            }

            var userPolicies = new List<OpenMetadataPolicy>();
            foreach (var roleRef in user.Roles)
            {
                var matchedRole = roles.FirstOrDefault(r =>
                    (roleRef.Id.HasValue && r.Id == roleRef.Id.Value) ||
                    string.Equals(r.Name, roleRef.Name, StringComparison.OrdinalIgnoreCase));
                if (matchedRole != null)
                {
                    userPolicies.AddRange(ResolvePolicies(matchedRole.Policies, policyById, policyByName));
                }
            }

            AddForPolicies(userPolicies,
                (meta, tenantId, effect) => CreateConsentForSid(GranteeType.User, userSid, meta, tenantId, effect, validFrom, effect == ConsentEffect.Deny ? denyValidTo : validTo));
        }

        // Filter: what would actually be held by the sync.
        int syncedConsentsCount = 0;
        var effective = new Dictionary<SyncConsentKey, Consent>();
        foreach (var (key, consent) in desired)
        {
            try
            {
                consent.Validate();
            }
            catch (InvalidOperationException ex)
            {
                var warn = $"Invalid OpenMetadata-derived consent for {consent.TableIdentifier}: {ex.Message}";
                _logger.LogWarning("{Warning}", warn);
                warnings.Add(warn);
                continue;
            }

            syncedConsentsCount++;

            if (consent.Effect == ConsentEffect.Allow && !omOptions.AutoCreateConsents)
            {
                // SEC H-19: no automatic data grants from metadata policies – proposal only (requires four-eyes approval in the gateway).
                _logger.LogInformation(
                    "OpenMetadata consent proposal (not created, AutoCreateConsents=false): {Effect} {GranteeType} '{Grantee}' on {Table}.",
                    consent.Effect, consent.GranteeType, key.Grantee, consent.TableIdentifier);
                continue;
            }

            // EX-01 / E-06: four-eyes, Art. 9, HIGH/RESTRICTED or tables with sensitive columns are never auto-granted.
            if (consent.Effect == ConsentEffect.Allow &&
                (!tableMetadataMap.TryGetValue(consent.TableIdentifier, out var targetTableMeta) || IsExcludedFromAutoGrant(targetTableMeta)))
            {
                _logger.LogWarning(
                    "OpenMetadata sync skipped automatic allow consent for sensitive table '{Table}' ({GranteeType} '{Grantee}'): table requires four-eyes approval (Article 9 / HIGH / RESTRICTED / sensitive columns).",
                    consent.TableIdentifier, consent.GranteeType, key.Grantee);
                continue;
            }

            effective[key] = consent;
        }

        if (dryRun)
        {
            return new ReconcileOutcome(syncedConsentsCount, true);
        }

        var existingSyncConsents = await _consentRepo.GetActiveConsentsByConsentRequestIdAsync(OpenMetadataSyncConsentMarker, DateTimeOffset.UtcNow, ct)
                                   ?? Array.Empty<Consent>();

        var managedTenants = new HashSet<TenantId>();
        foreach (var tid in tableTenantMap.Values)
        {
            managedTenants.Add(tid);
        }
        if (!string.IsNullOrWhiteSpace(omOptions.DefaultTenantId))
        {
            managedTenants.Add(new TenantId(omOptions.DefaultTenantId.Trim()));
        }
        foreach (var (_, tStr) in omOptions.ServiceDatabaseToTenantMap)
        {
            if (!string.IsNullOrWhiteSpace(tStr))
            {
                managedTenants.Add(new TenantId(tStr.Trim()));
            }
        }

        // ---- Phase 2: apply ----
        bool success = true;
        var kept = new HashSet<SyncConsentKey>();

        foreach (var existing in existingSyncConsents)
        {
            if (existing.ConsentRequestId != OpenMetadataSyncConsentMarker)
            {
                continue;
            }

            // EX-03: Scope reconcile to managed tenants. Consents belonging to other tenants are untouched.
            if (!managedTenants.Contains(existing.TenantId))
            {
                continue;
            }

            var key = SyncConsentKey.From(existing);
            if (effective.ContainsKey(key) && kept.Add(key))
            {
                try
                {
                    if (existing.Effect == ConsentEffect.Allow)
                    {
                        // Short validity, refreshed on every successful run (a failing sync closes access instead of keeping it open).
                        await _consentRepo.ExtendConsentExpiryAsync(existing.Id, validTo, ct);
                    }
                    else if (existing.ValidTo < DateTimeOffset.UtcNow.Add(DenyRefreshThreshold))
                    {
                        await _consentRepo.ExtendConsentExpiryAsync(existing.Id, denyValidTo, ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    success = false;
                    var warn = $"Failed to refresh OpenMetadata-synced consent {existing.Id}: {ex.Message}";
                    _logger.LogWarning(ex, "{Warning}", warn);
                    warnings.Add(warn);
                }

                continue;
            }

            try
            {
                // SEC E-05: system revocation bound to the sync marker (the sync actor is no data owner of the table).
                var revoked = await _consentRepo.RevokeSystemConsentAsync(
                    existing.Id,
                    OpenMetadataSyncConsentMarker,
                    SyncActorSid,
                    "OpenMetadata sync no longer grants this access (source rule removed, grantee/table unmapped, auto-grant disabled or excluded).",
                    ct);
                if (revoked)
                {
                    affectedTables.Add(existing.TableIdentifier);
                    _logger.LogInformation("Revoked stale OpenMetadata-synced consent {ConsentId} on {Table}.", existing.Id, existing.TableIdentifier);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                success = false;
                var warn = $"Failed to revoke stale consent {existing.Id}: {ex.Message}";
                _logger.LogWarning(ex, "{Warning}", warn);
                warnings.Add(warn);
            }
        }

        foreach (var (key, consent) in effective)
        {
            if (kept.Contains(key))
            {
                continue;
            }

            try
            {
                await _consentRepo.CreateConsentAsync(consent, ct);
                affectedTables.Add(consent.TableIdentifier);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                success = false;
                var warn = $"Failed to create consent for {consent.TableIdentifier}: {ex.Message}";
                _logger.LogWarning(ex, "{Warning}", warn);
                warnings.Add(warn);
            }
        }

        return new ReconcileOutcome(syncedConsentsCount, success);
    }

    /// <summary>SEC E-05 / EX-02: sync consents are valid for two sync intervals and refreshed on every successful run.</summary>
    internal static TimeSpan SyncConsentValidity(OpenMetadataOptions omOptions) =>
        TimeSpan.FromMinutes(2 * Math.Max(1, omOptions.SyncIntervalMinutes));

    private static readonly TimeSpan DenyConsentValidity = TimeSpan.FromDays(365);
    private static readonly TimeSpan DenyRefreshThreshold = TimeSpan.FromDays(30);

    private static readonly HashSet<string> AutoGrantableSensitivities = new(StringComparer.OrdinalIgnoreCase) { "NORMAL", "LOW", "PUBLIC", "INTERNAL" };

    /// <summary>
    /// SEC E-06: Allow consents are never created automatically for tables that require four-eyes approval, are highly
    /// sensitive (HIGH, Art. 9, RESTRICTED or any unknown classification) or contain sensitive / masked columns.
    /// </summary>
    internal static bool IsExcludedFromAutoGrant(TableMetadata metadata)
    {
        var table = metadata.Table;
        var sensitivity = string.IsNullOrWhiteSpace(table.Sensitivity) ? "NORMAL" : table.Sensitivity.Trim();
        return table.RequiresFourEyes ||
               table.IsHighlySensitive ||
               !AutoGrantableSensitivities.Contains(sensitivity) ||
               metadata.Columns.Any(c => c.IsSensitive) ||
               metadata.ColumnMaskingRules.Count > 0;
    }

    public async Task<bool> HandleWebhookEventAsync(string eventPayload, string? signatureHeader = null, CancellationToken ct = default)
    {
        var omOptions = _options.Value.OpenMetadata;

        bool bypassSignature = _options.Value.IsWebhookSignatureBypassed;
        if (!bypassSignature)
        {
            // Enforce signature verification (fail closed)
            if (string.IsNullOrWhiteSpace(omOptions.WebhookSecret))
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: WebhookSecret is not configured.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(signatureHeader))
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: missing signature header.");
                return false;
            }

            if (!VerifyWebhookSignature(eventPayload, signatureHeader, omOptions.WebhookSecret, _logger))
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: signature verification failed.");
                return false;
            }
        }
        else
        {
            _logger.LogWarning("[INSECURE GETTING STARTED] Bypassing OpenMetadata webhook signature verification.");
        }

        OpenMetadataWebhookEvent? webhookEvent;
        try
        {
            webhookEvent = JsonSerializer.Deserialize<OpenMetadataWebhookEvent>(eventPayload, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize OpenMetadata webhook payload.");
            return false;
        }

        if (webhookEvent == null)
        {
            _logger.LogWarning("OpenMetadata webhook event payload is null.");
            return false;
        }

        bool ignoreTimestampTolerance = _options.Value.IsWebhookTimestampToleranceIgnored;
        if (!ignoreTimestampTolerance)
        {
            // SEC-07: Enforce mandatory Id and Timestamp to prevent replay attacks (fail-closed)
            if (!webhookEvent.Id.HasValue || !webhookEvent.Timestamp.HasValue)
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: mandatory event 'Id' or 'Timestamp' is missing (fail-closed replay defense).");
                return false;
            }

            // Validate timestamp to prevent replay attacks (tolerance: 5 minutes)
            var eventTime = webhookEvent.Timestamp.Value > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(webhookEvent.Timestamp.Value)
                : DateTimeOffset.FromUnixTimeSeconds(webhookEvent.Timestamp.Value);

            var skew = Math.Abs((DateTimeOffset.UtcNow - eventTime).TotalMinutes);
            if (skew > 5)
            {
                _logger.LogWarning("Rejecting OpenMetadata webhook: event timestamp is skewed or outside acceptable replay window ({Skew:F1} minutes).", skew);
                return false;
            }
        }

        // Event ID deduplication
        if (webhookEvent.Id.HasValue && !ProcessedWebhookEvents.TryAdd(webhookEvent.Id.Value, DateTimeOffset.UtcNow))
        {
            _logger.LogInformation("OpenMetadata webhook event {EventId} has already been processed. Skipping duplicate.", webhookEvent.Id.Value);
            return true;
        }

            // Bound the size of the deduplication dictionary
            if (ProcessedWebhookEvents.Count > 10_000)
            {
                var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
                foreach (var (k, v) in ProcessedWebhookEvents)
                {
                    if (v < cutoff)
                    {
                        ProcessedWebhookEvents.TryRemove(k, out _);
                    }
                }
            }

        _logger.LogInformation(
            "Processing OpenMetadata webhook event: Type={Type}, EntityType={Entity}, FQN={Fqn}",
            webhookEvent.EventType, webhookEvent.EntityType, webhookEvent.EntityFullyQualifiedName);

        if (string.Equals(webhookEvent.EntityType, "table", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(webhookEvent.EntityFullyQualifiedName))
        {
            var omTable = await _client.GetTableByFqnAsync(webhookEvent.EntityFullyQualifiedName, ct);
            if (omTable != null)
            {
                // SEC E-09 / EX-06: the webhook path applies the same ServiceFilter / domain mapping as the full sync.
                if (!OpenMetadataTableIdentity.TryResolve(omTable, omOptions, out var resolved, out var reason))
                {
                    _logger.LogWarning("Ignoring OpenMetadata webhook table event for {Fqn}: {Reason}.", omTable.FullyQualifiedName, reason);
                    return true;
                }

                var tableId = resolved.Identifier;
                // SEC M-32: webhook-triggered updates use the same tighten-only merge.
                var existing = await _metadataRepo.GetTableMetadataAsync(tableId, ct);
                var (tableMeta, _) = MapToTableMetadata(omTable, tableId, omOptions, isNewTable: existing == null);
                var merged = Autheris.Application.DataCatalog.Services.CatalogGovernanceRatchet.Merge(tableMeta, existing);
                await _metadataRepo.UpsertTableMetadataAsync(merged, ct);
                await _epochRepo.IncrementTableEpochAsync(tableId, ct);
                return true;
            }
        }
        else if (string.Equals(webhookEvent.EntityType, "policy", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(webhookEvent.EntityType, "role", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(webhookEvent.EntityType, "team", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(webhookEvent.EntityType, "user", StringComparison.OrdinalIgnoreCase))
        {
            var result = await SyncPermissionsAsync(dryRun: false, ct);
            return result.Success;
        }

        return true;
    }

    public static bool VerifyWebhookSignature(string payload, string signatureHeader, string secret, ILogger? logger = null)
    {
        try
        {
            var cleanSignature = signatureHeader.Trim();
            if (cleanSignature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            {
                cleanSignature = cleanSignature[7..];
            }

            var expectedBytes = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
            var expectedHex = Convert.ToHexStringLower(expectedBytes);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedHex),
                Encoding.UTF8.GetBytes(cleanSignature.ToLowerInvariant()));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "OpenMetadata webhook signature verification failed due to an exception.");
            return false;
        }
    }

    private (TableMetadata Metadata, int MaskingRulesCount) MapToTableMetadata(
        OpenMetadataTable omTable,
        TableIdentifier tableId,
        OpenMetadataOptions omOptions,
        bool isNewTable)
    {
        var catalogOptions = _options.Value.Catalog;
        var art9Tags = catalogOptions.GdprArticle9Tags;
        var piiTags = catalogOptions.PiiTags;

        var tableGuid = omTable.Id == Guid.Empty ? Guid.NewGuid() : omTable.Id;
        var columns = new List<TableColumn>();
        var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
        bool anyArt9Column = false;

        foreach (var omCol in omTable.Columns)
        {
            var colId = Guid.NewGuid();
            bool isSensitive = false;
            string? ruleType = null;

            foreach (var tag in omCol.Tags)
            {
                if (omOptions.TagToMaskingRuleMap.TryGetValue(tag.TagFQN, out var mappedRule))
                {
                    ruleType ??= mappedRule;
                    isSensitive = true;
                }

                // SEC E-06: Art. 9 and PII tags (Catalog.GdprArticle9Tags / Catalog.PiiTags, like the catalog sync) mark the
                // column as sensitive; without an explicit masking rule the column is redacted.
                var isArt9Tag = MatchesAnyTag(tag.TagFQN, art9Tags);
                anyArt9Column |= isArt9Tag;
                if (isArt9Tag ||
                    MatchesAnyTag(tag.TagFQN, piiTags) ||
                    tag.TagFQN.StartsWith("PII.", StringComparison.OrdinalIgnoreCase) ||
                    tag.TagFQN.StartsWith("PersonalData.", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag.TagFQN, "PII", StringComparison.OrdinalIgnoreCase))
                {
                    isSensitive = true;
                }
            }

            if (isSensitive)
            {
                ruleType ??= "REDACT";
                maskingRules[omCol.Name] = new MaskingRule
                {
                    Id = Guid.NewGuid(),
                    TableColumnId = colId,
                    RuleType = ruleType,
                    PatternOrFormat = string.Equals(ruleType, "HMAC_SHA256", StringComparison.OrdinalIgnoreCase) ? "SHA256" : null,
                    Replacement = string.Equals(ruleType, "REDACT", StringComparison.OrdinalIgnoreCase) ? "[REDACTED]" : null,
                    HmacKeyId = string.Equals(ruleType, "HMAC_SHA256", StringComparison.OrdinalIgnoreCase) ? _options.Value.DataMasking.HmacKeyId : null
                };
            }

            columns.Add(new TableColumn
            {
                Id = colId,
                TableId = tableGuid,
                ColumnName = omCol.Name,
                DataType = omCol.DataType,
                IsSensitive = isSensitive
            });
        }

        // SEC E-06: Art. 9 on the table or on any column => HIGH + four-eyes (same rule as the catalog sync);
        // table-level PII / "Sensitive" tags => HIGH.
        var isArt9 = anyArt9Column || omTable.Tags.Any(t => MatchesAnyTag(t.TagFQN, art9Tags));
        var isHigh = isArt9 ||
                     omTable.Tags.Any(t => t.TagFQN.Contains("Sensitive", StringComparison.OrdinalIgnoreCase) || MatchesAnyTag(t.TagFQN, piiTags));

        var tableEntity = new Table
        {
            Id = tableGuid,
            SourceType = omTable.ServiceType ?? "PostgreSQL",
            SourceName = tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            DisplayName = omTable.DisplayName ?? omTable.Name,
            Sensitivity = isHigh ? "HIGH" : "NORMAL",
            RequiresFourEyes = isArt9 || omTable.Tags.Any(t => t.TagFQN.Contains("FourEyes", StringComparison.OrdinalIgnoreCase)),
            // SEC E-09 / EX-06: new tables stay inactive until a gateway admin activates them (OpenMetadata.ActivateNewTables).
            // Existing tables keep their persisted state through the tighten-only ratchet.
            IsActive = !isNewTable || omOptions.ActivateNewTables
        };

        var metadata = new TableMetadata
        {
            Table = tableEntity,
            Identifier = tableId,
            Columns = columns,
            ColumnMaskingRules = maskingRules
        };

        return (metadata, maskingRules.Count);
    }

    /// <summary>
    /// SEC E-06: Tag match against configured catalog tags: exact (case-insensitive, like the catalog sync) or
    /// hierarchical in OpenMetadata FQN form ("Classification.Tag" matches "Tag", "GDPR.Art9.Health" matches "GDPR.Art9").
    /// </summary>
    internal static bool MatchesAnyTag(string tagFqn, IEnumerable<string> configuredTags)
    {
        if (string.IsNullOrWhiteSpace(tagFqn))
        {
            return false;
        }

        foreach (var configured in configuredTags)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            if (string.Equals(tagFqn, configured, StringComparison.OrdinalIgnoreCase) ||
                tagFqn.EndsWith("." + configured, StringComparison.OrdinalIgnoreCase) ||
                tagFqn.StartsWith(configured + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// SEC E-07: Team → group SID resolution with separate indexes per key kind. Config keys may be prefixed
    /// (<c>id:</c>, <c>fqn:</c>, <c>name:</c>); unprefixed GUID keys are ids, other unprefixed keys are FQN/name (legacy).
    /// Ids are compared as GUIDs, FQNs and names ordinally. Keys that map to different SIDs are dropped (ambiguous).
    /// </summary>
    internal sealed class TeamSidIndex
    {
        private readonly SidKeyMap<Guid> _byId = new(EqualityComparer<Guid>.Default);
        private readonly SidKeyMap<string> _byFqn = new(StringComparer.Ordinal);
        private readonly SidKeyMap<string> _byName = new(StringComparer.Ordinal);

        public static TeamSidIndex Build(IReadOnlyDictionary<string, string> map, Microsoft.Extensions.Logging.ILogger logger)
        {
            var index = new TeamSidIndex();
            foreach (var (rawKey, sidValue) in map)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(sidValue))
                {
                    continue;
                }

                var key = rawKey.Trim();
                var sid = new Sid(sidValue.Trim());
                if (TryStripPrefix(key, "id:", out var idPart))
                {
                    if (Guid.TryParse(idPart, out var id)) index._byId.Add(id, sid);
                    else logger.LogWarning("Ignoring TeamToGroupSidMap entry with invalid team id.");
                }
                else if (TryStripPrefix(key, "fqn:", out var fqnPart))
                {
                    index._byFqn.Add(fqnPart, sid);
                }
                else if (TryStripPrefix(key, "name:", out var namePart))
                {
                    index._byName.Add(namePart, sid);
                }
                else if (Guid.TryParse(key, out var legacyId))
                {
                    index._byId.Add(legacyId, sid);
                }
                else
                {
                    index._byFqn.Add(key, sid);
                    index._byName.Add(key, sid);
                }
            }

            return index;
        }

        public bool TryResolve(OpenMetadataTeam team, out Sid sid)
        {
            if (team.Id != Guid.Empty && _byId.TryGet(team.Id, out sid))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(team.FullyQualifiedName) && _byFqn.TryGet(team.FullyQualifiedName, out sid))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(team.Name) && _byName.TryGet(team.Name, out sid))
            {
                return true;
            }

            sid = default;
            return false;
        }
    }

    /// <summary>
    /// SEC E-07 / EX-01: User → user SID resolution only by stable OM id (GUID) or by e-mail, never by user name.
    /// E-mail normalization (documented): trimmed, compared OrdinalIgnoreCase on BOTH sides (config key and OM value).
    /// Config keys may be prefixed (<c>id:</c>, <c>email:</c>); unprefixed GUID keys are ids, keys containing '@' are
    /// e-mails, other keys are ignored. An e-mail used by more than one OM user (case-insensitive) is ambiguous and never
    /// resolves; config keys that map to different SIDs are dropped.
    /// </summary>
    internal sealed class UserSidIndex
    {
        private readonly SidKeyMap<Guid> _byId = new(EqualityComparer<Guid>.Default);
        private readonly SidKeyMap<string> _byEmail = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguousOmEmails = new(StringComparer.OrdinalIgnoreCase);

        public static UserSidIndex Build(
            IReadOnlyDictionary<string, string> map,
            IEnumerable<OpenMetadataUser> omUsers,
            Microsoft.Extensions.Logging.ILogger logger)
        {
            var index = new UserSidIndex();
            foreach (var (rawKey, sidValue) in map)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(sidValue))
                {
                    continue;
                }

                var key = rawKey.Trim();
                var sid = new Sid(sidValue.Trim());
                if (TryStripPrefix(key, "id:", out var idPart))
                {
                    if (Guid.TryParse(idPart, out var id)) index._byId.Add(id, sid);
                    else logger.LogWarning("Ignoring UserToUserSidMap entry with invalid user id.");
                }
                else if (TryStripPrefix(key, "email:", out var emailPart))
                {
                    index._byEmail.Add(emailPart, sid);
                }
                else if (Guid.TryParse(key, out var legacyId))
                {
                    index._byId.Add(legacyId, sid);
                }
                else if (key.Contains('@', StringComparison.Ordinal))
                {
                    index._byEmail.Add(key, sid);
                }
                else
                {
                    logger.LogWarning("Ignoring UserToUserSidMap entry that is neither an OpenMetadata user id nor an e-mail address (user names are never used).");
                }
            }

            var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in omUsers)
            {
                var email = user.Email?.Trim();
                if (!string.IsNullOrEmpty(email) && !seenEmails.Add(email))
                {
                    index._ambiguousOmEmails.Add(email);
                }
            }

            if (index._ambiguousOmEmails.Count > 0)
            {
                logger.LogWarning("{Count} OpenMetadata e-mail address(es) are used by more than one user (case-insensitive); they are not mapped to SIDs.", index._ambiguousOmEmails.Count);
            }

            return index;
        }

        public bool TryResolve(OpenMetadataUser user, out Sid sid)
        {
            if (user.Id != Guid.Empty && _byId.TryGet(user.Id, out sid))
            {
                return true;
            }

            var email = user.Email?.Trim();
            if (!string.IsNullOrEmpty(email) && !_ambiguousOmEmails.Contains(email) && _byEmail.TryGet(email, out sid))
            {
                return true;
            }

            sid = default;
            return false;
        }
    }

    /// <summary>Key → SID map that drops keys configured with conflicting SIDs (fail-closed).</summary>
    internal sealed class SidKeyMap<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, Sid> _map;
        private readonly HashSet<TKey> _ambiguous;

        public SidKeyMap(IEqualityComparer<TKey> comparer)
        {
            _map = new Dictionary<TKey, Sid>(comparer);
            _ambiguous = new HashSet<TKey>(comparer);
        }

        public void Add(TKey key, Sid sid)
        {
            if (_ambiguous.Contains(key))
            {
                return;
            }

            if (_map.TryGetValue(key, out var current))
            {
                if (!string.Equals(current.Value, sid.Value, StringComparison.Ordinal))
                {
                    _map.Remove(key);
                    _ambiguous.Add(key);
                }

                return;
            }

            _map[key] = sid;
        }

        public bool TryGet(TKey key, out Sid sid) => _map.TryGetValue(key, out sid);
    }

    private static bool TryStripPrefix(string key, string prefix, out string rest)
    {
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.Length > prefix.Length)
        {
            rest = key[prefix.Length..].Trim();
            return rest.Length > 0;
        }

        rest = string.Empty;
        return false;
    }

    private static List<OpenMetadataPolicy> ResolvePolicies(
        IEnumerable<OpenMetadataEntityReference> policyRefs,
        IReadOnlyDictionary<Guid, OpenMetadataPolicy> byId,
        IReadOnlyDictionary<string, OpenMetadataPolicy> byName)
    {
        var resolved = new List<OpenMetadataPolicy>();
        foreach (var pRef in policyRefs)
        {
            if (pRef.Id.HasValue && byId.TryGetValue(pRef.Id.Value, out var policyById))
            {
                resolved.Add(policyById);
            }
            else if (!string.IsNullOrEmpty(pRef.Name) && byName.TryGetValue(pRef.Name, out var policyByName))
            {
                resolved.Add(policyByName);
            }
        }
        return resolved;
    }

    private static bool IsRuleApplicableToTable(OpenMetadataRule rule, TableIdentifier table)
    {
        // SEC H-19: resource "all" is no longer treated as "every table"; only the explicit entity type "table"
        // or explicitly named tables apply.
        if (rule.Resources.Any(r => string.Equals(r, "table", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return rule.Resources.Any(r =>
            string.Equals(r, table.TableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, $"{table.Schema}.{table.TableName}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r, $"{table.Domain}.{table.Schema}.{table.TableName}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// SEC H-19: Only unconditional rules with explicit data-read operations produce consents.
    /// Allow: ViewAll / ViewSampleData. Deny (tightening only): additionally "All".
    /// </summary>
    private bool TryGetDataAccessEffect(OpenMetadataRule rule, string policyName, out ConsentEffect effect)
    {
        var isDeny = string.Equals(rule.Effect, "deny", StringComparison.OrdinalIgnoreCase);
        effect = isDeny ? ConsentEffect.Deny : ConsentEffect.Allow;

        if (!isDeny && !string.Equals(rule.Effect, "allow", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Condition))
        {
            _logger.LogWarning("Ignoring OpenMetadata rule '{Rule}' in policy '{Policy}': conditional rules are not mapped to gateway consents.", rule.Name, policyName);
            return false;
        }

        var allowedOperations = isDeny ? DenyDataOperations : AllowDataOperations;
        if (!rule.Operations.Any(op => allowedOperations.Contains(op)))
        {
            _logger.LogDebug("Ignoring OpenMetadata rule '{Rule}' in policy '{Policy}': no data-read operation.", rule.Name, policyName);
            return false;
        }

        return true;
    }

    internal readonly record struct SyncConsentKey(TenantId TenantId, TableIdentifier Table, GranteeType GranteeType, string Grantee, ConsentEffect Effect)
    {
        public static SyncConsentKey From(Consent consent)
        {
            var grantee = consent.GranteeType == GranteeType.Role
                ? (consent.RoleName ?? string.Empty).ToUpperInvariant()
                : (consent.GranteeSid?.Value ?? string.Empty).ToUpperInvariant();
            return new SyncConsentKey(consent.TenantId, consent.TableIdentifier, consent.GranteeType, grantee, consent.Effect);
        }
    }

    private static Consent CreateConsentForRole(string roleName, TableMetadata table, TenantId tenantId, ConsentEffect effect, DateTimeOffset validFrom, DateTimeOffset validTo)
    {
        return new Consent
        {
            Id = Guid.NewGuid(),
            ConsentRequestId = OpenMetadataSyncConsentMarker,
            TableId = table.Table.Id,
            TableIdentifier = table.Identifier,
            TenantId = tenantId,
            GranteeType = GranteeType.Role,
            RoleName = roleName,
            Effect = effect,
            ValidFrom = validFrom,
            ValidTo = validTo
        };
    }

    private static Consent CreateConsentForSid(GranteeType granteeType, Sid sid, TableMetadata table, TenantId tenantId, ConsentEffect effect, DateTimeOffset validFrom, DateTimeOffset validTo)
    {
        return new Consent
        {
            Id = Guid.NewGuid(),
            TableId = table.Table.Id,
            TableIdentifier = table.Identifier,
            TenantId = tenantId,
            ConsentRequestId = OpenMetadataSyncConsentMarker,
            GranteeType = granteeType,
            GranteeSid = sid,
            Effect = effect,
            ValidFrom = validFrom,
            ValidTo = validTo
        };
    }
}
