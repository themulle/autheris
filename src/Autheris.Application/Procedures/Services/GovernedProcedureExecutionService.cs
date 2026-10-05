namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02 (phase 1, read): Governed execution of a declared stored procedure.
/// Row isolation is enforced by the database (SECURITY POLICY + SESSION_CONTEXT, see ADR-018); the gateway enforces
/// consents on all referenced tables, column deny/mask on the result set (unknown columns are removed, fail-closed)
/// and writes a synchronous audit entry before any data is returned.
/// </summary>
public sealed class GovernedProcedureExecutionService : IProcedureExecutionService
{
    private const string DeniedMessage = "Access to the procedure is denied.";

    private readonly IProcedureRegistry _registry;
    private readonly IProcedureInvoker _invoker;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly IColumnMaskingProvider? _masking;
    private readonly IPolicyEnforcementService? _policyEnforcement;
    private readonly IAuditLogRepository? _audit;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly ILogger<GovernedProcedureExecutionService>? _logger;

    public GovernedProcedureExecutionService(
        IProcedureRegistry registry,
        IProcedureInvoker invoker,
        IOptions<GatewayOptions> options,
        ITableMetadataRepository? tableRepository = null,
        IConsentRepository? consentRepository = null,
        IConsentResolutionService? consentResolution = null,
        IColumnMaskingProvider? masking = null,
        IPolicyEnforcementService? policyEnforcement = null,
        IAuditLogRepository? audit = null,
        IClientIpResolver? clientIpResolver = null,
        ILogger<GovernedProcedureExecutionService>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tableRepository = tableRepository;
        _consentRepository = consentRepository;
        _consentResolution = consentResolution;
        _masking = masking;
        _policyEnforcement = policyEnforcement;
        _audit = audit;
        _clientIpResolver = clientIpResolver;
        _logger = logger;
    }

    public async Task<GovernedProcedureResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object?>? rawInputs,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(user);

        if (!_registry.TryGet(name, out var registered) || registered == null)
        {
            throw new KeyNotFoundException($"Procedure endpoint '{name}' is not registered.");
        }

        var definition = registered.Definition;
        if (registered.State != ProcedureState.Active || registered.Validation == null)
        {
            throw new ProcedureUnavailableException($"Procedure endpoint '{name}' is currently unavailable.");
        }

        var sw = Stopwatch.StartNew();
        bool consentBypassed = _options.Value.IsConsentBypassed;

        // 1. Identity and role gate
        var userSidNullable = user.GetUserSid();
        if (userSidNullable == null && !consentBypassed)
        {
            throw new SecurityException("An authenticated user identity is required.");
        }

        var userSid = userSidNullable ?? new Sid("anonymous");

        if (definition.RequiredRoles.Count > 0 && !HasAnyRole(user, definition.RequiredRoles))
        {
            await AuditAsync("PROCEDURE_DENIED", "DENY", definition, tenantId, userSid, new { reason = "role" }, ct).ConfigureAwait(false);
            throw new SecurityException(DeniedMessage);
        }

        // 2. Input validation (unknown and context-bound parameters are rejected, never ignored)
        var values = ValidateInputs(definition, rawInputs);

        // 3. Consent (+ ABAC restriction) on every table the procedure reads
        var decisions = new Dictionary<string, (TableAccessDecision Decision, TableMetadata Meta)>(StringComparer.OrdinalIgnoreCase);
        var tableKeys = registered.Validation.ReferencedTables.ToList();
        if (definition.ResultTable != null && !tableKeys.Contains(definition.ResultTable, StringComparer.OrdinalIgnoreCase))
        {
            tableKeys.Add(definition.ResultTable);
        }

        foreach (var key in tableKeys)
        {
            var evaluated = await EvaluateTableAsync(key, user, userSid, tenantId, consentBypassed, ct).ConfigureAwait(false);
            if (evaluated == null)
            {
                await AuditAsync("PROCEDURE_DENIED", "DENY", definition, tenantId, userSid, new { reason = "consent" }, ct).ConfigureAwait(false);
                throw new SecurityException(DeniedMessage);
            }

            decisions[key] = evaluated.Value;
        }

        // 4. Call (security context goes into the read-only SESSION_CONTEXT)
        var context = new ProcedureSecurityContext(
            tenantId.Value,
            userSid.Value,
            user.FindFirst("purpose")?.Value ?? user.FindFirst("purpose_id")?.Value);

        RawProcedureResult raw;
        try
        {
            raw = await _invoker.ExecuteReadAsync(definition, values, context, ct).ConfigureAwait(false);
        }
        catch (ProcedureBusinessException)
        {
            await AuditAsync("PROCEDURE_REJECTED", "DENY", definition, tenantId, userSid, new { reason = "business-error" }, ct).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Stored procedure endpoint '{Endpoint}' failed.", name);
            await AuditAsync("PROCEDURE_FAILED", "DENY", definition, tenantId, userSid, new { reason = ex.GetType().Name }, ct).ConfigureAwait(false);
            throw;
        }

        // 5. Result-set governance (deny / mask / drop unknown columns)
        var governed = GovernResult(definition, raw, decisions);

        // 6. Synchronous audit BEFORE data leaves the gateway (fail-closed: an audit failure aborts the response)
        await AuditAsync(
            "PROCEDURE_EXECUTE",
            "ALLOW",
            definition,
            tenantId,
            userSid,
            new
            {
                mode = definition.Mode.ToString(),
                rowCount = governed.Rows.Count,
                truncated = raw.Truncated,
                returnedColumns = governed.Columns.Count,
                droppedColumns = raw.Columns.Count - governed.Columns.Count,
                parameterHashes = HashParameters(values)
            },
            ct).ConfigureAwait(false);

        return new GovernedProcedureResult(governed.Columns, governed.Rows, governed.Rows.Count, raw.Truncated, sw.ElapsedMilliseconds);
    }

    private static bool HasAnyRole(ClaimsPrincipal user, IReadOnlyList<string> roles)
    {
        var claimRoles = user.GetUserRoles();
        return roles.Any(r => !string.IsNullOrWhiteSpace(r) && (user.IsInRole(r) || claimRoles.Contains(r)));
    }

    private Dictionary<string, object?> ValidateInputs(ProcedureDefinition definition, IReadOnlyDictionary<string, object?>? rawInputs)
    {
        var inputs = rawInputs ?? new Dictionary<string, object?>();
        var declared = definition.Parameters.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var key in inputs.Keys)
        {
            if (!declared.ContainsKey(key))
            {
                throw new ArgumentException($"Unknown parameter '{key}'.", key);
            }
        }

        int maxString = _options.Value.SqlEndpoints.Procedures.MaxStringParameterLength;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in definition.Parameters)
        {
            if (!inputs.TryGetValue(p.Name, out var provided) || provided == null)
            {
                if (p.IsRequired)
                {
                    throw new ArgumentException($"Missing required parameter '{p.Name}'.", p.Name);
                }

                continue;
            }

            var coerced = Coerce(provided, p);
            if (coerced is string s && s.Length > (p.MaxLength ?? maxString))
            {
                throw new ArgumentException($"Parameter '{p.Name}' exceeds the maximum length of {p.MaxLength ?? maxString}.", p.Name);
            }

            values[p.Name] = coerced;
        }

        return values;
    }

    private static object Coerce(object value, ProcedureParameter p)
    {
        if (p.ClrType.IsInstanceOfType(value))
        {
            return value;
        }

        string s = value is JsonElement je ? (je.ValueKind == JsonValueKind.String ? je.GetString() ?? string.Empty : je.GetRawText()) : value.ToString() ?? string.Empty;
        try
        {
            if (p.ClrType == typeof(string)) return s;
            if (p.ClrType == typeof(int)) return int.Parse(s, CultureInfo.InvariantCulture);
            if (p.ClrType == typeof(long)) return long.Parse(s, CultureInfo.InvariantCulture);
            if (p.ClrType == typeof(short)) return short.Parse(s, CultureInfo.InvariantCulture);
            if (p.ClrType == typeof(byte)) return byte.Parse(s, CultureInfo.InvariantCulture);
            if (p.ClrType == typeof(decimal)) return decimal.Parse(s, CultureInfo.InvariantCulture);
            if (p.ClrType == typeof(double)) return double.Parse(s, CultureInfo.InvariantCulture);
            if (p.ClrType == typeof(float)) return float.Parse(s, CultureInfo.InvariantCulture);
            if (p.ClrType == typeof(bool)) return bool.Parse(s);
            if (p.ClrType == typeof(Guid)) return Guid.Parse(s);
            if (p.ClrType == typeof(DateTime)) return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (p.ClrType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new ArgumentException($"Parameter '{p.Name}' cannot be converted to {p.SqlType}.", p.Name, ex);
        }

        throw new ArgumentException($"Parameter '{p.Name}' has an unsupported type.", p.Name);
    }

    /// <summary>
    /// Returns null if access is denied. Consent row filters cannot be pushed into a procedure and are not applied
    /// in memory (differs from SQL semantics, review E-6); such a consent therefore denies the call (fail-closed).
    /// </summary>
    private async Task<(TableAccessDecision Decision, TableMetadata Meta)?> EvaluateTableAsync(
        string tableKey,
        ClaimsPrincipal user,
        Sid userSid,
        TenantId tenantId,
        bool consentBypassed,
        CancellationToken ct)
    {
        if (_tableRepository == null || !TableIdentifier.TryParse("default." + tableKey, out var tableId))
        {
            return null;
        }

        var meta = await _tableRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        if (meta == null || meta.Columns.Count == 0)
        {
            return null;
        }

        TableAccessDecision decision;
        if (consentBypassed)
        {
            decision = TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        }
        else
        {
            if (_consentRepository == null || _consentResolution == null)
            {
                return null;
            }

            var groupSids = user.GetGroupSids();
            var subjects = groupSids.Append(userSid).ToList();
            var consents = await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, tableId, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false);
            decision = _consentResolution.ResolveAccess(
                userSid,
                groupSids,
                user.GetUserRoles(),
                tableId,
                consents.Where(c => c.TenantId == tenantId).ToList(),
                meta.Dialect);
        }

        if (!decision.IsAllowed || !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            return null;
        }

        if (!consentBypassed && _policyEnforcement != null && _policyEnforcement.HasPolicies(tenantId))
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in user.Claims)
            {
                attributes[claim.Type] = claim.Value;
            }

            attributes["gql.action"] = "read";

            var ctx = new SecurityEvaluationContext(
                UserSid: userSid,
                GroupSids: user.GetGroupSids(),
                Tenant: tenantId,
                TargetTable: tableId,
                RequestedColumns: meta.Columns.Select(c => c.ColumnName).ToList(),
                ClientIp: ResolveClientIp(user),
                Timestamp: DateTimeOffset.UtcNow,
                PurposeId: user.FindFirst("purpose")?.Value ?? user.FindFirst("purpose_id")?.Value,
                Attributes: attributes);

            var policy = await _policyEnforcement.EvaluatePolicyAsync(ctx, ct).ConfigureAwait(false);
            if (!policy.IsAllowed || !string.IsNullOrWhiteSpace(policy.CombinedRowFilterSql))
            {
                return null;
            }

            // The ABAC decision can only lower column access (Clear -> Mask -> Deny), never raise it.
            var merged = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in meta.Columns)
            {
                var consentLevel = decision.GetColumnAccess(col.ColumnName);
                var policyLevel = policy.GetColumnAccess(col.ColumnName);
                merged[col.ColumnName] = consentLevel < policyLevel ? consentLevel : policyLevel;
            }

            decision = decision with { ColumnAccess = merged, HasUnconstrainedColumnAllow = false };
        }

        return (decision, meta);
    }

    private (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows) GovernResult(
        ProcedureDefinition definition,
        RawProcedureResult raw,
        Dictionary<string, (TableAccessDecision Decision, TableMetadata Meta)> decisions)
    {
        (TableAccessDecision Decision, TableMetadata Meta)? mapped =
            definition.ResultTable != null && decisions.TryGetValue(definition.ResultTable, out var m) ? m : null;

        var cleared = new HashSet<string>(definition.ClearedResultColumns, StringComparer.OrdinalIgnoreCase);

        // Plan per raw column: index, output name, action
        var plan = new List<(int Index, string Name, ColumnAccessLevel Level, MaskingRule? Rule)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < raw.Columns.Count; i++)
        {
            string col = raw.Columns[i];
            if (string.IsNullOrWhiteSpace(col))
            {
                continue; // unnamed columns cannot be governed
            }

            if (!seen.Add(col))
            {
                throw new InvalidOperationException("The procedure returned duplicate column names; the result cannot be governed.");
            }

            if (cleared.Contains(col))
            {
                plan.Add((i, col, ColumnAccessLevel.Clear, null));
                continue;
            }

            if (definition.ValidationMode == ProcedureValidationMode.Declared)
            {
                if (definition.DeclaredOutputs.Count == 0 || definition.DeclaredOutputs.Contains(col, StringComparer.OrdinalIgnoreCase))
                {
                    plan.Add((i, col, ColumnAccessLevel.Clear, null));
                }

                continue;
            }

            if (mapped == null || !mapped.Value.Meta.HasColumn(col))
            {
                continue; // unknown column: removed (fail-closed)
            }

            var (decision, meta) = mapped.Value;
            var level = decision.GetEffectiveColumnAccess(col, meta);
            if (level == ColumnAccessLevel.Deny)
            {
                continue;
            }

            MaskingRule? rule = null;
            if (level == ColumnAccessLevel.Mask)
            {
                var catalogColumn = meta.GetColumn(col);
                if (!meta.ColumnMaskingRules.TryGetValue(col, out rule) && catalogColumn != null)
                {
                    meta.ColumnMaskingRules.TryGetValue(catalogColumn.ColumnName, out rule);
                }

                rule ??= new MaskingRule { RuleType = "REDACT" };
            }

            plan.Add((i, col, level, rule));
        }

        if (plan.Count == 0 && raw.Columns.Count > 0)
        {
            _logger?.LogWarning(
                "Procedure endpoint '{Endpoint}': all result columns were removed by governance. Declare @result-table / @result-column.",
                definition.Name);
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>(raw.Rows.Count);
        foreach (var row in raw.Rows)
        {
            var output = new Dictionary<string, object?>(plan.Count, StringComparer.Ordinal);
            foreach (var (index, colName, level, rule) in plan)
            {
                object? value = row[index];
                if (level == ColumnAccessLevel.Mask)
                {
                    value = _masking != null && rule != null ? _masking.MaskValue(colName, value, rule) : null;
                }

                output[colName] = value;
            }

            rows.Add(output);
        }

        return (plan.Select(p => p.Name).ToList(), rows);
    }

    private static Dictionary<string, string> HashParameters(IReadOnlyDictionary<string, object?> values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            result[key] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
        }

        return result;
    }

    private async Task AuditAsync(
        string eventType,
        string decision,
        ProcedureDefinition definition,
        TenantId tenantId,
        Sid actor,
        object details,
        CancellationToken ct)
    {
        if (_audit == null)
        {
            _logger?.LogWarning("No audit repository available; {EventType} for '{Endpoint}' was not recorded.", eventType, definition.Name);
            return;
        }

        await _audit.RecordAuditEventAsync(
            new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = eventType,
                ActorSid = actor,
                TargetTable = definition.ProcedureName,
                Decision = decision,
                TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(details)
            },
            ct).ConfigureAwait(false);
    }

    private System.Net.IPAddress ResolveClientIp(ClaimsPrincipal user)
    {
        return _clientIpResolver?.ResolveClientIp() ??
            (user.FindFirst("ip")?.Value is { Length: > 0 } ipStr && System.Net.IPAddress.TryParse(ipStr, out var parsed)
                ? parsed
                : System.Net.IPAddress.None);
    }
}
