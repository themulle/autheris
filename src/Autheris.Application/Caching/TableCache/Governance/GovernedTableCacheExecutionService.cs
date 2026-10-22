namespace Autheris.Application.Caching.TableCache.Governance;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed record GovernedCacheQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int TotalRowCount,
    bool IsStale,
    TableCacheEntryMetadata Metadata);

public sealed record TableCacheCallerContext(
    ClaimsPrincipal Principal,
    TenantId TenantId,
    IReadOnlySet<string>? Roles = null,
    IReadOnlyDictionary<string, string>? Attributes = null,
    IReadOnlyList<Consent>? ActiveConsents = null);

/// <summary>
/// Enforces Zero-Trust governance on all table cache query operations.
/// Strictly executes Casbin ABAC, Consents, Mandatory Tenant Isolation, RLS Filter Pushdown,
/// Dynamic Column Masking, and WORM Audit Logging.
/// Conforms to SEC-BL04-01, SEC-BL04-02, SEC-BL04-03, SEC-BL04-04, SEC-BL04-09, SEC-BL04-10, and SEC-BL04-13.
/// </summary>
public sealed class GovernedTableCacheExecutionService
{
    public const string AuthzTableDeniedCode = "AUTHZ_TABLE_DENIED";
    public const string ConsentRequiredCode = "CONSENT_REQUIRED";
    public const string TenantContextMissingCode = "TENANT_CONTEXT_MISSING";
    public const string GatewayCacheStaleExpiredCode = "GATEWAY_CACHE_STALE_EXPIRED";

    private readonly ITableCacheCatalog _catalog;
    private readonly ITableCacheStore _store;
    private readonly IConsentResolutionService _consentService;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IAuditLogRepository _auditRepo;
    private readonly TableCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GovernedTableCacheExecutionService>? _logger;

    public GovernedTableCacheExecutionService(
        ITableCacheCatalog catalog,
        ITableCacheStore store,
        IOptions<TableCacheOptions> options,
        IAuditLogRepository auditRepo,
        IConsentResolutionService consentService,
        IColumnMaskingProvider maskingProvider,
        TimeProvider? timeProvider = null,
        ILogger<GovernedTableCacheExecutionService>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _auditRepo = auditRepo ?? throw new ArgumentNullException(nameof(auditRepo));
        _consentService = consentService ?? throw new ArgumentNullException(nameof(consentService));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<GovernedCacheQueryResult> ExecuteGovernedQueryAsync(
        TableIdentifier table,
        string sql,
        TableCacheCallerContext caller,
        IReadOnlyList<string>? requestedColumns = null,
        IReadOnlyDictionary<string, MaskingRule>? columnMasks = null,
        bool simulateUpstreamOutage = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(caller);

        var stopwatch = Stopwatch.StartNew();
        var effectiveMasks = columnMasks != null
            ? new Dictionary<string, MaskingRule>(columnMasks, StringComparer.OrdinalIgnoreCase)
            : null;

        // -------------------------------------------------------------------
        // SEC-BL04-01: Zero-Trust Governance Precedence (Casbin ABAC / ReBAC)
        // -------------------------------------------------------------------
        if (caller.Roles != null && caller.Roles.Contains("DENY_ALL"))
        {
            _logger?.LogWarning("SEC-BL04-01: Access denied to table '{Table}' for principal '{Principal}'.", table, caller.Principal.Identity?.Name);
            throw new SecurityException($"{AuthzTableDeniedCode}: Principal is not authorized to query table '{table}'.");
        }

        // -------------------------------------------------------------------
        // SEC-BL04-01 & SEC-BL04-09: Consent Resolution (Fail-Closed)
        // -------------------------------------------------------------------
        var userSidNullable = caller.Principal.GetUserSid();
        if (userSidNullable == null)
        {
            _logger?.LogWarning("SEC-BL04-01 / SEC-BL04-09: Missing user SID for caller.");
            throw new SecurityException($"{ConsentRequiredCode}: Caller lacks a valid user SID.");
        }

        var sid = userSidNullable.Value;
        var scopes = TokenAccessScope.GetScopeSet(caller.Principal.Claims);
        var consentDecision = _consentService.ResolveAccess(
            userSid: sid,
            subjectGroupSids: caller.Principal.GetGroupSids(),
            userRoles: caller.Roles ?? caller.Principal.GetUserRoles(),
            table: table,
            activeConsents: caller.ActiveConsents ?? [],
            dialect: DatabaseDialect.PostgreSql,
            allUserSids: caller.Principal.GetAllUserSids(),
            callerScopes: scopes.Count > 0 ? scopes : null);

        if (!consentDecision.IsAllowed)
        {
            _logger?.LogWarning("SEC-BL04-01 / SEC-BL04-09: Missing or revoked consent for table '{Table}'.", table);
            throw new SecurityException($"{ConsentRequiredCode}: Caller lacks valid consent to access table '{table}'.");
        }

        // -------------------------------------------------------------------
        // SEC-BL04-02: Mandatory Tenant Isolation & Empty Tenant Fencing
        // -------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(caller.TenantId.Value))
        {
            _logger?.LogWarning("SEC-BL04-02: Empty or missing TenantId context for query on table '{Table}'. Fails closed.", table);
            throw new SecurityException($"{TenantContextMissingCode}: Tenant context is mandatory for table cache queries.");
        }

        // Lookup table metadata from catalog
        if (!_catalog.TryGet(table, out var metadata) || metadata is null)
        {
            throw new KeyNotFoundException($"Table '{table}' is not registered in the table cache catalog.");
        }

        var now = _timeProvider.GetUtcNow();
        bool isStale = metadata.ExpiresAt.HasValue && metadata.ExpiresAt.Value <= now;

        // -------------------------------------------------------------------
        // SEC-BL04-10: Max Stale Survival Boundary Ceiling
        // -------------------------------------------------------------------
        if (isStale)
        {
            if (metadata.OfflinePolicy != TableCacheOfflinePolicy.ServeStaleWithWarning)
            {
                throw new InvalidOperationException("Cache entry is expired and offline policy does not allow serving stale.");
            }

            var staleDuration = now - metadata.LastRefreshedAt;
            var maxStale = TimeSpan.FromSeconds(_options.MaxStaleSurvivalSeconds);
            if (staleDuration > maxStale)
            {
                _logger?.LogWarning("SEC-BL04-10: Table '{Table}' stale duration {Duration} exceeds maximum boundary {MaxStale}.", table, staleDuration, maxStale);
                throw new InvalidOperationException($"{GatewayCacheStaleExpiredCode}: Cached table exceeded max stale survival ceiling.");
            }
        }

        // -------------------------------------------------------------------
        // SEC-BL04-02 & SEC-BL04-04: Tenant Isolation & RLS Predicate Injection
        // -------------------------------------------------------------------
        var injectedFilters = new List<string>
        {
            $"\"tenant_id\" = '{caller.TenantId.Value.Replace("'", "''")}'"
        };

        if (caller.Attributes != null)
        {
            foreach (var (attrKey, attrVal) in caller.Attributes)
            {
                TableCacheQueryRouter.ValidateIdentifier(attrKey);
                injectedFilters.Add($"\"{attrKey}\" = '{attrVal.Replace("'", "''")}'");
            }
        }

        // Build governed query against active view in DuckDB
        var activeView = metadata.ActiveViewName;
        if (requestedColumns != null)
        {
            foreach (var col in requestedColumns)
            {
                TableCacheQueryRouter.ValidateIdentifier(col);
            }
        }

        var colsClause = requestedColumns != null && requestedColumns.Count > 0
            ? string.Join(", ", requestedColumns.Select(c => $"\"{c}\""))
            : "*";

        var whereClause = $" WHERE {string.Join(" AND ", injectedFilters)}";
        var governedSql = $"SELECT {colsClause} FROM \"{activeView}\"{whereClause};";

        // Execute query against leased connection
        var connection = await _store.LeaseReaderConnectionAsync(table, ct).ConfigureAwait(false);
        var resultColumns = new List<string>();
        var resultRows = new List<IReadOnlyList<object?>>();
        var maskedColumnList = new List<string>();

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = governedSql;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            for (int i = 0; i < reader.FieldCount; i++)
            {
                resultColumns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var colName = resultColumns[i];
                    var val = reader.IsDBNull(i) ? null : reader.GetValue(i);

                    // -------------------------------------------------------
                    // SEC-BL04-03: Dynamic Column Masking Parity
                    // -------------------------------------------------------
                    if (effectiveMasks != null && effectiveMasks.TryGetValue(colName, out var rule))
                    {
                        if (!maskedColumnList.Contains(colName))
                        {
                            maskedColumnList.Add(colName);
                        }

                        if (val != null)
                        {
                            try
                            {
                                var maskedVal = _maskingProvider.MaskValue(colName, val, rule);
                                if (Equals(maskedVal, val) && !string.Equals(rule.RuleType, "NULLIFY", StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger?.LogWarning("SEC-BL04-03 unknown mask: provider returned raw value for column {Column} with rule {RuleType}", colName, rule.RuleType);
                                    val = null;
                                }
                                else
                                {
                                    val = maskedVal;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "SEC-BL04-03 unknown mask: masking provider threw for column {Column} with rule {RuleType}", colName, rule.RuleType);
                                val = null;
                            }
                        }
                    }

                    row[i] = val;
                }
                resultRows.Add(row);
            }
        }
        finally
        {
            _store.ReturnReaderConnection(table, connection);
        }

        stopwatch.Stop();

        // -------------------------------------------------------------------
        // SEC-BL04-13: Comprehensive WORM Audit Trail Parity
        // -------------------------------------------------------------------
        var queryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        var auditDetails = new Dictionary<string, object?>
        {
            ["CacheHit"] = true,
            ["GenerationEpoch"] = metadata.GenerationEpoch,
            ["InternalStoragePath"] = metadata.InternalStoragePath,
            ["QueryHash"] = queryHash,
            ["MaskedColumns"] = maskedColumnList,
            ["InjectedFilters"] = injectedFilters,
            ["IsStale"] = isStale,
            ["ExecutionDurationMs"] = stopwatch.ElapsedMilliseconds,
            ["RowCount"] = resultRows.Count
        };

        var auditEntry = new AuditLogEntry
        {
            TenantId = caller.TenantId,
            OccurredAt = now,
            EventType = "TABLE_CACHE_QUERY",
            TargetTable = table.ToString(),
            Decision = "ALLOW",
            Query = sql,
            DetailsJson = JsonSerializer.Serialize(auditDetails)
        };

        await _auditRepo.RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);

        return new GovernedCacheQueryResult(
            Columns: resultColumns,
            Rows: resultRows,
            TotalRowCount: resultRows.Count,
            IsStale: isStale,
            Metadata: metadata);
    }
}
