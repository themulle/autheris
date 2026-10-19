namespace Autheris.Application.Governance.Services;

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Events;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Events;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Two-phase access planning and administrative control plane service.
/// Implements ADR-03 (Two-Phase Confirmation), ADR-05 (RFC 6238 TOTP Step-Up),
/// SEC M-30 (Inactive Datasource Onboarding), and R-60 to R-64.
/// </summary>
public class AccessPlanningService : IAccessPlanningService
{
    private const string SigningKeyInfo = "autheris/access-plan/confirmation-token/v1";

    private readonly byte[] _hmacKey;
    private readonly IAccessPlanStore _store;

    private readonly ITableMetadataRepository _tableRepo;
    private readonly IRebacStore _rebacStore;
    private readonly IAuditLogRepository _auditRepo;
    private readonly ITotpVerificationService? _totpService;
    private readonly ITotpSecretStore? _totpSecretStore;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IPolicyEpochRepository? _policyEpochRepo;
    private readonly IPolicyEnforcementService? _policyEnforcement;
    private readonly IDataOwnershipRepository? _dataOwnershipRepo;
    private readonly ILogger<AccessPlanningService> _logger;
    private readonly IGatewayEventBus? _eventBus;
    private readonly AccessPlanOptions _planOptions;
    private readonly TimeProvider _time;
    private readonly bool _allowDemoDirectory;
    private readonly IGrantPolicy _grantPolicy;
    private readonly IRowFilterEvaluationAdminService? _rowFilterEvaluationAdmin;

    private void EnsureGrantsAllowed(AdminPlanAccessRequest request, TableMetadata? meta, IReadOnlyList<AccessPlanDiffItem> diffs, DateTimeOffset now)
    {
        foreach (var grant in request.Grants)
        {
            var clearPii = diffs.Any(d => d.IsPii
                && string.Equals(d.Principal, grant.Principal, StringComparison.Ordinal)
                && string.Equals(d.AfterState, "clear", StringComparison.OrdinalIgnoreCase));
            var sensitivity = clearPii || meta is null
                ? GrantSensitivity.Sensitive
                : GrantClassifier.Classify([meta]);
            var decision = _grantPolicy.Evaluate(new GrantRequest(
                GranteeType.User,
                grant.Principal,
                GrantKind.McpAdminGrant,
                grant.ValidUntil,
                now,
                sensitivity,
                Justification: request.Reason));
            GrantPolicyViolationException.ThrowIfDenied(decision);
        }
    }

    private static string? NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;

    private static readonly HashSet<string> PiiKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "email", "mail", "ssn", "iban", "phone", "mobile", "tel", "name",
        "firstname", "lastname", "surname", "salary", "wage", "tax",
        "credit", "card", "passport", "birth", "dob", "address", "street", "zip"
    };

    private static readonly List<PrincipalResolutionItem> DefaultDirectory =
    [
        new("S-1-5-21-1001", "David Miller", "User", false, ["Engineers", "DataStewards"]),
        new("S-1-5-21-1002", "Philipp Meier", "User", false, ["Compliance", "Auditors"]),
        new("S-1-5-21-1003", "Alice Analyst", "User", false, ["Analytics"]),
        new("S-1-5-21-1004", "Bob Manager", "User", false, ["Management"]),
        new("S-1-5-21-2001", "Data Engineering", "Group", false, []),
        new("S-1-5-21-3001", "sp_lakehouse_sync", "ServicePrincipal", false, [])
    ];

    public AccessPlanningService(
        IAuditLogRepository auditLogRepository,
        ITableMetadataRepository? tableMetadataRepository = null,
        IRebacStore? rebacStore = null,
        ITotpVerificationService? totpVerificationService = null,
        ITotpSecretStore? totpSecretStore = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IPolicyEpochRepository? policyEpochRepository = null,
        IPolicyEnforcementService? policyEnforcementService = null,
        IDataOwnershipRepository? dataOwnershipRepository = null,
        IOptions<GatewayOptions>? options = null,
        ILogger<AccessPlanningService>? logger = null,
        IGatewayEventBus? eventBus = null,
        IOptions<AccessPlanOptions>? planOptions = null,
        IHostEnvironment? hostEnvironment = null,
        TimeProvider? timeProvider = null,
        IAccessPlanStore? accessPlanStore = null,
        IClusterStateTopology? clusterTopology = null,
        IGrantPolicy? grantPolicy = null,
        IRowFilterEvaluationAdminService? rowFilterEvaluationAdmin = null)
    {
        _rowFilterEvaluationAdmin = rowFilterEvaluationAdmin;
        _grantPolicy = grantPolicy ?? new GrantPolicy();
        _auditRepo = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _tableRepo = tableMetadataRepository ?? new NullTableMetadataRepository();
        _rebacStore = rebacStore ?? new NullRebacStore();
        _totpService = totpVerificationService;
        _totpSecretStore = totpSecretStore;
        _secretProvider = secretProvider;
        _policyEpochRepo = policyEpochRepository;
        _policyEnforcement = policyEnforcementService;
        _dataOwnershipRepo = dataOwnershipRepository;
        _logger = logger ?? NullLogger<AccessPlanningService>.Instance;
        _eventBus = eventBus;
        _planOptions = planOptions?.Value ?? new AccessPlanOptions();
        _time = timeProvider ?? TimeProvider.System;
        _store = accessPlanStore
            ?? new InMemoryAccessPlanStore(_time, TimeSpan.FromSeconds(_planOptions.SweepIntervalSeconds));
        _hmacKey = ResolveSigningKey(_secretProvider, _planOptions.SigningKeyRef, clusterTopology?.IsClusterWide == true);
        // SR-L-06 Part D: the demo directory is only exposed when the host is explicitly a Development host.
        _allowDemoDirectory = hostEnvironment?.IsDevelopment() == true;
    }

    public async Task<AdminPlanAccessResult> PlanAccessAsync(
        AdminPlanAccessRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        var tableId = ParseTableIdentifier(request.DatasetId);
        var meta = await _tableRepo.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);

        var diffs = new List<AccessPlanDiffItem>();
        var warnings = new List<string>();

        foreach (var grant in request.Grants)
        {
            foreach (var (column, requestedLevel) in grant.Columns)
            {
                var colMeta = meta?.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, column, StringComparison.OrdinalIgnoreCase));
                bool isPii = (colMeta?.IsSensitive ?? false) ||
                             PiiKeywords.Any(k => column.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                             (meta?.Table.IsHighlySensitive ?? false);

                string beforeState = "none";
                if (meta?.ColumnMaskingRules.TryGetValue(column, out var rule) == true)
                {
                    beforeState = string.Equals(rule.RuleType, "CLEAR", StringComparison.OrdinalIgnoreCase) ? "clear" : "mask";
                }

                diffs.Add(new AccessPlanDiffItem(
                    Principal: grant.Principal,
                    Column: column,
                    BeforeState: beforeState,
                    AfterState: requestedLevel,
                    IsPii: isPii));

                if (isPii && string.Equals(requestedLevel, "clear", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Column '{column}' for principal '{grant.Principal}' contains sensitive PII but requested clear-text access.");
                }
            }
        }

        var now = _time.GetUtcNow();

        // WP-SC-16: every individual (user) principal needs a mandatory, class-bounded validUntil already at planning time.
        EnsureGrantsAllowed(request, meta, diffs, now);

        var planId = $"plan_{Guid.NewGuid():N}";
        var planTtl = TimeSpan.FromMinutes(_planOptions.PlanTtlMinutes);
        var expiresAt = now.Add(planTtl);
        var result = new AdminPlanAccessResult(planId, request.DatasetId, diffs, warnings, expiresAt);

        // ARC-C-05: plans live in the shared store with a TTL; an unavailable store fails the operation.
        var saved = await _store.TrySavePlanAsync(
            new StoredAccessPlan(result, request, adminSid, TenantId: NormalizeTenant(tenantId)),
            planTtl,
            _planOptions.MaxPendingPlansPerAdmin,
            ct).ConfigureAwait(false);
        if (!saved)
        {
            throw new SecurityException(
                $"Too many pending access plans for this administrator (limit {_planOptions.MaxPendingPlansPerAdmin}). Apply or wait for existing plans to expire.");
        }

        if (_eventBus != null)
        {
            await _eventBus.PublishAsync(new AccessPlanCreatedEvent(
                EventId: Guid.NewGuid().ToString("N"),
                PlanId: planId,
                DatasetId: request.DatasetId,
                RequesterSid: adminSid,
                GrantCount: request.Grants.Count,
                Timestamp: DateTimeOffset.UtcNow,
                TenantId: NormalizeTenant(tenantId)), ct).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<AdminConfirmPlanResult> ConfirmPlanAsync(
        string planId,
        string totpCode,
        string adminSid,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(totpCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        var stored = await _store.GetPlanAsync(planId, ct).ConfigureAwait(false);
        if (stored == null || stored.Result.ExpiresAt <= _time.GetUtcNow())
        {
            throw new SecurityException($"Plan '{planId}' does not exist or has expired.");
        }

        if (!string.Equals(stored.AdminSid, adminSid, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("Plan confirmation must be performed by the initiating administrator.");
        }

        if (_totpService == null || _totpSecretStore == null)
        {
            throw new SecurityException("Two-factor authentication (TOTP) service is required to confirm access plans (ADR-03 / ADR-05).");
        }

        var secret = await _totpSecretStore.GetSecretAsync(adminSid, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new SecurityException("No 2FA secret enrolled for administrator. Step-up verification required.");
        }

        var isValidTotp = await _totpService.VerifyAndConsumeTotpAsync(adminSid, secret, totpCode, ct).ConfigureAwait(false);
        if (!isValidTotp)
        {
            throw new SecurityException("Invalid or already consumed TOTP verification code.");
        }

        var tokenExpiresAt = _time.GetUtcNow().AddMinutes(_planOptions.ConfirmationTokenTtlMinutes);
        var token = GenerateConfirmationToken(planId, adminSid, tokenExpiresAt);

        if (!await _store.SetConfirmedTokenAsync(planId, HashToken(token), ct).ConfigureAwait(false))
        {
            throw new SecurityException($"Plan '{planId}' does not exist or has expired.");
        }

        return new AdminConfirmPlanResult(token, tokenExpiresAt);
    }

    public async Task<AdminApplyAccessResult> ApplyAccessAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var stored = await ConsumeConfirmedPlanAsync(request, adminSid, tenantId, AccessPlanKind.Access, null, ct).ConfigureAwait(false);

        var tableId = ParseTableIdentifier(stored.Request.DatasetId);

        // WP-SC-16: re-evaluated at apply time (the plan may have been stored with a validUntil that has meanwhile lapsed).
        var applyMeta = await _tableRepo.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        EnsureGrantsAllowed(stored.Request, applyMeta, stored.Result.Diffs, _time.GetUtcNow());

        // SR-H-02: the caller's tenant scopes the ReBAC tuples and audit; the dataset domain is only the legacy fallback.
        var effectiveTenantId = !string.IsNullOrWhiteSpace(tenantId) ? tenantId : !string.IsNullOrWhiteSpace(tableId.Domain) ? tableId.Domain : "default";
        var tuples = new List<RebacTuple>();

        foreach (var grant in stored.Request.Grants)
        {
            tuples.Add(new RebacTuple(effectiveTenantId, $"user:{grant.Principal}", "can_query", $"table:{stored.Request.DatasetId}") { ExpiresAt = grant.ValidUntil });

            foreach (var (column, access) in grant.Columns)
            {
                tuples.Add(new RebacTuple(effectiveTenantId, $"user:{grant.Principal}", $"access:{access}", $"column:{stored.Request.DatasetId}.{column}") { ExpiresAt = grant.ValidUntil });
            }
        }

        await _rebacStore.AddTuplesAsync(tuples, ct).ConfigureAwait(false);

        if (_policyEpochRepo != null)
        {
            await _policyEpochRepo.IncrementTableEpochAsync(tableId, ct).ConfigureAwait(false);
        }

        if (_policyEnforcement != null)
        {
            await _policyEnforcement.ReloadPoliciesAsync(new TenantId(effectiveTenantId), ct).ConfigureAwait(false);
        }

        var auditEntry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId(effectiveTenantId),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "ADMIN_APPLY_ACCESS",
            ActorSid = new Sid(adminSid),
            TargetTable = stored.Request.DatasetId,
            Decision = "ALLOW",
            TraceId = stored.Request.DatasetId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                channel = "mcp",
                planId = request.PlanId,
                diffs = stored.Result.Diffs,
                reason = stored.Request.Reason,
                appliedTuplesCount = tuples.Count,
                wormSignature = Guid.NewGuid().ToString("N")
            })
        };

        await _auditRepo.RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);

        if (_eventBus != null)
        {
            await _eventBus.PublishAsync(new AccessPlanAppliedEvent(
                EventId: Guid.NewGuid().ToString("N"),
                PlanId: request.PlanId,
                DatasetId: stored.Request.DatasetId,
                ApproverSid: adminSid,
                AppliedGrantCount: tuples.Count,
                Timestamp: DateTimeOffset.UtcNow,
                TenantId: NormalizeTenant(tenantId) ?? stored.TenantId), ct).ConfigureAwait(false);
        }

        return new AdminApplyAccessResult(true, request.PlanId, tuples.Count, "Access applied successfully.");
    }

    // ---- Issue #107: row filter evaluation plans (SEC-RF107-14) ----

    private IRowFilterEvaluationAdminService RowFilterAdmin
        => _rowFilterEvaluationAdmin ?? throw new SecurityException("Row filter evaluation administration is not available.");

    // Fixed text: neither the id nor the existence of a rule of another tenant is revealed.
    private const string RowFilterNotFound = "The row filter or dataset was not found.";

    public async Task<AdminPlanRowFilterEvaluationResult> PlanRowFilterEvaluationAsync(
        AdminPlanRowFilterEvaluationRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);
        var admin = RowFilterAdmin;

        var hasRule = !string.IsNullOrWhiteSpace(request.RowFilterId);
        var hasDataset = !string.IsNullOrWhiteSpace(request.DatasetId);
        if (hasRule == hasDataset)
        {
            throw new ArgumentException("Exactly one of rowFilterId (rule level) or datasetId (object level) is required.");
        }

        if (!RowFilterEvaluationInput.TryCreate(request.Strategy, request.DurationSeconds, request.MaxKeys, out var settings, out var inputError))
        {
            throw new ArgumentException(inputError);
        }

        var tenant = NormalizeTenant(tenantId);
        Guid? ruleId = null;
        RowFilterEvaluationPreview preview;
        RowFilterEvaluationSettings after;
        if (hasRule)
        {
            if (!Guid.TryParseExact(request.RowFilterId, "D", out var parsedRule))
            {
                throw new ArgumentException("rowFilterId must be a GUID.");
            }

            if (tenant is null)
            {
                throw new SecurityException("A tenant context is required to change a rule level setting.");
            }

            ruleId = parsedRule;
            after = settings;
            preview = await admin.ValidateRuleAsync(parsedRule, new TenantId(tenant), settings, ct).ConfigureAwait(false)
                ?? throw new SecurityException(RowFilterNotFound);
        }
        else
        {
            var table = await DatasetIdResolver.ResolveActiveAsync(request.DatasetId, _tableRepo, ct).ConfigureAwait(false)
                ?? throw new SecurityException(RowFilterNotFound);
            after = settings;
            preview = await admin.ValidateObjectAsync(table, settings, ct).ConfigureAwait(false)
                ?? throw new SecurityException(RowFilterNotFound);
        }

        var now = _time.GetUtcNow();
        var planId = $"plan_{Guid.NewGuid():N}";
        var planTtl = TimeSpan.FromMinutes(_planOptions.PlanTtlMinutes);
        var expiresAt = now.Add(planTtl);
        var level = hasRule ? "rule" : "object";
        var tableText = preview.Table.ToString();
        var warnings = new List<string>();
        if (after.Strategy == RowFilterEvaluationStrategy.KeySetCache)
        {
            warnings.Add("KeySetCache evaluates the correlated filter from a cached key set: changes of the dependent data become visible after at most the configured duration.");
        }

        // The generic plan fields describe the change for the shared store and GetPlanAsync; they carry no grants.
        var diffs = new List<AccessPlanDiffItem>();
        AddDiff(diffs, level, "strategy", RowFilterEvaluationInput.Format(preview.Before.Strategy), RowFilterEvaluationInput.Format(after.Strategy));
        AddDiff(diffs, level, "durationSeconds", preview.Before.DurationSeconds?.ToString(CultureInfo.InvariantCulture), after.DurationSeconds?.ToString(CultureInfo.InvariantCulture));
        AddDiff(diffs, level, "maxKeys", preview.Before.MaxKeys?.ToString(CultureInfo.InvariantCulture), after.MaxKeys?.ToString(CultureInfo.InvariantCulture));
        var stored = new StoredAccessPlan(
            new AdminPlanAccessResult(planId, tableText, diffs, warnings, expiresAt),
            new AdminPlanAccessRequest(tableText, [], request.Reason ?? string.Empty),
            adminSid,
            TenantId: tenant,
            PlanKind: AccessPlanKind.RowFilterEvaluation,
            RowFilterEvaluation: new RowFilterEvaluationPlan(ruleId, tableText, preview.Before, after));

        if (!await _store.TrySavePlanAsync(stored, planTtl, _planOptions.MaxPendingPlansPerAdmin, ct).ConfigureAwait(false))
        {
            throw new SecurityException(
                $"Too many pending access plans for this administrator (limit {_planOptions.MaxPendingPlansPerAdmin}). Apply or wait for existing plans to expire.");
        }

        return new AdminPlanRowFilterEvaluationResult(
            planId, level, ruleId?.ToString("D"), tableText, ToDto(preview.Before), ToDto(after), warnings, expiresAt);
    }

    public async Task<AdminApplyRowFilterEvaluationResult> ApplyRowFilterEvaluationAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var admin = RowFilterAdmin;
        var callerTenant = NormalizeTenant(tenantId);

        // Validation against the CURRENT caps and the optimistic concurrency check happen before the token is consumed.
        var stored = await ConsumeConfirmedPlanAsync(request, adminSid, tenantId, AccessPlanKind.RowFilterEvaluation, async (plan, token) =>
        {
            var change = plan.RowFilterEvaluation ?? throw new SecurityException("The plan carries no row filter evaluation change.");
            RowFilterEvaluationPreview? current;
            if (change.RowFilterId is { } ruleId)
            {
                var tenant = NormalizeTenant(plan.TenantId ?? callerTenant)
                    ?? throw new SecurityException("A tenant context is required to change a rule level setting.");
                current = await admin.ValidateRuleAsync(ruleId, new TenantId(tenant), change.After, token).ConfigureAwait(false);
            }
            else
            {
                current = await admin.ValidateObjectAsync(ParseStoredTable(change.Table), change.After, token).ConfigureAwait(false);
            }

            if (current is null)
            {
                throw new SecurityException(RowFilterNotFound);
            }

            if (current.Before != change.Before)
            {
                throw new SecurityException("The stored settings changed since the plan was created; create a new plan.");
            }
        }, ct).ConfigureAwait(false);

        var applied = stored.RowFilterEvaluation!;
        var actor = new Sid(adminSid);
        RowFilterEvaluationChange? result;
        string level;
        if (applied.RowFilterId is { } id)
        {
            level = "rule";
            var tenant = new TenantId(NormalizeTenant(stored.TenantId ?? callerTenant)!);
            result = await admin.SetRuleAsync(id, tenant, applied.After, actor, ct).ConfigureAwait(false);
        }
        else
        {
            level = "object";
            var tenant = NormalizeTenant(stored.TenantId ?? callerTenant) is { } t ? new TenantId(t) : TenantId.LegacySingleTenant;
            var settings = applied.After == RowFilterEvaluationSettings.Inherit ? null : applied.After;
            result = await admin.SetObjectAsync(ParseStoredTable(applied.Table), settings, actor, tenant, ct).ConfigureAwait(false);
        }

        if (result is null)
        {
            throw new SecurityException(RowFilterNotFound);
        }

        // Residual admin-vs-admin race (the check before the write is not transactional): detectable afterwards, logged without values.
        if (result.Before != applied.Before)
        {
            _logger.LogWarning(
                "Row filter evaluation plan {PlanId} was applied over settings that changed between the check and the write (level {Level}).",
                request.PlanId, level);
        }

        return new AdminApplyRowFilterEvaluationResult(
            true, request.PlanId, level, applied.Table, result.NewEpoch, "Row filter evaluation settings applied successfully.");
    }

    private static TableIdentifier ParseStoredTable(string table)
        => TableIdentifier.TryParse(table, out var parsed) ? parsed : throw new SecurityException(RowFilterNotFound);

    private static RowFilterEvaluationSettingsDto ToDto(RowFilterEvaluationSettings s)
        => new(RowFilterEvaluationInput.Format(s.Strategy), s.DurationSeconds, s.MaxKeys);

    private static void AddDiff(List<AccessPlanDiffItem> diffs, string level, string setting, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            diffs.Add(new AccessPlanDiffItem(level, setting, before ?? "inherit", after ?? "inherit", false));
        }
    }

    /// <summary>
    /// The checks every apply shares (SR-L-06, ADR-03): the plan exists, has the expected kind, is unexpired, belongs to the confirming
    /// administrator and tenant, the confirmation token matches the confirmed token, is valid and unexpired, and is consumed once cluster-wide.
    /// </summary>
    private async Task<StoredAccessPlan> ConsumeConfirmedPlanAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        string? tenantId,
        AccessPlanKind expectedKind,
        Func<StoredAccessPlan, CancellationToken, Task>? beforeConsume,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
        {
            throw new SecurityException("Confirmation token is required to apply access changes (ADR-03).");
        }

        var stored = await _store.GetPlanAsync(request.PlanId, ct).ConfigureAwait(false);
        if (stored == null)
        {
            throw new SecurityException($"Plan '{request.PlanId}' was not found.");
        }

        // Issue #107 (SEC-RF107-14c): every apply path accepts only plans of its own kind, in both directions.
        if (stored.PlanKind != expectedKind)
        {
            throw new SecurityException("The plan is not of the kind this operation applies.");
        }

        if (stored.Result.ExpiresAt <= _time.GetUtcNow())
        {
            throw new SecurityException($"Plan '{request.PlanId}' has expired.");
        }

        // SR-L-06: apply is bound to the confirming administrator and the planning tenant.
        if (!string.Equals(stored.AdminSid, adminSid, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("Plan confirmation must be performed by the initiating administrator.");
        }

        var callerTenant = NormalizeTenant(tenantId);
        if (stored.TenantId != null && callerTenant != null &&
            !string.Equals(stored.TenantId, callerTenant, StringComparison.Ordinal))
        {
            throw new SecurityException("Plan belongs to a different tenant.");
        }

        if (string.IsNullOrWhiteSpace(stored.ConfirmedTokenHash) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(HashToken(request.ConfirmationToken)),
                Encoding.UTF8.GetBytes(stored.ConfirmedTokenHash)))
        {
            throw new SecurityException("Confirmation token does not match the confirmed plan token.");
        }

        if (!ValidateConfirmationToken(request.ConfirmationToken, request.PlanId, adminSid, out var tokenExpiry))
        {
            throw new SecurityException("Invalid or malformed confirmation token.");
        }

        if (tokenExpiry <= _time.GetUtcNow())
        {
            throw new SecurityException("Confirmation token has expired.");
        }

        // Kind specific checks run before the token is consumed, so a stale or invalid plan does not burn the confirmation.
        if (beforeConsume != null)
        {
            await beforeConsume(stored, ct).ConfigureAwait(false);
        }

        // Cluster-wide single use: the first caller wins; an unavailable store throws (no apply).
        var consumeTtl = stored.Result.ExpiresAt - _time.GetUtcNow();
        if (!await _store.TryConsumeTokenAsync(HashToken(request.ConfirmationToken), consumeTtl, ct).ConfigureAwait(false))
        {
            throw new SecurityException("Confirmation token has already been consumed (replay prevention).");
        }

        return stored;
    }

    public async Task<AdminRegisterDatasourceResult> RegisterDatasourceAsync(
        AdminRegisterDatasourceRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        var datasourceId = $"{request.Domain.ToLowerInvariant()}.{request.Name.ToLowerInvariant()}";
        string? secretRef = null;
        var vaultPrefix = $"vault://datasources/{request.Domain.ToLowerInvariant()}/";

        // SR-H-03: a client-supplied SecretRef must lie inside this domain's datasource namespace.
        DatasourceSecretRefPolicy.ValidateClientRef(request.Auth?.SecretRef, vaultPrefix);

        if (!string.IsNullOrWhiteSpace(request.Auth?.Secret))
        {
            // ARC-C-05: no plaintext copy in process memory; the secret goes to the secret provider or the request fails.
            if (_secretProvider == null)
            {
                throw new InvalidOperationException(
                    "Inline datasource secrets require a writable IKeyVaultSecretProvider; supply Auth.SecretRef instead.");
            }

            secretRef = $"{DatasourceSecretRefPolicy.DomainPrefix(request.Domain.ToLowerInvariant())}{request.Name.ToLowerInvariant()}:auth_secret";
            try
            {
                _secretProvider.SetSecret(secretRef, Encoding.UTF8.GetBytes(request.Auth.Secret));
            }
            catch (NotSupportedException ex)
            {
                throw new InvalidOperationException(
                    "The configured secret provider is read-only and cannot store inline datasource secrets; supply Auth.SecretRef instead.", ex);
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Auth?.SecretRef))
        {
            secretRef = request.Auth.SecretRef;
        }

        var auditEntry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = !string.IsNullOrWhiteSpace(tenantId) ? new TenantId(tenantId) : TenantId.LegacySingleTenant,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "ADMIN_REGISTER_DATASOURCE",
            ActorSid = new Sid(adminSid),
            TargetTable = datasourceId,
            Decision = "ALLOW",
            TraceId = datasourceId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                datasourceId,
                name = request.Name,
                domain = request.Domain,
                status = "inactive",
                isConfigured = true,
                secretRef,
                baseUrl = request.BaseUrl,
                dryRun = request.DryRun
            })
        };

        await _auditRepo.RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);

        return new AdminRegisterDatasourceResult(
            DatasourceId: datasourceId,
            Name: request.Name,
            Domain: request.Domain,
            Status: "inactive",
            IsConfigured: true,
            SecretRef: secretRef,
            Message: "Datasource registered in inactive status (SEC M-30).");
    }

    public async Task<AdminSetDatasetStateResult> SetDatasetStateAsync(
        AdminSetDatasetStateRequest request,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        var validStates = new[] { "active", "quarantined", "deprecated", "inactive" };
        var newState = request.State.ToLowerInvariant();
        if (!validStates.Contains(newState))
        {
            throw new ArgumentException($"Invalid dataset state '{request.State}'. Must be one of: {string.Join(", ", validStates)}", nameof(request));
        }

        var tableId = ParseTableIdentifier(request.DatasetId);
        var existing = await _tableRepo.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);

        string previousState = existing?.Table.IsActive == false ? "inactive" : "active";

        if (existing != null)
        {
            bool isActive = string.Equals(newState, "active", StringComparison.OrdinalIgnoreCase);
            var updated = existing with
            {
                Table = existing.Table with
                {
                    IsActive = isActive
                }
            };
            await _tableRepo.UpsertTableMetadataAsync(updated, ct).ConfigureAwait(false);
        }

        var auditEntry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = !string.IsNullOrWhiteSpace(tenantId) ? new TenantId(tenantId) : TenantId.LegacySingleTenant,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "ADMIN_SET_DATASET_STATE",
            ActorSid = new Sid(adminSid),
            TargetTable = request.DatasetId,
            Decision = "ALLOW",
            TraceId = request.DatasetId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                datasetId = request.DatasetId,
                previousState,
                newState,
                reason = request.Reason
            })
        };

        await _auditRepo.RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);

        if (_eventBus != null)
        {
            await _eventBus.PublishAsync(new DatasetStateChangedEvent(
                EventId: Guid.NewGuid().ToString("N"),
                DatasetId: request.DatasetId,
                OldState: string.Equals(previousState, "active", StringComparison.OrdinalIgnoreCase),
                NewState: string.Equals(newState, "active", StringComparison.OrdinalIgnoreCase),
                ChangedBySid: adminSid,
                Reason: request.Reason,
                Timestamp: DateTimeOffset.UtcNow,
                TenantId: NormalizeTenant(tenantId)), ct).ConfigureAwait(false);
        }

        return new AdminSetDatasetStateResult(request.DatasetId, previousState, newState, true);
    }

    public async Task<AdminResolvePrincipalResult> ResolvePrincipalAsync(
        AdminResolvePrincipalRequest request,
        string adminSid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query.Trim();

        // SR-L-06 Part D: demo SIDs and the hard-coded sample table owner lookup exist only in Development.
        var candidates = _allowDemoDirectory
            ? new List<PrincipalResolutionItem>(DefaultDirectory)
            : new List<PrincipalResolutionItem>();

        if (_allowDemoDirectory && _dataOwnershipRepo != null)
        {
            try
            {
                var owners = await _dataOwnershipRepo.GetDataOwnersForTableAsync(new TableIdentifier("sales", "public", "orders"), ct).ConfigureAwait(false);
                foreach (var owner in owners)
                {
                    candidates.Add(new PrincipalResolutionItem(
                        owner.AdSid.Value,
                        owner.DisplayName,
                        "User",
                        false,
                        []));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not fetch data owners for principal resolution.");
            }
        }

        var matches = candidates
            .Where(c => c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.Sid.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(c => c with
            {
                ExactMatch = string.Equals(c.DisplayName, query, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(c.Sid, query, StringComparison.OrdinalIgnoreCase)
            })
            .ToList();

        return new AdminResolvePrincipalResult(matches);
    }

    public async Task<AdminPlanAccessResult?> GetPlanAsync(string planId, CancellationToken ct = default)
    {
        var stored = await _store.GetPlanAsync(planId, ct).ConfigureAwait(false);
        return stored?.Result;
    }

    internal void SweepExpired(bool force) => (_store as InMemoryAccessPlanStore)?.Sweep(force);

    internal int PlanCount => (_store as InMemoryAccessPlanStore)?.PlanCount ?? 0;

    internal int ConsumedTokenCount => (_store as InMemoryAccessPlanStore)?.ConsumedTokenCount ?? 0;

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Derives the confirmation-token HMAC key (HKDF-SHA256) from the configured secret. Without a configured secret a
    /// per-process random key is used, which is only acceptable when the state is not shared across replicas.
    /// </summary>
    private static byte[] ResolveSigningKey(IKeyVaultSecretProvider? provider, string? signingKeyRef, bool clusterWide)
    {
        if (string.IsNullOrWhiteSpace(signingKeyRef))
        {
            if (clusterWide)
            {
                throw new InvalidOperationException(
                    $"{AccessPlanOptions.SectionName}:SigningKeyRef is required when the cluster state is shared across replicas.");
            }

            return RandomNumberGenerator.GetBytes(32);
        }

        if (provider == null)
        {
            throw new InvalidOperationException(
                $"{AccessPlanOptions.SectionName}:SigningKeyRef is configured but no IKeyVaultSecretProvider is registered.");
        }

        var ikm = provider.GetSecretBytes(signingKeyRef);
        if (ikm == null || ikm.Length == 0)
        {
            throw new InvalidOperationException($"The secret referenced by {AccessPlanOptions.SectionName}:SigningKeyRef is empty.");
        }

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: null, info: Encoding.UTF8.GetBytes(SigningKeyInfo));
    }

    private static string B64(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private string Sign(string signedPart)
        => Convert.ToHexString(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(signedPart)));

    // Token layout: v1.{b64url(planId)}.{b64url(adminSid)}.{unix}.{hmac-hex}. Base64url never contains '.',
    // so the encoding is unambiguous even when the SID contains ':' or other separators.
    private string GenerateConfirmationToken(string planId, string adminSid, DateTimeOffset expiresAt)
    {
        var signedPart = $"v1.{B64(planId)}.{B64(adminSid)}.{expiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return $"{signedPart}.{Sign(signedPart)}";
    }

    private bool ValidateConfirmationToken(string token, string expectedPlanId, string expectedAdminSid, out DateTimeOffset expiresAt)
    {
        expiresAt = DateTimeOffset.MinValue;
        var parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != "v1") return false;

        var signedPart = string.Join('.', parts[0], parts[1], parts[2], parts[3]);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(parts[4]),
            Encoding.UTF8.GetBytes(Sign(signedPart))))
        {
            return false;
        }

        if (!long.TryParse(parts[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var unix)) return false;

        string planId, adminSid;
        try
        {
            planId = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
            adminSid = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[2]));
        }
        catch (FormatException)
        {
            return false;
        }

        if (!string.Equals(planId, expectedPlanId, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(adminSid, expectedAdminSid, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return true;
    }

    private static TableIdentifier ParseTableIdentifier(string datasetId)
    {
        var parts = datasetId.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            >= 3 => new TableIdentifier(parts[0], parts[1], parts[2]),
            2 => new TableIdentifier(parts[0], "public", parts[1]),
            1 => new TableIdentifier("default", "public", parts[0]),
            _ => new TableIdentifier("default", "public", "unknown")
        };
    }

    private sealed class NullTableMetadataRepository : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>(Array.Empty<TableMetadata>());
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class NullRebacStore : IRebacStore
    {
        public ValueTask AddTupleAsync(RebacTuple tuple, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask AddTuplesAsync(IEnumerable<RebacTuple> tuples, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<bool> DeleteTupleAsync(RebacTuple tuple, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<IReadOnlyList<RebacTuple>> GetTuplesAsync(string tenantId, string? user = null, string? relation = null, string? obj = null, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<RebacTuple>>(Array.Empty<RebacTuple>());
        public ValueTask ClearTenantTuplesAsync(string tenantId, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
