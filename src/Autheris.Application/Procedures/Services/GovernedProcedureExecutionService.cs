namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TableDecision = (Autheris.Domain.Interfaces.TableAccessDecision Decision, Autheris.Domain.Model.TableMetadata Meta, System.Collections.Generic.IReadOnlySet<string>? SensitivityOnlyMasked);

/// <summary>
/// F-SQL-02 (phase 1, read): Governed execution of a declared stored procedure.
/// Row isolation is enforced by the database (SECURITY POLICY + SESSION_CONTEXT, see ADR-018); the gateway enforces
/// consents on all referenced tables, column deny/mask on the result set (unknown columns are removed, fail-closed)
/// and writes a synchronous audit entry before any data is returned.
/// </summary>
public sealed class GovernedProcedureExecutionService : IProcedureExecutionService
{
    /// <summary>The one message of every pre-visibility failure (SEC-PROC152-03); never contains the name or the reason.</summary>
    public const string NotFoundMessage = "Procedure endpoint not found.";

    internal const string UnavailableMessage = "Procedure endpoint is currently unavailable.";
    private const string InvalidTargetMarker = "<invalid>";

    private static readonly Regex TargetNameRegex = new(
        @"^[A-Za-z0-9_.\-]{1,128}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private readonly IConsentCacheService? _consentCache;
    private readonly IProcedureRegistry _registry;
    private readonly IProcedureInvoker _invoker;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly IColumnMaskingProvider? _masking;
    private readonly IPolicyEnforcementService? _policyEnforcement;
    private readonly Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? _rebacEvaluator;
    private readonly IAuditLogRepository _audit;
    private readonly Autheris.Application.Audit.IAuditWriter _auditWriter;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly IProcedureRowScopeResolver? _rowScope;
    private readonly ILogger<GovernedProcedureExecutionService>? _logger;
    private readonly ITableReadConcurrencyGate? _concurrencyGate;
    private readonly ProcedureDenialAuditLimiter _denialAuditLimiter;

    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? _mandatoryFilters;
    private readonly Autheris.Application.Policy.TypedPolicySources? _typedPolicies;
    private readonly IAccessProfileRepository? _accessProfileRepository;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _memoryCache;
    private readonly Autheris.Application.Policy.Interfaces.IAccessProfileCache? _accessProfileCache;

    public GovernedProcedureExecutionService(
        IProcedureRegistry registry,
        IProcedureInvoker invoker,
        IOptions<GatewayOptions> options,
        IAuditLogRepository audit,
        ITableMetadataRepository? tableRepository = null,
        IConsentRepository? consentRepository = null,
        IConsentResolutionService? consentResolution = null,
        IColumnMaskingProvider? masking = null,
        IPolicyEnforcementService? policyEnforcement = null,
        IClientIpResolver? clientIpResolver = null,
        ILogger<GovernedProcedureExecutionService>? logger = null,
        IProcedureRowScopeResolver? rowScope = null,
        Autheris.Application.Security.Rebac.Interfaces.IRebacEvaluator? rebacEvaluator = null,
        IConsentCacheService? consentCache = null,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null,
        IAccessProfileRepository? accessProfileRepository = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache = null,
        Autheris.Application.Policy.Interfaces.IAccessProfileCache? accessProfileCache = null,
        Autheris.Application.Policy.TypedPolicySources? typedPolicies = null,
        Autheris.Application.Audit.IAuditWriter? auditWriter = null,
        ITableReadConcurrencyGate? concurrencyGate = null,
        ProcedureDenialAuditLimiter? denialAuditLimiter = null)
    {
        _concurrencyGate = concurrencyGate;
        _denialAuditLimiter = denialAuditLimiter ?? new ProcedureDenialAuditLimiter();
        _typedPolicies = typedPolicies;
        _mandatoryFilters = mandatoryFilters;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _auditWriter = auditWriter ?? new Autheris.Application.Audit.AuditWriter(_audit, Microsoft.Extensions.Logging.Abstractions.NullLogger<Autheris.Application.Audit.AuditWriter>.Instance);
        _tableRepository = tableRepository;
        _consentRepository = consentRepository;
        _consentResolution = consentResolution;
        _masking = masking;
        _policyEnforcement = policyEnforcement;
        _clientIpResolver = clientIpResolver;
        _logger = logger;
        _rowScope = rowScope;
        _rebacEvaluator = rebacEvaluator;
        _consentCache = consentCache;
        _accessProfileRepository = accessProfileRepository;
        _memoryCache = memoryCache;
        _accessProfileCache = accessProfileCache;
    }

    public async Task<GovernedProcedureResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object?>? rawInputs,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        string traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        // 1. Registry lookup. An unknown name is audited through the same writer as every denial (one awaited write, no early
        // return) and answered exactly like an invisible procedure (SEC-PROC152-03/04).
        if (string.IsNullOrWhiteSpace(name) || !_registry.TryGet(name, out var registered) || registered == null)
        {
            await AuditUnknownAsync(name, user, tenantId, traceId, ct).ConfigureAwait(false);
            throw new KeyNotFoundException(NotFoundMessage);
        }

        var definition = registered.Definition;

        // 1a. Write-mode guard (SEC-PROC152I2-10): only read procedures are executable. A write-mode definition is not visible,
        // answered exactly like an unknown name, before anything caller-controlled is looked at.
        if (definition.Mode != ProcedureMode.Read)
        {
            var denyUser = user.GetUserSid() ?? new Sid("anonymous");
            await AuditAsync("PROCEDURE_DENIED", "DENY", definition, tenantId, denyUser, new { reason = "write-mode" }, traceId, ct).ConfigureAwait(false);
            throw new KeyNotFoundException(NotFoundMessage);
        }

        var sw = Stopwatch.StartNew();
        var userSid = user.GetUserSid() ?? new Sid("anonymous");

        // 2. Visibility (404): identity, role gate, table set and consent. Nothing caller-controlled (parameters, state) is
        // looked at before this stage has passed (no existence, health or parameter oracle).
        var visibility = await EvaluateVisibilityAsync(registered, user, tenantId, traceId, ct).ConfigureAwait(false);
        if (!visibility.Visible)
        {
            await AuditAsync("PROCEDURE_DENIED", "DENY", definition, tenantId, userSid, new { reason = visibility.Reason }, traceId, ct).ConfigureAwait(false);
            throw new KeyNotFoundException(NotFoundMessage);
        }

        // 3. Availability (503), only for callers that may see the procedure.
        if (registered.State != ProcedureState.Active || registered.Validation == null)
        {
            throw new ProcedureUnavailableException(UnavailableMessage);
        }

        var decisions = visibility.Decisions;

        // 4. Concurrency lease (429), after visibility and availability; released in the single finally below.
        List<IDisposable>? leases;
        try
        {
            leases = AcquireLeases(decisions, user, tenantId);
        }
        catch (GatewayThrottledException)
        {
            await AuditAsync("PROCEDURE_THROTTLED", "DENY", definition, tenantId, userSid, new { reason = "concurrency" }, traceId, ct).ConfigureAwait(false);
            throw;
        }

        try
        {
            // 5. Input validation (unknown and context-bound parameters are rejected, never ignored)
            var values = ValidateInputs(definition, rawInputs, out int? requestedLimit);

            // 6. Call (security context goes into the read-only SESSION_CONTEXT)
            var context = new ProcedureSecurityContext(
                tenantId.Value,
                userSid.Value,
                user.FindFirst("purpose")?.Value ?? user.FindFirst("purpose_id")?.Value);

            // 6a. Row budget, decided before the invoke from the visibility decisions (SEC-PROC152I2-01): a scoped call scans up
            // to MaxScanRows raw rows (the limit is applied after the row scope), an unscoped call reads the limit itself.
            var procedureOptions = _options.Value.SqlEndpoints.Procedures;
            int effectiveLimit = requestedLimit ?? procedureOptions.MaxRows;
            bool scoped = IsRowScoped(definition, decisions, out var resultDecision);
            int rowBudget = scoped ? procedureOptions.EffectiveMaxScanRows : effectiveLimit;

            RawProcedureResult raw;
            try
            {
                raw = await _invoker.ExecuteReadAsync(definition, values, context, rowBudget, ct).ConfigureAwait(false);
            }
            catch (ProcedureBusinessException bex)
            {
                // SEC-PROC152-08: number/SQLSTATE and ids only, never the database message text.
                _logger?.LogWarning(
                    "Procedure '{Procedure}' rejected the request: number {Number}, sqlState {SqlState}, trace {TraceId}.",
                    definition.ProcedureName, bex.ErrorNumber, bex.SqlState, traceId);
                await AuditAsync(
                    "PROCEDURE_REJECTED", "DENY", definition, tenantId, userSid,
                    new { reason = "business-error", number = bex.ErrorNumber, sqlState = bex.SqlState }, traceId, ct).ConfigureAwait(false);
                throw new ProcedureBusinessException(bex.ErrorNumber, bex.SqlState, traceId);
            }
            catch (DbException dbx)
            {
                // SEC-PROC152-08: driver text (messages, inner exceptions) may contain values; log the type and number only.
                _logger?.LogError(
                    "Stored procedure endpoint '{Procedure}' failed with {ExceptionType} (number {Number}, trace {TraceId}).",
                    definition.ProcedureName, dbx.GetType().Name, DbErrorNumber(dbx), traceId);
                await AuditAsync("PROCEDURE_FAILED", "DENY", definition, tenantId, userSid, new { reason = dbx.GetType().Name }, traceId, ct).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SEC-PROC152-08: exception type only; never the message, inner exceptions or ToString (may carry values).
                _logger?.LogError(
                    "Stored procedure endpoint '{Procedure}' failed with {ExceptionType} (trace {TraceId}).",
                    definition.ProcedureName, ex.GetType().Name, traceId);
                await AuditAsync("PROCEDURE_FAILED", "DENY", definition, tenantId, userSid, new { reason = ex.GetType().Name }, traceId, ct).ConfigureAwait(false);
                throw;
            }

            // 6b. Row scope: keep only the rows the consent row filter of the result table allows, THEN cut to the limit
            // (SEC-PROC152I2-02: the cut never precedes the scope; a scope failure stays fail-closed).
            int rowsRemovedByScope = 0;
            bool truncated = raw.MoreRowsAvailable;
            bool scanCapReached = scoped && raw.MoreRowsAvailable;
            if (scoped)
            {
                var scopedRaw = await ApplyRowScopeAsync(definition, raw, resultDecision.Decision, resultDecision.Meta, registered.Validation.ResultColumnSources, context, tenantId, userSid, traceId, ct).ConfigureAwait(false);
                rowsRemovedByScope = raw.Rows.Count - scopedRaw.Rows.Count;
                truncated = scopedRaw.Rows.Count > effectiveLimit || raw.MoreRowsAvailable;
                raw = scopedRaw.Rows.Count > effectiveLimit
                    ? new RawProcedureResult(scopedRaw.Columns, scopedRaw.Rows.Take(effectiveLimit).ToList(), raw.MoreRowsAvailable)
                    : scopedRaw;
            }

            // 7. Result-set governance (deny / mask / drop unknown columns)
            var governed = GovernResult(definition, raw, decisions, registered.Validation.ResultColumnSources, tenantId);

            // 8. Synchronous audit BEFORE data leaves the gateway (fail-closed: an audit failure aborts the response)
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
                    truncated,
                    scanCapReached,
                    effectiveLimit,
                    rowsRemovedByScope,
                    virtual_filters = decisions
                        .Where(d => d.Value.Decision.AppliedVirtualFilters is { Count: > 0 })
                        .ToDictionary(d => d.Key.ToString(), d => d.Value.Decision.AppliedVirtualFilters),
                    returnedColumns = governed.Columns.Count,
                    droppedColumns = raw.Columns.Count - governed.Columns.Count,
                    parameterHashes = HashParameters(values)
                },
                traceId,
                ct).ConfigureAwait(false);

            return new GovernedProcedureResult(governed.Columns, governed.Rows, governed.Rows.Count, truncated, sw.ElapsedMilliseconds);
        }
        finally
        {
            ReleaseLeases(leases);
        }
    }

    public async Task<bool> IsVisibleAsync(RegisteredProcedure procedure, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        ArgumentNullException.ThrowIfNull(user);
        if (procedure.State != ProcedureState.Active)
        {
            return false;
        }

        var visibility = await EvaluateVisibilityAsync(procedure, user, tenantId, traceId: null, ct).ConfigureAwait(false);
        return visibility.Visible;
    }

    /// <summary>
    /// SEC-PROC152-01/02/10: the visibility stage. Every failure except cancellation (including an unexpected exception) is
    /// "not visible"; nothing from the caller or the exception reaches the response.
    /// </summary>
    internal async Task<ProcedureVisibility> EvaluateVisibilityAsync(
        RegisteredProcedure registered,
        ClaimsPrincipal user,
        TenantId tenantId,
        string? traceId,
        CancellationToken ct)
    {
        var definition = registered.Definition;
        try
        {
            bool consentBypassed = _options.Value.IsConsentBypassed;
            var sidNullable = user.GetUserSid();
            if (sidNullable == null && !consentBypassed)
            {
                return ProcedureVisibility.Hidden("identity");
            }

            var userSid = sidNullable ?? new Sid("anonymous");
            if (definition.RequiredRoles.Count > 0 && !HasAnyRole(user, definition.RequiredRoles))
            {
                return ProcedureVisibility.Hidden("role");
            }

            // Table set: an Active procedure is governed by every table it references plus its result table; otherwise only
            // the result table can be evaluated. Zero tables is never visible (nothing could be consent-checked).
            var tableKeys = new List<string>();
            if (registered.State == ProcedureState.Active && registered.Validation != null)
            {
                tableKeys.AddRange(registered.Validation.ReferencedTables);
            }

            if (definition.ResultTable != null && !tableKeys.Contains(definition.ResultTable, StringComparer.OrdinalIgnoreCase))
            {
                tableKeys.Add(definition.ResultTable);
            }

            if (tableKeys.Count == 0)
            {
                return ProcedureVisibility.Hidden("no-result-table");
            }

            var decisions = new Dictionary<string, TableDecision>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in tableKeys)
            {
                // A consent row filter on the result table is enforced after the call by a key match (RowScopeKey);
                // on every other table it cannot be enforced and denies the call.
                bool allowRowFilter = definition.RowScopeKey.Count > 0 && _rowScope != null && IsResultTable(definition, key);
                var evaluated = await EvaluateTableAsync(definition.CatalogDomain, key, user, userSid, tenantId, consentBypassed, allowRowFilter, ct).ConfigureAwait(false);
                if (evaluated == null)
                {
                    return ProcedureVisibility.Hidden("consent");
                }

                decisions[key] = evaluated.Value;
            }

            return new ProcedureVisibility(true, string.Empty, decisions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Type only: the exception text can name tables, filters or values.
            _logger?.LogError(
                "Procedure '{Procedure}': the visibility stage failed with {ExceptionType} (trace {TraceId}); treated as not visible.",
                definition.Name, ex.GetType().Name, traceId);
            return ProcedureVisibility.Hidden("visibility-error");
        }
    }

    private async Task AuditUnknownAsync(string? name, ClaimsPrincipal user, TenantId tenantId, string traceId, CancellationToken ct)
    {
        // SEC-PROC152-05: only authenticated callers, rate limited per tenant and SID; anonymous (bypass) callers are not audited.
        var sid = user.GetUserSid();
        if (sid == null)
        {
            return;
        }

        if (!_denialAuditLimiter.TryAcquire(tenantId.Value, sid.Value.Value))
        {
            long dropped = _denialAuditLimiter.Dropped;
            if (dropped == 1 || dropped % 100 == 0)
            {
                _logger?.LogInformation("Unknown-procedure denial audit rate limit reached; {Dropped} audit rows dropped so far.", dropped);
            }

            return;
        }

        string target = name != null && TargetNameRegex.IsMatch(name) ? name : InvalidTargetMarker;
        await AuditAsync("PROCEDURE_DENIED", "DENY", target, tenantId, sid.Value, new { reason = "unknown" }, traceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// SEC-PROC152-06/07: one lease per consent-checked table in ordinal order of the qualified name, key and limit exactly as in
    /// the SQL path (so both share one budget). A partial acquisition is rolled back. Limit greater than zero without a gate
    /// fails closed.
    /// </summary>
    private List<IDisposable>? AcquireLeases(Dictionary<string, TableDecision> decisions, ClaimsPrincipal user, TenantId tenantId)
    {
        int max = _options.Value.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        if (max <= 0)
        {
            return null;
        }

        if (_concurrencyGate == null)
        {
            throw new InvalidOperationException("The table read concurrency gate is not configured although a limit is set (fail-closed).");
        }

        string sid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                     ?? user.FindFirst("sub")?.Value
                     ?? "anonymous";
        var tableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in decisions.Values)
        {
            tableSet.Add(d.Meta.Identifier.ToQualifiedName());
        }

        var tables = new List<string>(tableSet);
        tables.Sort(StringComparer.Ordinal);
        var leases = new List<IDisposable>(tables.Count);
        try
        {
            foreach (var table in tables)
            {
                var key = $"{tenantId.Value}|{sid}|{table}".ToLowerInvariant();
                leases.Add(_concurrencyGate.TryEnter(key, max) ?? throw new GatewayThrottledException(2));
            }
        }
        catch
        {
            ReleaseLeases(leases);
            throw;
        }

        return leases;
    }

    private static void ReleaseLeases(List<IDisposable>? leases)
    {
        if (leases == null)
        {
            return;
        }

        foreach (var lease in leases)
        {
            lease.Dispose();
        }
    }

    private static int? DbErrorNumber(DbException ex) => ex.GetType().GetProperty("Number")?.GetValue(ex) as int?;

    private static bool HasAnyRole(ClaimsPrincipal user, IReadOnlyList<string> roles)
    {
        var claimRoles = user.GetUserRoles();
        return roles.Any(r => !string.IsNullOrWhiteSpace(r) && (user.IsInRole(r) || claimRoles.Contains(r)));
    }

    private const string InvalidLimitMessage = "Invalid 'limit'.";

    private Dictionary<string, object?> ValidateInputs(ProcedureDefinition definition, IReadOnlyDictionary<string, object?>? rawInputs, out int? requestedLimit)
    {
        // The reserved key 'limit' (any case) is taken out before the unknown-parameter check: it never reaches the parameter
        // values, the parameter hashes, the SQL call or SESSION_CONTEXT (SEC-PROC152I2-07).
        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        object? limitRaw = null;
        int limitKeys = 0;
        if (rawInputs != null)
        {
            foreach (var (key, value) in rawInputs)
            {
                if (string.Equals(key, "limit", StringComparison.OrdinalIgnoreCase))
                {
                    limitKeys++;
                    limitRaw = value;
                }
                else
                {
                    inputs[key] = value;
                }
            }
        }

        // Several spellings of the key in a case-sensitive input map are ambiguous: fail closed.
        requestedLimit = limitKeys > 1 ? throw new ArgumentException(InvalidLimitMessage) : ParseLimit(limitRaw, _options.Value.SqlEndpoints.Procedures.MaxRows);
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

    /// <summary>
    /// Parses the reserved <c>limit</c> input: a JSON integer or a digit string. Null / JSON null is absent, everything else that is
    /// not a plain positive integer is a 400 with a constant text (the value is never echoed); above <paramref name="maxRows"/> or
    /// beyond <see cref="int"/> range it clamps to <paramref name="maxRows"/>.
    /// </summary>
    private static int? ParseLimit(object? raw, int maxRows)
    {
        string? digits;
        switch (raw)
        {
            case null:
                return null;
            case JsonElement { ValueKind: JsonValueKind.Null }:
                return null;
            case JsonElement { ValueKind: JsonValueKind.Number } number:
                digits = number.GetRawText();
                break;
            case JsonElement { ValueKind: JsonValueKind.String } text:
                digits = text.GetString();
                break;
            case JsonElement:
                throw new ArgumentException(InvalidLimitMessage);
            case string str:
                digits = str;
                break;
            case int or long or short or byte or uint or ushort:
                long numeric = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                return numeric < 1 ? throw new ArgumentException(InvalidLimitMessage) : (int)Math.Min(numeric, maxRows);
            default:
                throw new ArgumentException(InvalidLimitMessage);
        }

        if (string.IsNullOrEmpty(digits) || !digits.All(static c => c is >= '0' and <= '9'))
        {
            throw new ArgumentException(InvalidLimitMessage);
        }

        string trimmed = digits.TrimStart('0');
        if (trimmed.Length == 0)
        {
            throw new ArgumentException(InvalidLimitMessage); // zero
        }

        // More than 9 digits cannot be a valid in-range value below MaxRows (hard cap 100000): clamp without parsing.
        return trimmed.Length > 9 ? maxRows : Math.Min(int.Parse(trimmed, CultureInfo.InvariantCulture), maxRows);
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
    /// in memory (differs from SQL semantics, review E-6); such a consent therefore denies the call (fail-closed),
    /// unless <paramref name="allowRowFilter"/> is set: the result table's filter is then enforced by a database key
    /// match after the call (<see cref="ApplyRowScopeAsync"/>).
    /// </summary>
    internal Task<TableDecision?> EvaluateTableAsync(
        string catalogDomain,
        string tableKey,
        ClaimsPrincipal user,
        Sid userSid,
        TenantId tenantId,
        bool consentBypassed,
        bool allowRowFilter,
        CancellationToken ct)
    {
        if (!TableIdentifier.TryParse(catalogDomain + "." + tableKey, out var tableId) &&
            !TableIdentifier.TryParse(tableKey, out tableId))
        {
            return Task.FromResult<TableDecision?>(null);
        }

        return EvaluateTableAsync(tableId, user, userSid, tenantId, consentBypassed, allowRowFilter, ct);
    }

    internal async Task<TableDecision?> EvaluateTableAsync(
        TableIdentifier tableId,
        ClaimsPrincipal user,
        Sid userSid,
        TenantId tenantId,
        bool consentBypassed,
        bool allowRowFilter,
        CancellationToken ct)
    {
        if (_tableRepository == null)
        {
            return null;
        }

        var meta = await _tableRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        if (meta == null || meta.Columns.Count == 0 || !meta.Table.IsActive)
        {
            return null;
        }

        if (!consentBypassed && (_consentRepository == null || _consentResolution == null))
        {
            return null;
        }

        // Architecture 1: the shared access decision (ReBAC on query paths, consents with the decision cache, Casbin as
        // an additional restriction). Row filters (consent or Casbin) cannot be pushed into a procedure: they deny the
        // call unless the result table's filter is enforced by the row scope after the call.
        var decision = consentBypassed && (_consentRepository == null || _consentResolution == null)
            ? await WithVirtualFiltersAsync(
                TableAccessDecision.Allowed(tableId, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true),
                user, userSid, tenantId, meta, ct).ConfigureAwait(false)
            : await new Autheris.Application.Policy.TableAccessPolicy(
                _consentRepository!,
                _consentResolution!,
                _consentCache ?? Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
                _policyEnforcement ?? throw new InvalidOperationException("IPolicyEnforcementService is not configured; refusing to evaluate table access without policy enforcement (fail-closed)."),
                _rebacEvaluator ?? Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
                _clientIpResolver,
                _options.Value,
                _mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance,
                contractManager: null,
                accessProfileRepository: _accessProfileRepository,
                memoryCache: _memoryCache,
                accessProfileCache: _accessProfileCache,
                rowPolicyFactory: _typedPolicies?.RowPolicies,
                maskPolicyFactory: _typedPolicies?.MaskPolicies)
                .DecideAsync(
                    new Autheris.Application.Policy.TableAccessQuery(
                        userSid,
                        tenantId,
                        user.GetGroupSids(),
                        user.GetUserRoles(),
                        meta,
                        user.Claims,
                        ClientIp: ResolveClientIp(user),
                        ExtraAttributes: new Dictionary<string, object?> { ["gql.action"] = "read" },
                        ObjectKind: FilterObjectKinds.ProcedureResult),
                    ct).ConfigureAwait(false);

        if (!decision.IsAllowed || (!allowRowFilter && !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql)))
        {
            return null;
        }

        // SEC-PROC152-09: computed BEFORE the ABAC rewrite below, which makes a sensitivity mask indistinguishable from an
        // explicit one. null (not computable) lifts nothing.
        var sensitivityOnlyMasked = ComputeSensitivityOnlyMask(decision, meta);

        if (!consentBypassed && _policyEnforcement != null && _policyEnforcement.HasPolicies(tenantId))
        {
            // With ABAC active every catalog column carries an explicit level; columns outside the catalog are denied.
            var explicitLevels = meta.Columns.ToDictionary(c => c.ColumnName, c => decision.GetEffectiveColumnAccess(c.ColumnName, meta), StringComparer.OrdinalIgnoreCase);
            decision = decision with { ColumnAccess = explicitLevels, HasUnconstrainedColumnAllow = false };
        }

        return (decision, meta, sensitivityOnlyMasked);
    }

    /// <summary>
    /// Columns whose <see cref="ColumnAccessLevel.Mask"/> comes from catalog sensitivity only: no explicit base or scope-specific
    /// column rule exists and the consent allows the columns unconstrained. Only these may be lifted by <c>cleared_columns</c>.
    /// </summary>
    private static HashSet<string>? ComputeSensitivityOnlyMask(TableAccessDecision decision, TableMetadata meta)
    {
        try
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in meta.Columns)
            {
                if (decision.ColumnAccess.ContainsKey(column.ColumnName))
                {
                    continue;
                }

                if (decision.HasUnconstrainedColumnAllow &&
                    decision.GetEffectiveColumnAccess(column.ColumnName, meta) == ColumnAccessLevel.Mask)
                {
                    set.Add(column.ColumnName);
                }
            }

            return set;
        }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// With catalog validation the database reports the source of every result column (D-2): a key column must come from
    /// the same column of the result table, otherwise the match would select unrelated rows. Declared mode has no source
    /// information; there the declaration is trusted (documented residual risk).
    /// </summary>
    private static bool IsKeyFromResultTable(
        string resultColumn,
        string tableColumn,
        TableMetadata meta,
        IReadOnlyDictionary<string, ResultColumnSource>? resultColumnSources)
    {
        if (resultColumnSources == null)
        {
            return true;
        }

        var source = resultColumnSources
            .FirstOrDefault(kv => string.Equals(kv.Key, resultColumn, StringComparison.OrdinalIgnoreCase)).Value;
        return source?.Table != null && source.Column != null &&
               string.Equals(source.Schema, meta.Identifier.Schema, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(source.Table, meta.Identifier.TableName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(source.Column, tableColumn, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResultTable(ProcedureDefinition definition, string key) =>
        definition.ResultTable != null && string.Equals(key, definition.ResultTable, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes the rows of the procedure result whose key (<see cref="ProcedureDefinition.RowScopeKey"/>) the consent row
    /// filter of the result table does not allow. Fail-closed: a missing key column, a key column the caller may not use
    /// in a filter, or a failing lookup denies the call; rows with a NULL key part are removed.
    /// </summary>
    /// <summary>
    /// SEC-PROC152I2-01: the single predicate for "a row filter is active on the result table". The invoker budget and the
    /// row scope branch both use it.
    /// </summary>
    private static bool IsRowScoped(ProcedureDefinition definition, Dictionary<string, TableDecision> decisions, out TableDecision resultDecision)
    {
        resultDecision = default;
        return definition.ResultTable != null &&
               decisions.TryGetValue(definition.ResultTable, out resultDecision) &&
               !string.IsNullOrWhiteSpace(resultDecision.Decision.CombinedRowFilterSql);
    }

    private async Task<RawProcedureResult> ApplyRowScopeAsync(
        ProcedureDefinition definition,
        RawProcedureResult raw,
        TableAccessDecision decision,
        TableMetadata meta,
        IReadOnlyDictionary<string, ResultColumnSource>? resultColumnSources,
        ProcedureSecurityContext security,
        TenantId tenantId,
        Sid userSid,
        string traceId,
        CancellationToken ct)
    {
        var keyColumns = definition.RowScopeKey;
        var tableColumnNames = definition.RowScopeKeyTable.Count == keyColumns.Count ? definition.RowScopeKeyTable : keyColumns;
        var indexes = new int[keyColumns.Count];
        bool usable = _rowScope != null && keyColumns.Count > 0;
        for (int k = 0; usable && k < keyColumns.Count; k++)
        {
            indexes[k] = raw.Columns.ToList().FindIndex(c => string.Equals(c, keyColumns[k], StringComparison.OrdinalIgnoreCase));
            var column = meta.GetColumn(tableColumnNames[k]);
            // Filtering on a column that is not effectively Clear would leak it through the match (side channel).
            usable = indexes[k] >= 0 && column != null &&
                     decision.GetEffectiveColumnAccess(column.ColumnName, meta) == ColumnAccessLevel.Clear &&
                     IsKeyFromResultTable(keyColumns[k], column.ColumnName, meta, resultColumnSources);
        }

        if (!usable)
        {
            await AuditAsync("PROCEDURE_DENIED", "DENY", definition, tenantId, userSid, new { reason = "row-scope-key" }, traceId, ct).ConfigureAwait(false);
            throw new ProcedureUnavailableException(UnavailableMessage);
        }

        var tableKeyColumns = tableColumnNames.Select(k => meta.GetColumn(k)!.ColumnName).ToList();
        var candidates = raw.Rows.Select(r => indexes.Select(i => r[i]).ToArray()).ToList();

        IReadOnlySet<string> allowed;
        try
        {
            allowed = await _rowScope!.GetAllowedKeysAsync(definition, meta, decision, tableKeyColumns, candidates, security, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(
                "Row scope lookup for procedure endpoint '{Endpoint}' failed with {ExceptionType} (trace {TraceId}).",
                definition.Name, ex.GetType().Name, traceId);
            await AuditAsync("PROCEDURE_FAILED", "DENY", definition, tenantId, userSid, new { reason = "row-scope-" + ex.GetType().Name }, traceId, ct).ConfigureAwait(false);
            throw new ProcedureUnavailableException(UnavailableMessage);
        }

        var kept = new List<object?[]>(raw.Rows.Count);
        for (int r = 0; r < raw.Rows.Count; r++)
        {
            string? key = RowScopeKeys.Normalize(candidates[r]);
            if (key != null && allowed.Contains(key))
            {
                kept.Add(raw.Rows[r]);
            }
        }

        return new RawProcedureResult(raw.Columns, kept, raw.MoreRowsAvailable);
    }

    private (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows) GovernResult(
        ProcedureDefinition definition,
        RawProcedureResult raw,
        Dictionary<string, TableDecision> decisions,
        IReadOnlyDictionary<string, ResultColumnSource>? sources,
        TenantId tenantId)
    {
        TableDecision? mapped = null;
        if (definition.ResultTable != null)
        {
            if (decisions.TryGetValue(definition.ResultTable, out var m))
            {
                mapped = m;
            }
            else
            {
                var rParts = definition.ResultTable.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var rSchema = rParts.Length >= 2 ? rParts[^2] : "dbo";
                var rTable = rParts[^1];
                if (TryFindDecision(decisions, rSchema, rTable, out var fm))
                {
                    mapped = fm;
                }
            }
        }

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

            // Review R4-6: the declared-output filter also applies to cleared columns.
            // Review P-1: declared mode only narrows the result to the declared outputs; the columns are then governed
            // exactly like in catalog mode (consent deny/mask of the result table, unknown columns removed).
            if (definition.ValidationMode == ProcedureValidationMode.Declared &&
                !definition.DeclaredOutputs.Contains(col, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // SEC D-2: with browse-mode source information every result column is governed by the decision of ITS source table.
            // Computed / ambiguous columns (no source) are removed unless declared clear; columns of tables other than the
            // result table survive only with an explicit column-level Clear.
            var (decision, meta, sourceColumn, foreign) = (default(TableAccessDecision)!, default(TableMetadata)!, col, false);
            IReadOnlySet<string>? sensitivityOnly = null;
            if (sources != null)
            {
                if (!sources.TryGetValue(col, out var src) || string.IsNullOrWhiteSpace(src.Table) || string.IsNullOrWhiteSpace(src.Column))
                {
                    // Computed / ambiguous column without source: only survives if declared clear AND result table is not denied
                    if (!cleared.Contains(col) || mapped == null || !mapped.Value.Decision.IsAllowed)
                    {
                        continue;
                    }
                    plan.Add((i, col, ColumnAccessLevel.Clear, null));
                    continue;
                }

                var schema = string.IsNullOrWhiteSpace(src.Schema) ? "dbo" : src.Schema;
                if (!TryFindDecision(decisions, schema, src.Table, out var srcMapped) || !srcMapped.Meta.HasColumn(src.Column))
                {
                    continue; // source table not governed: fail closed
                }

                (decision, meta, sensitivityOnly) = srcMapped;
                sourceColumn = src.Column;
                var key = $"{schema}.{src.Table}";
                foreign = definition.ResultTable == null ||
                          (!string.Equals(key, definition.ResultTable, StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(src.Table, definition.ResultTable, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (mapped == null || !mapped.Value.Meta.HasColumn(col))
                {
                    continue; // unknown column: removed (fail-closed)
                }

                (decision, meta, sensitivityOnly) = mapped.Value;
            }

            // If source table access is denied, fail closed
            if (!decision.IsAllowed)
            {
                continue;
            }

            var level = decision.GetEffectiveColumnAccess(sourceColumn, meta);
            // Zero-Trust (SEC-PROC152-09): ClearedResultColumns lifts only a Mask that stems from catalog sensitivity alone; an explicit
            // base or scope-specific Mask and any Deny stay; an unknown sensitivity set lifts nothing.
            if (cleared.Contains(col) && level == ColumnAccessLevel.Mask && sensitivityOnly != null && sensitivityOnly.Contains(sourceColumn))
            {
                level = ColumnAccessLevel.Clear;
            }

            if (level == ColumnAccessLevel.Deny)
            {
                continue;
            }

            if (foreign && level != ColumnAccessLevel.Clear)
            {
                continue;
            }

            MaskingRule? rule = null;
            if (level == ColumnAccessLevel.Mask)
            {
                var catalogColumn = meta.GetColumn(sourceColumn);
                if (!meta.ColumnMaskingRules.TryGetValue(sourceColumn, out rule) && catalogColumn != null)
                {
                    meta.ColumnMaskingRules.TryGetValue(catalogColumn.ColumnName, out rule);
                }

                rule ??= new MaskingRule { RuleType = "REDACT" };

                // SEC D-3: HMAC pseudonyms are tenant-scoped.
                rule = Autheris.Application.Services.GatewayExecutionService.ScopeRuleForTenant(
                    rule, tenantId.Value, _options.Value.DataMasking?.HmacKeyId);
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

    private Task AuditAsync(
        string eventType,
        string decision,
        ProcedureDefinition definition,
        TenantId tenantId,
        Sid actor,
        object details,
        string traceId,
        CancellationToken ct) =>
        AuditAsync(eventType, decision, definition.ProcedureName, tenantId, actor, details, traceId, ct);

    private async Task AuditAsync(
        string eventType,
        string decision,
        string target,
        TenantId tenantId,
        Sid actor,
        object details,
        string traceId,
        CancellationToken ct)
    {
        await Autheris.Application.Policy.GovernedQueryStages.WriteAsync(
            _auditWriter,
            Autheris.Application.Policy.GovernedQueryStages.BuildEntry(
                eventType,
                tenantId,
                actor,
                target,
                allowed: string.Equals(decision, "ALLOW", StringComparison.Ordinal),
                details,
                traceId: traceId),
            Autheris.Application.Policy.AuditTier.Required,
            ct).ConfigureAwait(false);
    }

    // SEC (Low): no fallback to the caller-influenced token "ip" claim; unknown client -> IPAddress.None (fail closed).
    /// <summary>
    /// Virtual filters also apply with the consent bypass (decision 1), including this path without consent services
    /// where <c>TableAccessPolicy</c> is not used.
    /// </summary>
    private async Task<TableAccessDecision> WithVirtualFiltersAsync(
        TableAccessDecision decision, ClaimsPrincipal user, Sid userSid, TenantId tenantId, TableMetadata meta, CancellationToken ct)
    {
        var mandatory = await (_mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance).ResolveAsync(
            new Autheris.Application.VirtualFilters.MandatoryFilterQuery(userSid, user.GetGroupSids(), user.GetUserRoles(), tenantId, meta, FilterObjectKinds.ProcedureResult),
            ct).ConfigureAwait(false);
        if (mandatory.IsDenied)
        {
            return TableAccessDecision.Denied(decision.Table, mandatory.DenyReason ?? "Denied by virtual filters.");
        }

        return mandatory.PredicateSql != null ? decision.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters, mandatory.AppliedBindings) : decision;
    }

    private System.Net.IPAddress ResolveClientIp(ClaimsPrincipal user)
    {
        return _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None;
    }

    private static bool TryFindDecision(
        Dictionary<string, TableDecision> decisions,
        string schema,
        string table,
        out TableDecision result)
    {
        var keyWithSchema = $"{(string.IsNullOrWhiteSpace(schema) ? "dbo" : schema)}.{table}";
        if (decisions.TryGetValue(keyWithSchema, out result))
        {
            return true;
        }

        if (decisions.TryGetValue(table, out result))
        {
            return true;
        }

        foreach (var kvp in decisions)
        {
            if (string.Equals(kvp.Key, keyWithSchema, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, table, StringComparison.OrdinalIgnoreCase) ||
                (kvp.Value.Meta != null &&
                 string.Equals(kvp.Value.Meta.Identifier.TableName, table, StringComparison.OrdinalIgnoreCase) &&
                 (string.IsNullOrWhiteSpace(schema) || string.Equals(kvp.Value.Meta.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))))
            {
                result = kvp.Value;
                return true;
            }
        }

        result = default;
        return false;
    }
}

/// <summary>Result of the visibility stage; <see cref="Reason"/> is for the server-side audit only.</summary>
internal sealed record ProcedureVisibility(bool Visible, string Reason, Dictionary<string, TableDecision> Decisions)
{
    public static ProcedureVisibility Hidden(string reason) => new(false, reason, new Dictionary<string, TableDecision>(StringComparer.OrdinalIgnoreCase));
}
