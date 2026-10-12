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
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly IProcedureRowScopeResolver? _rowScope;
    private readonly ILogger<GovernedProcedureExecutionService>? _logger;

    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? _mandatoryFilters;

    public GovernedProcedureExecutionService(
        IProcedureRegistry registry,
        IProcedureInvoker invoker,
        IOptions<GatewayOptions> options,
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
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null)
        : this(registry, invoker, options, Autheris.Application.Audit.NullAuditLogRepository.Instance, tableRepository, consentRepository, consentResolution, masking, policyEnforcement, clientIpResolver, logger, rowScope, rebacEvaluator, consentCache, mandatoryFilters)
    {
    }

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
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null)
    {
        _mandatoryFilters = mandatoryFilters;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
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
        var sw = Stopwatch.StartNew();
        bool consentBypassed = _options.Value.IsConsentBypassed;

        // 1. Identity and role gate. SEC (Low): the role/visibility check comes BEFORE the health check, and a caller
        // that may not see the endpoint gets the same 404 as for an unknown name (no existence/health oracle).
        var userSidNullable = user.GetUserSid();
        if (userSidNullable == null && !consentBypassed)
        {
            throw new SecurityException("An authenticated user identity is required.");
        }

        var userSid = userSidNullable ?? new Sid("anonymous");

        if (definition.RequiredRoles.Count > 0 && !HasAnyRole(user, definition.RequiredRoles))
        {
            await AuditAsync("PROCEDURE_DENIED", "DENY", definition, tenantId, userSid, new { reason = "role" }, ct).ConfigureAwait(false);
            throw new KeyNotFoundException($"Procedure endpoint '{name}' is not registered.");
        }

        if (registered.State != ProcedureState.Active || registered.Validation == null)
        {
            throw new ProcedureUnavailableException($"Procedure endpoint '{name}' is currently unavailable.");
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

        if (definition.ValidationMode == ProcedureValidationMode.Declared && tableKeys.Count == 0)
        {
            // Review P-1: without a result table no consent could be evaluated (fail-closed).
            await AuditAsync("PROCEDURE_DENIED", "DENY", definition, tenantId, userSid, new { reason = "no-result-table" }, ct).ConfigureAwait(false);
            throw new SecurityException(DeniedMessage);
        }

        foreach (var key in tableKeys)
        {
            // A consent row filter on the result table is enforced after the call by a key match (RowScopeKey);
            // on every other table it cannot be enforced and denies the call.
            bool allowRowFilter = definition.RowScopeKey.Count > 0 && _rowScope != null && IsResultTable(definition, key);
            var evaluated = await EvaluateTableAsync(definition.CatalogDomain, key, user, userSid, tenantId, consentBypassed, allowRowFilter, ct).ConfigureAwait(false);
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

        // 4b. Row scope: keep only the rows the consent row filter of the result table allows
        int rowsRemovedByScope = 0;
        if (definition.ResultTable != null &&
            decisions.TryGetValue(definition.ResultTable, out var resultDecision) &&
            !string.IsNullOrWhiteSpace(resultDecision.Decision.CombinedRowFilterSql))
        {
            var scoped = await ApplyRowScopeAsync(definition, raw, resultDecision.Decision, resultDecision.Meta, registered.Validation.ResultColumnSources, context, tenantId, userSid, ct).ConfigureAwait(false);
            rowsRemovedByScope = raw.Rows.Count - scoped.Rows.Count;
            raw = scoped;
        }

        // 5. Result-set governance (deny / mask / drop unknown columns)
        var governed = GovernResult(definition, raw, decisions, registered.Validation.ResultColumnSources, tenantId);

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
                rowsRemovedByScope,
                virtual_filters = decisions
                    .Where(d => d.Value.Decision.AppliedVirtualFilters is { Count: > 0 })
                    .ToDictionary(d => d.Key.ToString(), d => d.Value.Decision.AppliedVirtualFilters),
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
    /// in memory (differs from SQL semantics, review E-6); such a consent therefore denies the call (fail-closed),
    /// unless <paramref name="allowRowFilter"/> is set: the result table's filter is then enforced by a database key
    /// match after the call (<see cref="ApplyRowScopeAsync"/>).
    /// </summary>
    internal Task<(TableAccessDecision Decision, TableMetadata Meta)?> EvaluateTableAsync(
        string catalogDomain,
        string tableKey,
        ClaimsPrincipal user,
        Sid userSid,
        TenantId tenantId,
        bool consentBypassed,
        bool allowRowFilter,
        CancellationToken ct)
    {
        if (!TableIdentifier.TryParse(catalogDomain + "." + tableKey, out var tableId))
        {
            return Task.FromResult<(TableAccessDecision Decision, TableMetadata Meta)?>(null);
        }

        return EvaluateTableAsync(tableId, user, userSid, tenantId, consentBypassed, allowRowFilter, ct);
    }

    internal async Task<(TableAccessDecision Decision, TableMetadata Meta)?> EvaluateTableAsync(
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
                _policyEnforcement ?? Autheris.Application.Policy.Services.NullPolicyEnforcementService.Instance,
                _rebacEvaluator ?? Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
                _clientIpResolver,
                _options.Value,
                _mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance)
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

        if (!consentBypassed && _policyEnforcement != null && _policyEnforcement.HasPolicies(tenantId))
        {
            // With ABAC active every catalog column carries an explicit level; columns outside the catalog are denied.
            var explicitLevels = meta.Columns.ToDictionary(c => c.ColumnName, c => decision.GetEffectiveColumnAccess(c.ColumnName, meta), StringComparer.OrdinalIgnoreCase);
            decision = decision with { ColumnAccess = explicitLevels, HasUnconstrainedColumnAllow = false };
        }

        return (decision, meta);
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
    private async Task<RawProcedureResult> ApplyRowScopeAsync(
        ProcedureDefinition definition,
        RawProcedureResult raw,
        TableAccessDecision decision,
        TableMetadata meta,
        IReadOnlyDictionary<string, ResultColumnSource>? resultColumnSources,
        ProcedureSecurityContext security,
        TenantId tenantId,
        Sid userSid,
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
            await AuditAsync("PROCEDURE_DENIED", "DENY", definition, tenantId, userSid, new { reason = "row-scope-key" }, ct).ConfigureAwait(false);
            throw new SecurityException(DeniedMessage);
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
            _logger?.LogError(ex, "Row scope lookup for procedure endpoint '{Endpoint}' failed.", definition.Name);
            await AuditAsync("PROCEDURE_FAILED", "DENY", definition, tenantId, userSid, new { reason = "row-scope-" + ex.GetType().Name }, ct).ConfigureAwait(false);
            throw new SecurityException(DeniedMessage);
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

        return new RawProcedureResult(raw.Columns, kept, raw.Truncated);
    }

    private (IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows) GovernResult(
        ProcedureDefinition definition,
        RawProcedureResult raw,
        Dictionary<string, (TableAccessDecision Decision, TableMetadata Meta)> decisions,
        IReadOnlyDictionary<string, ResultColumnSource>? sources,
        TenantId tenantId)
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

                var key = $"{(string.IsNullOrWhiteSpace(src.Schema) ? "dbo" : src.Schema)}.{src.Table}";
                if (!decisions.TryGetValue(key, out var srcMapped) || !srcMapped.Meta.HasColumn(src.Column))
                {
                    continue; // source table not governed: fail closed
                }

                (decision, meta) = srcMapped;
                sourceColumn = src.Column;
                foreign = definition.ResultTable == null ||
                          !string.Equals(key, definition.ResultTable, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                if (mapped == null || !mapped.Value.Meta.HasColumn(col))
                {
                    continue; // unknown column: removed (fail-closed)
                }

                (decision, meta) = mapped.Value;
            }

            // If source table access is denied, fail closed
            if (!decision.IsAllowed)
            {
                continue;
            }

            var level = decision.GetEffectiveColumnAccess(sourceColumn, meta);
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

    private async Task AuditAsync(
        string eventType,
        string decision,
        ProcedureDefinition definition,
        TenantId tenantId,
        Sid actor,
        object details,
        CancellationToken ct)
    {
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

        return mandatory.PredicateSql != null ? decision.WithMandatoryPredicate(mandatory.PredicateSql, mandatory.AppliedFilters) : decision;
    }

    private System.Net.IPAddress ResolveClientIp(ClaimsPrincipal user)
    {
        return _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None;
    }
}
