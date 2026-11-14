namespace Autheris.Application.Governance.Services;

using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Application.Events;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
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
    private readonly AccessPlanTokenSigner _tokenSigner;
    private readonly bool _isDev;
    private readonly IAccessPlanStore _store;

    private readonly ITableMetadataRepository _tableRepo;
    private readonly IConsentRepository? _consentRepository;
    private readonly IAuditWriter _audit;
    private readonly ITotpVerificationService? _totpService;
    private readonly ITotpSecretStore? _totpSecretStore;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IPolicyEpochRepository? _policyEpochRepo;
    private readonly IDataOwnershipRepository? _dataOwnershipRepo;
    private readonly ILogger<AccessPlanningService> _logger;
    private readonly IGatewayEventBus? _eventBus;
    private readonly AccessPlanOptions _planOptions;
    private readonly TimeProvider _time;
    private readonly bool _allowDemoDirectory;
    private readonly IGrantPolicy _grantPolicy;
    private readonly IRowFilterEvaluationAdminService? _rowFilterEvaluationAdmin;

    private IReadOnlyList<DateTimeOffset?> EnsureGrantsAllowed(
        AdminPlanAccessRequest request,
        TableMetadata? meta,
        IReadOnlyList<AccessPlanDiffItem> diffs,
        DateTimeOffset now,
        HashSet<string> actorIdentifiers,
        bool rejectFourEyes)
    {
        var maxValidTo = new List<DateTimeOffset?>(request.Grants.Count);
        foreach (var grant in request.Grants)
        {
            // an administrator never grants access to themselves (any identifier form, group or role they hold).
            if (ActorIdentifiers.IsActorSubject(actorIdentifiers, grant.Principal) ||
                (AccessGrantRecords.TryParseGrantee(grant.Principal, out _, out var granteeId) && ActorIdentifiers.IsActorSubject(actorIdentifiers, granteeId)))
            {
                throw new SecurityException("Self-grant is not allowed: an administrator cannot grant access to themselves.");
            }

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
            maxValidTo.Add(decision.MaxValidTo);

            // GOV-04: the single-actor path never grants sensitive access; those grants go through the consent workflow
            // (two independent approvers). An unknown table is not classified here (no table to gate yet).
            if (rejectFourEyes && decision.RequiresFourEyes && (meta is not null || clearPii))
            {
                throw new GrantPolicyViolationException(GrantDecision.Deny(
                    GrantErrorCodes.FourEyesRequired,
                    "This grant requires four-eyes approval; use the consent request workflow instead of the single-administrator access plan.",
                    decision.Class,
                    decision.MaxValidTo));
            }
        }

        return maxValidTo;
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
        IAuditWriter auditWriter,
        ITableMetadataRepository? tableMetadataRepository = null,
        IConsentRepository? consentRepository = null,
        ITotpVerificationService? totpVerificationService = null,
        ITotpSecretStore? totpSecretStore = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IPolicyEpochRepository? policyEpochRepository = null,
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
        _audit = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _tableRepo = tableMetadataRepository ?? new NullTableMetadataRepository();
        _consentRepository = consentRepository;
        _totpService = totpVerificationService;
        _totpSecretStore = totpSecretStore;
        _secretProvider = secretProvider;
        _policyEpochRepo = policyEpochRepository;
        _dataOwnershipRepo = dataOwnershipRepository;
        _logger = logger ?? NullLogger<AccessPlanningService>.Instance;
        _eventBus = eventBus;
        _planOptions = planOptions?.Value ?? new AccessPlanOptions();
        _time = timeProvider ?? TimeProvider.System;
        _store = accessPlanStore
            ?? new InMemoryAccessPlanStore(_time, TimeSpan.FromSeconds(_planOptions.SweepIntervalSeconds));
        _tokenSigner = new AccessPlanTokenSigner(_secretProvider, _planOptions.SigningKeyRef, clusterTopology?.IsClusterWide == true);
        _isDev = hostEnvironment?.IsDevelopment() == true;
        // SR-L-06 Part D: the demo directory is only exposed when the host is explicitly a Development host.
        _allowDemoDirectory = _isDev;
    }

    public async Task<AdminPlanAccessResult> PlanAccessAsync(
        AdminPlanAccessRequest request,
        string adminSid,
        string? tenantId = null,
        ClaimsPrincipal? caller = null,
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

        // every individual (user) principal needs a mandatory, class-bounded validUntil already at planning time.
        EnsureGrantsAllowed(request, meta, diffs, now, ActorIdentifiers.Collect(caller, new Sid(adminSid), null), rejectFourEyes: false);

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
        var token = _tokenSigner.GenerateConfirmationToken(planId, adminSid, tokenExpiresAt);

        if (!await _store.SetConfirmedTokenAsync(planId, AccessPlanTokenSigner.HashToken(token), ct).ConfigureAwait(false))
        {
            throw new SecurityException($"Plan '{planId}' does not exist or has expired.");
        }

        return new AdminConfirmPlanResult(token, tokenExpiresAt);
    }

    public async Task<AdminApplyAccessResult> ApplyAccessAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        TenantId tenantId,
        ClaimsPrincipal? caller = null,
        CancellationToken ct = default)
    {
        // GOV-04: the tuples are scoped to the caller tenant only; a missing tenant is never replaced by a domain or default.
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            throw new SecurityException("A tenant is required to apply an access plan.");
        }

        var stored = await ConsumeConfirmedPlanAsync(request, adminSid, tenantId.Value, AccessPlanKind.Access, (plan, token) =>
        {
            foreach (var grant in plan.Request.Grants)
            {
                if (!AccessGrantRecords.TryParseGrantee(grant.Principal, out _, out _))
                {
                    throw new SecurityException($"Invalid principal '{grant.Principal}' in access plan.");
                }
            }
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        var consentRepository = _consentRepository
            ?? throw new SecurityException("Access plans are applied as grant records; no grant store is available.");
        var tableId = ParseTableIdentifier(stored.Request.DatasetId);

        // re-evaluated at apply time (the plan may have been stored with a validUntil that has meanwhile lapsed).
        var applyMeta = await _tableRepo.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var maxValidTo = EnsureGrantsAllowed(stored.Request, applyMeta, stored.Result.Diffs, now, ActorIdentifiers.Collect(caller, new Sid(adminSid), null), rejectFourEyes: true);
        if (applyMeta is null)
        {
            throw new SecurityException($"Table '{stored.Request.DatasetId}' does not exist; a grant record needs a registered table.");
        }

        // the caller's tenant strictly scopes the grant records and audit; no Domain or default fallback.
        var effectiveTenantId = tenantId.Value;
        var consents = AccessGrantRecords.Build(stored.Request.Grants, applyMeta, tableId, new TenantId(effectiveTenantId), adminSid, now, maxValidTo);

        // Write-ahead: the required record exists before the grant records are written.
        await RecordAdminAuditAsync(
            AuditEventTypes.AdminApplyAccess, new TenantId(effectiveTenantId), adminSid, stored.Request.DatasetId,
            new
            {
                channel = "mcp",
                planId = request.PlanId,
                diffs = stored.Result.Diffs,
                reason = stored.Request.Reason,
                appliedGrantCount = consents.Count,
                wormSignature = Guid.NewGuid().ToString("N")
            }, ct).ConfigureAwait(false);

        foreach (var consent in consents)
        {
            await consentRepository.CreateConsentAsync(consent, ct).ConfigureAwait(false);
        }

        if (_policyEpochRepo != null)
        {
            await _policyEpochRepo.IncrementTableEpochAsync(tableId, ct).ConfigureAwait(false);
        }

        if (_eventBus != null)
        {
            await _eventBus.PublishAsync(new AccessPlanAppliedEvent(
                EventId: Guid.NewGuid().ToString("N"),
                PlanId: request.PlanId,
                DatasetId: stored.Request.DatasetId,
                ApproverSid: adminSid,
                AppliedGrantCount: consents.Count,
                Timestamp: DateTimeOffset.UtcNow,
                TenantId: NormalizeTenant(tenantId) ?? stored.TenantId), ct).ConfigureAwait(false);
        }

        return new AdminApplyAccessResult(true, request.PlanId, consents.Count, "Access applied successfully as grant records.");
    }

    // ---- Issue #107: row filter evaluation plans ----

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
        TenantId tenantId,
        CancellationToken ct = default)
    {
        var admin = RowFilterAdmin;
        var callerTenant = tenantId.Value;

        // Validation against the CURRENT caps and the optimistic concurrency check happen before the token is consumed.
        var stored = await ConsumeConfirmedPlanAsync(request, adminSid, callerTenant, AccessPlanKind.RowFilterEvaluation, async (plan, token) =>
        {
            var change = plan.RowFilterEvaluation ?? throw new SecurityException("The plan carries no row filter evaluation change.");
            RowFilterEvaluationPreview? current;
            if (change.RowFilterId is { } ruleId)
            {
                current = await admin.ValidateRuleAsync(ruleId, tenantId, change.After, token).ConfigureAwait(false);
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
            result = await admin.SetRuleAsync(id, tenantId, applied.After, actor, ct).ConfigureAwait(false);
        }
        else
        {
            level = "object";
            var settings = applied.After == RowFilterEvaluationSettings.Inherit ? null : applied.After;
            result = await admin.SetObjectAsync(ParseStoredTable(applied.Table), settings, actor, tenantId, ct).ConfigureAwait(false);
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

        // Issue #107 (14c): every apply path accepts only plans of its own kind, in both directions.
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
        if (stored.TenantId == null || callerTenant == null ||
            !string.Equals(stored.TenantId, callerTenant, StringComparison.Ordinal))
        {
            throw new SecurityException("Plan belongs to a different tenant.");
        }

        if (string.IsNullOrWhiteSpace(stored.ConfirmedTokenHash) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(AccessPlanTokenSigner.HashToken(request.ConfirmationToken)),
                Encoding.UTF8.GetBytes(stored.ConfirmedTokenHash)))
        {
            throw new SecurityException("Confirmation token does not match the confirmed plan token.");
        }

        if (!_tokenSigner.ValidateConfirmationToken(request.ConfirmationToken, request.PlanId, adminSid, out var tokenExpiry))
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
        if (!await _store.TryConsumeTokenAsync(AccessPlanTokenSigner.HashToken(request.ConfirmationToken), consumeTtl, ct).ConfigureAwait(false))
        {
            throw new SecurityException("Confirmation token has already been consumed (replay prevention).");
        }

        return stored;
    }

    // ---- Mutating MCP administrator operations (two-phase confirmation, ADR-03) ----

    public async Task<AdminPlanAccessResult> PlanAdminOperationAsync(
        string operation,
        string target,
        string argumentsDigest,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        var now = _time.GetUtcNow();
        var planId = $"plan_{Guid.NewGuid():N}";
        var planTtl = TimeSpan.FromMinutes(_planOptions.PlanTtlMinutes);
        var result = new AdminPlanAccessResult(
            planId,
            target,
            [],
            [$"Confirms the administrator operation '{operation}' on '{target}'. Confirm with TOTP, then repeat the call with the same arguments, the planId and the confirmation token."],
            now.Add(planTtl));

        // The plan carries no grants: ApplyAccessAsync rejects it by its kind, and the operation runs only through ConsumeAdminOperationAsync.
        var stored = new StoredAccessPlan(
            result,
            new AdminPlanAccessRequest(target, [], string.Empty),
            adminSid,
            TenantId: NormalizeTenant(tenantId),
            PlanKind: AccessPlanKind.AdminOperation,
            AdminOperation: new AdminOperationPlan(operation, argumentsDigest));

        if (!await _store.TrySavePlanAsync(stored, planTtl, _planOptions.MaxPendingPlansPerAdmin, ct).ConfigureAwait(false))
        {
            throw new SecurityException(
                $"Too many pending access plans for this administrator (limit {_planOptions.MaxPendingPlansPerAdmin}). Apply or wait for existing plans to expire.");
        }

        return result;
    }

    public async Task ConsumeAdminOperationAsync(
        AdminApplyAccessRequest request,
        string operation,
        string argumentsDigest,
        string adminSid,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsDigest);

        // The binding is checked before the token is consumed, so a mismatched call does not burn the confirmation.
        await ConsumeConfirmedPlanAsync(request, adminSid, tenantId, AccessPlanKind.AdminOperation, (plan, _) =>
        {
            var bound = plan.AdminOperation;
            if (bound is null ||
                !string.Equals(bound.Operation, operation, StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(bound.ArgumentsDigest ?? string.Empty), Encoding.UTF8.GetBytes(argumentsDigest)))
            {
                throw new SecurityException("The confirmed plan does not match this operation and its arguments.");
            }

            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    private Task RecordAdminAuditAsync(string eventType, TenantId tenant, string adminSid, string target, object details, CancellationToken ct) =>
        _audit.RecordRequiredAsync(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = eventType,
            ActorSid = new Sid(adminSid),
            TargetTable = target,
            Decision = "ALLOW",
            TraceId = target,
            DetailsJson = JsonSerializer.Serialize(details)
        }, ct);

    public async Task<AdminRegisterDatasourceResult> RegisterDatasourceAsync(
        AdminRegisterDatasourceRequest request,
        string adminSid,
        string? tenantId = null,
        bool isClusterAdmin = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        DatasourceSecretRefPolicy.RequireOwnDomain(request.Domain, NormalizeTenant(tenantId), isClusterAdmin);
        var datasourceId = $"{request.Domain.ToLowerInvariant()}.{request.Name.ToLowerInvariant()}";
        string? secretRef = null;
        IKeyVaultSecretProvider? inlineStore = null;
        var domainPrefix = DatasourceSecretRefPolicy.DomainPrefix(request.Domain.ToLowerInvariant());

        // SR-H-03: a client-supplied SecretRef must lie inside this domain's datasource namespace.
        DatasourceSecretRefPolicy.ValidateClientRef(request.Auth?.SecretRef, domainPrefix);

        if (!string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new ArgumentException($"BaseUrl '{request.BaseUrl}' must be a valid absolute URI.", nameof(request));
            }

            EgressUrlPolicy.ValidateStatic(baseUri, isDev: _isDev);
        }

        if (!string.IsNullOrWhiteSpace(request.Auth?.Secret))
        {
            // ARC-C-05: no plaintext copy in process memory; the secret goes to the secret provider or the request fails.
            if (_secretProvider == null)
            {
                throw new InvalidOperationException(
                    "Inline datasource secrets require a writable IKeyVaultSecretProvider; supply Auth.SecretRef instead.");
            }

            secretRef = DatasourceSecretRefPolicy.NewAuthSecretRef(_secretProvider, request.Domain, NormalizeTenant(tenantId), request.Name);
            inlineStore = _secretProvider;
        }
        else if (!string.IsNullOrWhiteSpace(request.Auth?.SecretRef))
        {
            secretRef = request.Auth.SecretRef;
        }

        var auditedBaseUrl = StripUserInfo(request.BaseUrl);

        // Write-ahead: the required record exists before the secret is stored.
        await RecordAdminAuditAsync(
            AuditEventTypes.AdminRegisterDatasource, TenantId.TryParse(tenantId, out var auditTenant) ? auditTenant : TenantId.Platform, adminSid, datasourceId,
            new
            {
                datasourceId,
                name = request.Name,
                domain = request.Domain,
                status = "inactive",
                isConfigured = true,
                secretRef,
                baseUrl = auditedBaseUrl,
                dryRun = request.DryRun
            }, ct).ConfigureAwait(false);

        if (inlineStore != null)
        {
            try
            {
                inlineStore.SetSecret(secretRef!, Encoding.UTF8.GetBytes(request.Auth!.Secret!));
            }
            catch (NotSupportedException ex)
            {
                throw new InvalidOperationException(
                    "The configured secret provider is read-only and cannot store inline datasource secrets; supply Auth.SecretRef instead.", ex);
            }
        }

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
        TenantId? restrictToTenant = null,
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

        // GHSA-9229: a missing table, or one owned by another tenant than the confined caller, is "not found" (no write, no audit).
        if (existing == null ||
            (restrictToTenant is { } owner && !string.Equals(existing.OwnerTenantId, owner.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return new AdminSetDatasetStateResult(request.DatasetId, "unknown", newState, false);
        }

        string previousState = existing.Table.IsActive == false ? "inactive" : "active";

        var isActive = string.Equals(newState, "active", StringComparison.OrdinalIgnoreCase);
        // Write-ahead: the required record exists before the state change.
        await RecordAdminAuditAsync(
            AuditEventTypes.AdminSetDatasetState, TenantId.TryParse(tenantId, out var auditTenant) ? auditTenant : TenantId.Platform, adminSid, request.DatasetId,
            new
            {
                datasetId = request.DatasetId,
                previousState,
                newState,
                reason = request.Reason
            }, ct).ConfigureAwait(false);

        await _tableRepo.UpsertTableMetadataAsync(existing with { Table = existing.Table with { IsActive = isActive } }, ct).ConfigureAwait(false);

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
        return stored is null ? null : stored.Result with { TenantId = stored.TenantId, AdminSid = stored.AdminSid };
    }

    internal void SweepExpired(bool force) => (_store as InMemoryAccessPlanStore)?.Sweep(force);

    internal int PlanCount => (_store as InMemoryAccessPlanStore)?.PlanCount ?? 0;

    internal int ConsumedTokenCount => (_store as InMemoryAccessPlanStore)?.ConsumedTokenCount ?? 0;

    private static string? StripUserInfo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
        }

        return url;
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
}
