namespace Autheris.Application.Governance.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
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
    private static readonly byte[] HmacKey = RandomNumberGenerator.GetBytes(32);

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

    private readonly ConcurrentDictionary<string, StoredPlan> _plans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _consumedTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _inMemoryVault = new(StringComparer.OrdinalIgnoreCase);

    private sealed record StoredPlan(
        AdminPlanAccessResult Result,
        AdminPlanAccessRequest Request,
        string AdminSid,
        string? ConfirmedToken = null);

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
        ILogger<AccessPlanningService>? logger = null)
    {
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
    }

    public async Task<AdminPlanAccessResult> PlanAccessAsync(
        AdminPlanAccessRequest request,
        string adminSid,
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

        var planId = $"plan_{Guid.NewGuid():N}";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var result = new AdminPlanAccessResult(planId, request.DatasetId, diffs, warnings, expiresAt);

        _plans[planId] = new StoredPlan(result, request, adminSid);
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

        if (!_plans.TryGetValue(planId, out var stored) || stored.Result.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new SecurityException($"Plan '{planId}' does not exist or has expired.");
        }

        if (_totpService != null && _totpSecretStore != null)
        {
            var secret = await _totpSecretStore.GetSecretAsync(adminSid, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(secret) || !_totpService.VerifyTotp(secret, totpCode))
            {
                throw new SecurityException("Invalid TOTP verification code.");
            }
        }

        var tokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var token = GenerateConfirmationToken(planId, adminSid, tokenExpiresAt);

        _plans[planId] = stored with { ConfirmedToken = token };
        return new AdminConfirmPlanResult(token, tokenExpiresAt);
    }

    public async Task<AdminApplyAccessResult> ApplyAccessAsync(
        AdminApplyAccessRequest request,
        string adminSid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
        {
            throw new SecurityException("Confirmation token is required to apply access changes (ADR-03).");
        }

        if (!_plans.TryGetValue(request.PlanId, out var stored))
        {
            throw new SecurityException($"Plan '{request.PlanId}' was not found.");
        }

        if (stored.Result.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new SecurityException($"Plan '{request.PlanId}' has expired.");
        }

        if (!ValidateConfirmationToken(request.ConfirmationToken, request.PlanId, out var tokenExpiry))
        {
            throw new SecurityException("Invalid or malformed confirmation token.");
        }

        if (tokenExpiry <= DateTimeOffset.UtcNow)
        {
            throw new SecurityException("Confirmation token has expired.");
        }

        if (!_consumedTokens.TryAdd(request.ConfirmationToken, true))
        {
            throw new SecurityException("Confirmation token has already been consumed (replay prevention).");
        }

        var tenantId = "default";
        var tableId = ParseTableIdentifier(stored.Request.DatasetId);
        var tuples = new List<RebacTuple>();

        foreach (var grant in stored.Request.Grants)
        {
            tuples.Add(new RebacTuple(tenantId, $"user:{grant.Principal}", "can_query", $"table:{stored.Request.DatasetId}"));

            foreach (var (column, access) in grant.Columns)
            {
                tuples.Add(new RebacTuple(tenantId, $"user:{grant.Principal}", $"access:{access}", $"column:{stored.Request.DatasetId}.{column}"));
            }
        }

        await _rebacStore.AddTuplesAsync(tuples, ct).ConfigureAwait(false);

        if (_policyEpochRepo != null)
        {
            await _policyEpochRepo.IncrementTableEpochAsync(tableId, ct).ConfigureAwait(false);
        }

        if (_policyEnforcement != null)
        {
            await _policyEnforcement.ReloadPoliciesAsync(new TenantId(tenantId), ct).ConfigureAwait(false);
        }

        var auditEntry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId(tenantId),
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
        return new AdminApplyAccessResult(true, request.PlanId, tuples.Count, "Access applied successfully.");
    }

    public async Task<AdminRegisterDatasourceResult> RegisterDatasourceAsync(
        AdminRegisterDatasourceRequest request,
        string adminSid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminSid);

        var datasourceId = $"{request.Domain.ToLowerInvariant()}.{request.Name.ToLowerInvariant()}";
        string? secretRef = null;

        if (!string.IsNullOrWhiteSpace(request.Auth?.Secret))
        {
            secretRef = $"vault://datasources/{request.Domain.ToLowerInvariant()}/{request.Name.ToLowerInvariant()}/auth_secret";
            _inMemoryVault[secretRef] = Encoding.UTF8.GetBytes(request.Auth.Secret);
        }
        else if (!string.IsNullOrWhiteSpace(request.Auth?.SecretRef))
        {
            secretRef = request.Auth.SecretRef;
        }

        var auditEntry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId.LegacySingleTenant,
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
                Table = new Table
                {
                    Id = existing.Table.Id,
                    SchemaName = existing.Table.SchemaName,
                    TableName = existing.Table.TableName,
                    Sensitivity = existing.Table.Sensitivity,
                    RequiresFourEyes = existing.Table.RequiresFourEyes,
                    IsActive = isActive,
                    Description = existing.Table.Description,
                    DisplayName = existing.Table.DisplayName
                }
            };
            await _tableRepo.UpsertTableMetadataAsync(updated, ct).ConfigureAwait(false);
        }

        var auditEntry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId.LegacySingleTenant,
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
        return new AdminSetDatasetStateResult(request.DatasetId, previousState, newState, true);
    }

    public async Task<AdminResolvePrincipalResult> ResolvePrincipalAsync(
        AdminResolvePrincipalRequest request,
        string adminSid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query.Trim();

        var candidates = new List<PrincipalResolutionItem>(DefaultDirectory);

        if (_dataOwnershipRepo != null)
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

    public AdminPlanAccessResult? GetPlan(string planId) =>
        _plans.TryGetValue(planId, out var stored) ? stored.Result : null;

    private static string GenerateConfirmationToken(string planId, string adminSid, DateTimeOffset expiresAt)
    {
        var unix = expiresAt.ToUnixTimeSeconds();
        var payload = $"{planId}:{adminSid}:{unix}";
        var hash = HMACSHA256.HashData(HmacKey, Encoding.UTF8.GetBytes(payload));
        return $"{payload}:{Convert.ToHexString(hash)}";
    }

    private static bool ValidateConfirmationToken(string token, string expectedPlanId, out DateTimeOffset expiresAt)
    {
        expiresAt = DateTimeOffset.MinValue;
        var lastColon = token.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == token.Length - 1) return false;

        var payload = token[..lastColon];
        var signatureHex = token[(lastColon + 1)..];

        var secondLastColon = payload.LastIndexOf(':');
        if (secondLastColon <= 0 || secondLastColon == payload.Length - 1) return false;

        var unixStr = payload[(secondLastColon + 1)..];
        if (!long.TryParse(unixStr, out var unix)) return false;

        var firstColon = payload.IndexOf(':');
        if (firstColon <= 0) return false;

        var planId = payload[..firstColon];
        if (!string.Equals(planId, expectedPlanId, StringComparison.OrdinalIgnoreCase)) return false;

        var expectedHash = Convert.ToHexString(HMACSHA256.HashData(HmacKey, Encoding.UTF8.GetBytes(payload)));

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signatureHex),
            Encoding.UTF8.GetBytes(expectedHash)))
        {
            return false;
        }

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
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
