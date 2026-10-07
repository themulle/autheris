using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Services;

public sealed partial class GatewayExecutionService : IGatewayExecutionService
{
    [GeneratedRegex(@"(?:\[[a-zA-Z0-9_]+\]|[a-zA-Z_][a-zA-Z0-9_]*)\.(\[?[a-zA-Z_][a-zA-Z0-9_]*\]?)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TablePrefixRegex();

    [GeneratedRegex(@"""([a-zA-Z0-9_]+)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DoubleQuotedIdentifierRegex();

    [GeneratedRegex(@"\(\s*([a-zA-Z0-9_, \[\]]+)\s*\)\s+IN\s*\(\s*(\(.*?\))\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TupleInRegex();

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository _consentRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IConsentResolutionService _resolutionService;
    private readonly IConsentCacheService _cacheService;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IChunkedQueryExecutor _chunkedQueryExecutor;
    private readonly GatewayOptions? _options;
    private readonly ITrafficDrainController? _drainController;
    private readonly IEnumerable<IDataSourceExecutor>? _dataSourceExecutors;
    private readonly IPolicyEnforcementService? _policyEnforcementService;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly Autheris.Application.Connectors.IAutherisConnectorRegistry? _connectorRegistry;
    private readonly ITableReadConcurrencyGate? _concurrencyGate;
    private readonly IDataSourceExecutor _defaultSqlExecutor = new SqlDataSourceExecutor();

    /// <summary>O10: Retry-After for a throttled read; the typical duration of a slow read is the query timeout.</summary>
    private const int ThrottledRetryAfterSeconds = 2;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EchoableColumnNameRegex();

    public int LastDispatchedChildQueryCount { get; private set; }

    [ActivatorUtilitiesConstructor]
    public GatewayExecutionService(
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IAuditLogRepository auditLogRepository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IColumnMaskingProvider maskingProvider,
        IChunkedQueryExecutor? chunkedQueryExecutor = null,
        IOptions<GatewayOptions>? options = null,
        ITrafficDrainController? drainController = null,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors = null,
        IPolicyEnforcementService? policyEnforcementService = null,
        IClientIpResolver? clientIpResolver = null,
        Autheris.Application.Connectors.IAutherisConnectorRegistry? connectorRegistry = null,
        ITableReadConcurrencyGate? concurrencyGate = null)
    {
        _metadataRepository = metadataRepository;
        _consentRepository = consentRepository;
        _auditLogRepository = auditLogRepository;
        _resolutionService = resolutionService;
        _cacheService = cacheService;
        _maskingProvider = maskingProvider;
        _chunkedQueryExecutor = chunkedQueryExecutor ?? new ChunkedQueryExecutor(500);
        _options = options?.Value;
        _drainController = drainController;
        _dataSourceExecutors = dataSourceExecutors;
        _policyEnforcementService = policyEnforcementService;
        _clientIpResolver = clientIpResolver;
        _connectorRegistry = connectorRegistry;
        _concurrencyGate = concurrencyGate;
    }

    public GatewayExecutionService(
        IGovernanceRepository repository,
        IConsentResolutionService resolutionService,
        IConsentCacheService cacheService,
        IColumnMaskingProvider maskingProvider,
        IChunkedQueryExecutor? chunkedQueryExecutor = null,
        IOptions<GatewayOptions>? options = null,
        ITrafficDrainController? drainController = null,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors = null,
        IPolicyEnforcementService? policyEnforcementService = null)
        : this(repository, repository, repository, resolutionService, cacheService, maskingProvider, chunkedQueryExecutor, options, drainController, dataSourceExecutors, policyEnforcementService)
    {
    }

    public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first = null,
        int? after = null,
        CancellationToken ct = default)
        => ExecuteTableQueryAsync(principal, table, first, after, null, null, null, ct);

    public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first,
        int? after,
        IReadOnlyDictionary<string, object?>? queryArguments,
        IReadOnlyList<string>? requestedFields = null,
        CancellationToken ct = default)
        => ExecuteTableQueryAsync(principal, table, first, after, queryArguments, requestedFields, null, ct);

    public async Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        int? first,
        int? after,
        IReadOnlyDictionary<string, object?>? queryArguments,
        IReadOnlyList<string>? requestedFields,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        CancellationToken ct = default)
    {
        using var _ = _drainController?.TrackQuery();
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            if (_options?.IsAnonymousAccessAllowed == true)
            {
                var anonSid = new Sid("S-1-5-21-DEV-ANONYMOUS");
                principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.PrimarySid, anonSid.Value),
                    new Claim(ClaimTypes.Name, "DEV_ANONYMOUS"),
                    new Claim(ClaimTypes.Role, "AnonymousUser")
                ], "InsecureAnonymousAuth"));
            }
            else
            {
                throw new GatewayUnauthorizedException("Authentication is required to query tables.");
            }
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            throw new GatewayUnauthorizedException("Keine gültige Benutzer-SID im Authentifizierungstoken vorhanden.");
        }
        var userSid = userSidNullable.Value;

        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();

        // Resolve TenantId upfront for cache and consent isolation
        // RR-L4-02: identical semantics to SecurityContextFactory (single source of truth for HTTP ingress):
        // a tenant header may only select a tenant for canonical cluster admins whose identity was not asserted
        // via ForwardAuth headers, or for the development-only anonymous principal (danger_allow_anonymous_access).
        var tenantId = principal.GetTenantId();
        if (requestHeaders != null &&
            (requestHeaders.TryGetValue("X-Tenant-ID", out var tHeaders) || requestHeaders.TryGetValue("X-Tenant-Id", out tHeaders)) &&
            tHeaders.Length > 0 && TenantId.TryParse(tHeaders[0], out var parsedFromHeader) &&
            !string.Equals(parsedFromHeader.Value, tenantId.Value, StringComparison.OrdinalIgnoreCase))
        {
            var isAnonymousDev = principal.Identity?.IsAuthenticated != true;
            if (isAnonymousDev || Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
            {
                tenantId = parsedFromHeader;
            }
            // Non-admins keep the tenant from their token (header ignored, fail-safe). HTTP ingress already rejects
            // such mismatches with 403 in SecurityContextResolutionMiddleware.
        }

        // Verify table existence in metadata catalog
        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct);
        if (metadata == null)
        {
            throw new TableNotFoundException(table);
        }

        TableAccessDecision decision;
        if (_options?.IsConsentBypassed == true)
        {
            decision = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        }
        else
        {
            // Check Consent Cache (L1/L2 with Epoch Validation & Group/Role Context Hash, strictly tenant-isolated)
            var contextHash = IConsentCacheService.ComputeSubjectContextHash(groupSids, roles);
            var cached = await _cacheService.GetCachedDecisionAsync(tenantId, userSid, table, contextHash, ct);
            if (cached == null)
            {
                // Cache Miss -> Load from Governance DB
                var allSubjects = groupSids.Append(userSid).ToList();
                // RR-L4-06: epoch snapshot BEFORE loading consents (compare-and-set on cache write)
                var epochAtLoad = await _cacheService.GetEpochSnapshotAsync(table, ct);
                var activeConsents = await _consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, table, DateTimeOffset.UtcNow, tenantId, ct);

                // Multi-Tenancy Isolation: Filter active consents strictly for current tenant
                activeConsents = activeConsents
                    .Where(c => c.TenantId == tenantId)
                    .ToList();

                decision = _resolutionService.ResolveAccess(userSid, groupSids, roles, table, activeConsents, metadata.Dialect);

                // Cache decision (SEC: TTL bounded by the earliest consent ValidTo, as in CheckTableAccessAsync)
                var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(metadata.Table.IsHighlySensitive, activeConsents, DateTimeOffset.UtcNow);
                await _cacheService.SetCachedDecisionAsync(tenantId, userSid, table, decision, ttl, contextHash, epochAtLoad, ct);
            }
            else
            {
                decision = cached;
            }
        }

        // Casbin ABAC & Row-Level Security (RLS) Pushdown Evaluation
        if (_policyEnforcementService != null && _policyEnforcementService.HasPolicies(tenantId) && _options?.IsConsentBypassed != true)
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in principal.Claims)
            {
                attributes[claim.Type] = claim.Value;
            }

            // SEC H-4: Fail closed to IPAddress.None; never trust 'ip' claims from tokens or loopback fallback
            var clientIp = _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None;

            var purpose = principal.FindFirst("purpose")?.Value ?? principal.FindFirst("purpose_id")?.Value;

            var secContext = new SecurityEvaluationContext(
                UserSid: userSid,
                GroupSids: groupSids,
                Tenant: tenantId,
                TargetTable: table,
                RequestedColumns: requestedFields ?? metadata.Columns.Select(c => c.ColumnName).ToList(),
                ClientIp: clientIp,
                Timestamp: DateTimeOffset.UtcNow,
                PurposeId: purpose,
                Attributes: attributes,
                TargetDialect: metadata.Dialect
            );

            var casbinDecision = await _policyEnforcementService.EvaluatePolicyAsync(secContext, ct);
            if (!casbinDecision.IsAllowed)
            {
                decision = TableAccessDecision.Denied(table, $"Casbin ABAC Policy Denial: Access denied for subject '{userSid.Value}' in tenant '{tenantId.Value}'.");
            }
            else if (!string.IsNullOrWhiteSpace(casbinDecision.CombinedRowFilterSql))
            {
                var mergedFilter = !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql)
                    ? $"({decision.CombinedRowFilterSql}) AND ({casbinDecision.CombinedRowFilterSql})"
                    : casbinDecision.CombinedRowFilterSql;

                decision = decision with { CombinedRowFilterSql = mergedFilter };
            }
        }

        // Audit evaluation (F-OPS-02: W3C Trace Correlation)
        var traceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId();
        await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = "TABLE_QUERY",
            ActorSid = userSid,
            TargetTable = table.ToString(),
            Decision = decision.IsAllowed ? "ALLOW" : "DENY",
            TraceId = traceId,
            DetailsJson = JsonSerializer.Serialize(new { is_allowed = decision.IsAllowed, reasons = decision.DeniedReasons })
        }, ct);

        // Enforce Access
        if (!decision.IsAllowed)
        {
            throw new GatewayForbiddenException($"Zugriff auf Tabelle '{table}' verweigert: {string.Join("; ", decision.DeniedReasons)}");
        }

        // Generate/Fetch query result via IDataSourceExecutor (SQL, Declarative HTTP, or Plugin)
        var maxRows = _options?.GraphQL?.MaxResponseRows > 0 ? _options.GraphQL.MaxResponseRows : 1000;
        var rowLimit = Math.Clamp(first ?? 50, 1, maxRows);

        var executor = _dataSourceExecutors?.FirstOrDefault(e => e.SupportedType == metadata.Table.DataSourceType)
                       ?? _defaultSqlExecutor;

        var execArgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["limit"] = rowLimit,
            ["offset"] = after ?? 0
        };
        if (queryArguments != null)
        {
            foreach (var (k, v) in queryArguments)
            {
                execArgs[k] = v;
            }
        }

        // Zero-Trust: Never request columns that are already denied by consent policy
        var authorizedColumns = metadata.Columns
            .Where(c => decision.GetColumnAccess(c.ColumnName) != ColumnAccessLevel.Deny)
            .Select(c => c.ColumnName)
            .ToList();

        // O1/O2/O13: requested columns are validated, never dropped silently (a dropped column used to widen the
        // projection to all columns). Unknown and denied columns are rejected with the same message.
        var effectiveRequestedFields = (requestedFields != null && requestedFields.Count > 0)
            ? ResolveRequestedFields(requestedFields, authorizedColumns)
            : authorizedColumns;

        // O10: bound concurrent reads of the same table by the same user (each slow read holds a database worker).
        var maxConcurrentReads = _options?.DataSources?.MaxConcurrentReadsPerUserAndTable ?? 0;
        using var readLease = AcquireReadLease(tenantId, userSid, table, maxConcurrentReads);

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rawRows;
        bool rlsPushdownAlreadyOccurred = false;
        bool inDbMaskingAlreadyOccurred = false;

        Autheris.Application.Connectors.IAutherisConnector? connector = null;
        bool canUseConnector = _connectorRegistry != null &&
            _connectorRegistry.TryGetConnectorForTable(table, out connector) &&
            connector != null &&
            (metadata.DataSourceType == DataSourceType.Sql ||
             (!string.Equals(connector.ConnectorId, "default-sql", StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(connector.ConnectorId, "sql", StringComparison.OrdinalIgnoreCase)));

        if (canUseConnector && connector != null)
        {
            var session = new Autheris.Domain.Connectors.ConnectorSessionContext(
                Principal: principal,
                Tenant: tenantId,
                AccessDecision: decision,
                ProjectedColumns: effectiveRequestedFields,
                Arguments: execArgs,
                PushdownFilterSql: decision.CombinedRowFilterSql,
                Limit: rowLimit,
                Offset: after ?? 0,
                RequestHeaders: requestHeaders);

            session.Items["TableMetadata"] = metadata;

            var splits = await connector.SplitManager.GetSplitsAsync(metadata, session, ct).ConfigureAwait(false);
            if (splits.Count == 0)
            {
                rawRows = Array.Empty<IReadOnlyDictionary<string, object?>>();
            }
            else
            {
                var combinedRows = new List<IReadOnlyDictionary<string, object?>>();
                foreach (var split in splits)
                {
                    var splitRows = await connector.RecordSource.ReadBatchAsync(split, session, ct).ConfigureAwait(false);
                    combinedRows.AddRange(splitRows);
                }
                rawRows = combinedRows;
            }

            rlsPushdownAlreadyOccurred = session.Items.TryGetValue("RlsPushdownExecuted", out var p1) && p1 is true;
            inDbMaskingAlreadyOccurred = session.Items.TryGetValue("InDbColumnMaskingExecuted", out var m1) && m1 is true;
        }
        else
        {
            var execContext = new DataSourceExecutionContext(
                SourceName: metadata.Table.SourceName,
                Metadata: metadata,
                Principal: principal,
                AccessDecision: decision,
                Arguments: execArgs,
                RequestedFields: effectiveRequestedFields,
                RequestHeaders: requestHeaders,
                Limit: rowLimit,
                Offset: after ?? 0,
                Tenant: tenantId
            );

            rawRows = await executor.ExecuteAsync(execContext, ct);

            rlsPushdownAlreadyOccurred = execContext.Items.TryGetValue("RlsPushdownExecuted", out var p2) && p2 is true;
            inDbMaskingAlreadyOccurred = execContext.Items.TryGetValue("InDbColumnMaskingExecuted", out var m2) && m2 is true;
        }

        // Central Zero-Trust Pipeline: Step 1: In-Memory RLS Post-Filtering
        // For SQL data sources where RLS pushdown has already been executed in the DB engine via WHERE clause,
        // redundant in-memory DataTable filtering is skipped.
        // For non-SQL data sources (REST, Plugins) or synthetic dev/test mock fallback without DB pushdown,
        // in-memory evaluation is enforced.
        var filteredRows = rawRows.ToList();

        if (!rlsPushdownAlreadyOccurred && !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            filteredRows = FilterRows(filteredRows, decision.CombinedRowFilterSql, metadata);
        }

        // Central Zero-Trust Pipeline: Step 2: Column Masking & Deny Stripping
        var processedRows = new List<IReadOnlyDictionary<string, object?>>(filteredRows.Count);
        foreach (var r in filteredRows)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in metadata.Columns)
            {
                var access = decision.GetColumnAccess(col.ColumnName);
                if (access == ColumnAccessLevel.Deny)
                {
                    continue; // Strip denied columns completely
                }

                // Zero-Trust Hardening: Sensitive columns in catalog NEVER output cleartext without explicit Clear rule
                bool isSensitiveInCatalog = col.IsSensitive || metadata.ColumnMaskingRules.ContainsKey(col.ColumnName);
                if (isSensitiveInCatalog && !decision.HasExplicitClear(col.ColumnName))
                {
                    access = ColumnAccessLevel.Mask;
                }

                if (r.TryGetValue(col.ColumnName, out var rawVal))
                {
                    if (access == ColumnAccessLevel.Mask && _options?.IsColumnMaskingDisabled != true)
                    {
                        if (!inDbMaskingAlreadyOccurred)
                        {
                            var rule = metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule) ? mRule : new MaskingRule { RuleType = "REDACT" };
                            if (IsHmacRule(rule))
                            {
                                rule = CreateTenantScopedHmacRule(rule, tenantId.Value, _options?.DataMasking?.HmacKeyId);
                            }
                            rawVal = _maskingProvider.MaskValue(col.ColumnName, rawVal, rule);
                        }
                    }
                    dict[col.ColumnName] = rawVal;
                }
                else
                {
                    dict[col.ColumnName] = null;
                }
            }
            processedRows.Add(dict);
        }

        // Central Zero-Trust Pipeline: Step 3: Hard Response Size Cap Enforcement
        var maxBytes = _options?.GraphQL?.MaxResponseBytes > 0 ? _options.GraphQL.MaxResponseBytes : 10 * 1024 * 1024;
        long estimatedBytes = 0;

        foreach (var row in processedRows)
        {
            foreach (var (key, val) in row)
            {
                estimatedBytes += key.Length * 2;
                if (val is string s)
                {
                    estimatedBytes += s.Length * 2;
                }
                else if (val is byte[] b)
                {
                    estimatedBytes += b.Length;
                }
                else if (val != null)
                {
                    estimatedBytes += 16;
                }
            }
        }

        if (estimatedBytes > maxBytes)
        {
            throw new GatewaySecurityException($"Antwortgröße ({estimatedBytes} Bytes) überschreitet das konfigurierte Limit von {maxBytes} Bytes.", "RESPONSE_TOO_LARGE");
        }

        return (processedRows, decision);
    }

    /// <summary>
    /// O1/O2/O13: maps requested columns to their catalog spelling (deduplicated, request order). Throws for any column
    /// that is unknown or denied; the message is identical for both cases.
    /// </summary>
    private static List<string> ResolveRequestedFields(IReadOnlyList<string> requestedFields, IReadOnlyList<string> authorizedColumns)
    {
        var resolved = new List<string>(requestedFields.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requested in requestedFields)
        {
            var catalogName = authorizedColumns.FirstOrDefault(c => string.Equals(c, requested?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (catalogName == null)
            {
                var echo = requested != null && EchoableColumnNameRegex().IsMatch(requested.Trim()) ? requested.Trim() : "(invalid name)";
                throw new GatewayInvalidQueryException($"The property '{echo}' does not exist or is not accessible.");
            }
            if (seen.Add(catalogName))
            {
                resolved.Add(catalogName);
            }
        }
        return resolved;
    }

    private IDisposable? AcquireReadLease(TenantId tenantId, Sid userSid, TableIdentifier table, int maxConcurrentReads)
    {
        if (_concurrencyGate == null || maxConcurrentReads <= 0)
        {
            return null;
        }

        var key = $"{tenantId.Value}|{userSid.Value}|{table.ToQualifiedName()}".ToLowerInvariant();
        return _concurrencyGate.TryEnter(key, maxConcurrentReads)
               ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
    }

    public static List<IReadOnlyDictionary<string, object?>> FilterRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        string rowFilterSql,
        TableMetadata metadata)
    {
        if (rows.Count == 0 || string.IsNullOrWhiteSpace(rowFilterSql))
        {
            return rows;
        }

        var normalizedSql = NormalizeRowFilterForInMemoryEvaluation(rowFilterSql);
        if (string.IsNullOrWhiteSpace(normalizedSql))
        {
            // Zero Trust: When a row filter is defined but cannot be safely evaluated in-memory, fail closed
            return new List<IReadOnlyDictionary<string, object?>>();
        }

        try
        {
            // Review E-6: SQL compares ordinally on the governed sources; DataTable defaults to case-insensitive.
            using var dt = new System.Data.DataTable { CaseSensitive = true };

            // Review E-6: a row that lacks a column the filter refers to must not count as NULL (IS NULL would match).
            var referencedColumns = metadata.Columns
                .Select(c => c.ColumnName)
                .Where(n => System.Text.RegularExpressions.Regex.IsMatch(
                    normalizedSql, @"(?<![A-Za-z0-9_])\[?" + System.Text.RegularExpressions.Regex.Escape(n) + @"\]?(?![A-Za-z0-9_])",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                .ToList();

            foreach (var col in metadata.Columns)
            {
                Type colType = col.DataType.ToLowerInvariant() switch
                {
                    var d when d.Contains("bigint") || d.Contains("long") => typeof(long),
                    var d when d.Contains("int") => typeof(int),
                    var d when d.Contains("decimal") || d.Contains("numeric") || d.Contains("money") => typeof(decimal),
                    var d when d.Contains("float") || d.Contains("double") || d.Contains("real") => typeof(double),
                    var d when d.Contains("bool") => typeof(bool),
                    var d when d.Contains("date") || d.Contains("time") => typeof(DateTime),
                    _ => typeof(string)
                };
                dt.Columns.Add(col.ColumnName, colType);
            }

            // Review E-5/E-6: DataTable.Select coerces ('007' = 7 on an int column, 7 = '7' on a string column) and knows
            // LIKE wildcards SQL does not. Filters that rely on either are refused (fail-closed).
            if (!IsTypeStrictRowFilter(normalizedSql, dt))
            {
                return new List<IReadOnlyDictionary<string, object?>>();
            }

            var rowMap = new Dictionary<System.Data.DataRow, IReadOnlyDictionary<string, object?>>();
            foreach (var r in rows)
            {
                if (referencedColumns.Any(n => !r.ContainsKey(n)))
                {
                    continue; // fail-closed: the filter cannot be evaluated for this row
                }

                var dr = dt.NewRow();
                foreach (var col in metadata.Columns)
                {
                    if (r.TryGetValue(col.ColumnName, out var v) && v != null)
                    {
                        if (v is DateTimeOffset dto)
                        {
                            dr[col.ColumnName] = dto.UtcDateTime;
                        }
                        else
                        {
                            dr[col.ColumnName] = v;
                        }
                    }
                    else
                    {
                        dr[col.ColumnName] = DBNull.Value;
                    }
                }
                dt.Rows.Add(dr);
                rowMap[dr] = r;
            }

            var matchedDataRows = dt.Select(normalizedSql);
            return matchedDataRows.Select(dr => rowMap[dr]).ToList();
        }
        catch
        {
            // Strict Fail-Closed if expression cannot be evaluated
            return new List<IReadOnlyDictionary<string, object?>>();
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<col>\[[^\]]+\]|""[^""]+""|\b[A-Za-z_][A-Za-z0-9_]*\b)\s*(?:NOT\s+)?(?<op>=|<>|!=|<=|>=|<|>|\bIN\b|\bLIKE\b)\s*(?<rhs>\((?:[^()']|'(?:[^']|'')*')*\)|'(?:[^']|'')*'|-?\d+(?:\.\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex ColumnVsLiteralRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"'(?:[^']|'')*'|-?\d+(?:\.\d+)?", System.Text.RegularExpressions.RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex LiteralTokenRegex();

    /// <summary>
    /// Review E-5/E-6: true when every comparison of a column with a literal uses a literal of the column's own type
    /// (quoted for text columns, unquoted number for numeric columns) and no LIKE pattern uses DataTable-only wildcards.
    /// </summary>
    internal static bool IsTypeStrictRowFilter(string normalizedSql, System.Data.DataTable table)
    {
        foreach (System.Text.RegularExpressions.Match m in ColumnVsLiteralRegex().Matches(normalizedSql))
        {
            var colName = m.Groups["col"].Value.Trim('[', ']', '"');
            if (!table.Columns.Contains(colName))
            {
                continue;
            }

            var colType = table.Columns[colName]!.DataType;
            bool isLike = m.Groups["op"].Value.Equals("LIKE", StringComparison.OrdinalIgnoreCase);
            bool numericColumn = colType == typeof(long) || colType == typeof(int) || colType == typeof(decimal) || colType == typeof(double);
            bool textColumn = colType == typeof(string);

            foreach (System.Text.RegularExpressions.Match lit in LiteralTokenRegex().Matches(m.Groups["rhs"].Value))
            {
                bool quoted = lit.Value[0] == '\'';
                if (isLike)
                {
                    // DataTable treats '*' and '[' as wildcards/escapes; in SQL they are plain characters.
                    if (!textColumn || !quoted || lit.Value.Contains('*') || lit.Value.Contains('['))
                    {
                        return false;
                    }

                    continue;
                }

                if ((numericColumn && quoted) || (textColumn && !quoted))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? NormalizeRowFilterForInMemoryEvaluation(string rowFilterSql)
    {
        if (string.IsNullOrWhiteSpace(rowFilterSql))
        {
            return null;
        }

        var trimmed = rowFilterSql.Trim();
        var trimmedUpper = trimmed.Trim('(', ')', ' ');

        // 1. Subqueries like EXISTS (...) cannot be evaluated against mock in-memory DataTables
        if (trimmedUpper.StartsWith("EXISTS", StringComparison.OrdinalIgnoreCase) ||
            trimmedUpper.StartsWith("NOT EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Protect string literals from regex mangling (e.g. 'john.doe@x.de' matching table.column regex)
        var literals = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inStr = false;
        var curLit = new System.Text.StringBuilder();

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '\'')
            {
                if (inStr)
                {
                    // Check for escaped quote ''
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == '\'')
                    {
                        curLit.Append("''");
                        i++;
                        continue;
                    }
                    inStr = false;
                    curLit.Append('\'');
                    var placeholder = $"__STR_LIT_{literals.Count}__";
                    literals.Add(curLit.ToString());
                    curLit.Clear();
                    sb.Append(placeholder);
                }
                else
                {
                    inStr = true;
                    curLit.Append('\'');
                }
            }
            else if (inStr)
            {
                curLit.Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }

        if (inStr)
        {
            // Unterminated string literal -> fail closed
            return null;
        }

        var normalized = sb.ToString();

        // 2. Strip table prefixes: [alias].[col] -> [col] or alias.col -> col
        normalized = TablePrefixRegex().Replace(normalized, "$1");

        // 3. Normalize double-quoted identifiers: "col" -> [col]
        normalized = DoubleQuotedIdentifierRegex().Replace(normalized, "[$1]");

        // 4. Expand Tuple-IN clauses: (col1, col2) IN ((v1, v2), (v3, v4)) -> ((([col1] = v1) AND ([col2] = v2)) OR ...)
        var tupleInRegex = TupleInRegex();

        while (true)
        {
            var match = tupleInRegex.Match(normalized);
            if (!match.Success)
            {
                break;
            }

            var colNames = match.Groups[1].Value
                .Split(',')
                .Select(c => c.Trim().Trim('[', ']'))
                .ToArray();

            var tuples = ParseTuples(match.Groups[2].Value);
            string replacement;

            if (tuples.Count == 0)
            {
                replacement = "(1 = 0)";
            }
            else
            {
                var orClauses = new List<string>();
                foreach (var tuple in tuples)
                {
                    var andClauses = new List<string>();
                    for (int i = 0; i < colNames.Length && i < tuple.Count; i++)
                    {
                        andClauses.Add($"([{colNames[i]}] = {tuple[i]})");
                    }
                    orClauses.Add($"({string.Join(" AND ", andClauses)})");
                }
                replacement = $"({string.Join(" OR ", orClauses)})";
            }

            normalized = normalized.Remove(match.Index, match.Length).Insert(match.Index, replacement);
        }

        // Restore string literals
        for (int i = 0; i < literals.Count; i++)
        {
            normalized = normalized.Replace($"__STR_LIT_{i}__", literals[i]);
        }

        return normalized;
    }

    private static List<List<string>> ParseTuples(string tupleString)
    {
        var tuples = new List<List<string>>();
        var currentTuple = new List<string>();
        var currentVal = new System.Text.StringBuilder();
        bool inQuote = false;
        bool inTuple = false;

        for (int i = 0; i < tupleString.Length; i++)
        {
            char c = tupleString[i];

            if (c == '\'' && (i == 0 || tupleString[i - 1] != '\\'))
            {
                inQuote = !inQuote;
                currentVal.Append(c);
            }
            else if (!inQuote && c == '(')
            {
                inTuple = true;
                currentTuple = new List<string>();
                currentVal.Clear();
            }
            else if (!inQuote && c == ')')
            {
                if (inTuple)
                {
                    var trimmed = currentVal.ToString().Trim();
                    if (trimmed.Length > 0)
                    {
                        currentTuple.Add(trimmed);
                    }
                    tuples.Add(currentTuple);
                    inTuple = false;
                    currentVal.Clear();
                }
            }
            else if (!inQuote && c == ',' && inTuple)
            {
                var trimmed = currentVal.ToString().Trim();
                currentTuple.Add(trimmed);
                currentVal.Clear();
            }
            else if (inTuple)
            {
                currentVal.Append(c);
            }
        }

        return tuples;
    }

    public async Task<TableAccessDecision> CheckTableAccessAsync(
        ClaimsPrincipal? principal,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        using var _ = _drainController?.TrackQuery();
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            return TableAccessDecision.Denied(table, "Authentication required.");
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            return TableAccessDecision.Denied(table, "Valid user SID required.");
        }
        var userSid = userSidNullable.Value;

        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();

        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct);
        if (metadata == null)
        {
            return TableAccessDecision.Denied(table, $"Table '{table}' not found in metadata catalog.");
        }

        var tenantId = principal.GetTenantId();

        var contextHash = IConsentCacheService.ComputeSubjectContextHash(groupSids, roles);
        var decision = await _cacheService.GetCachedDecisionAsync(tenantId, userSid, table, contextHash, ct);
        if (decision == null)
        {
            var allSubjects = groupSids.Append(userSid).ToList();
            // RR-L4-06: epoch snapshot BEFORE loading consents (compare-and-set on cache write)
            var epochAtLoad = await _cacheService.GetEpochSnapshotAsync(table, ct);
            var activeConsents = await _consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, table, DateTimeOffset.UtcNow, tenantId, ct);

            activeConsents = activeConsents
                .Where(c => c.TenantId == tenantId)
                .ToList();

            decision = _resolutionService.ResolveAccess(userSid, groupSids, roles, table, activeConsents, metadata.Dialect);

            var ttl = metadata.Table.IsHighlySensitive
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromMinutes(10);

            var now = DateTimeOffset.UtcNow;
            if (activeConsents.Count > 0)
            {
                var earliestExpiry = activeConsents
                    .Where(c => c.ValidTo > now)
                    .Select(c => c.ValidTo - now)
                    .DefaultIfEmpty(ttl)
                    .Min();

                if (earliestExpiry < ttl)
                {
                    ttl = earliestExpiry > TimeSpan.FromSeconds(1) ? earliestExpiry : TimeSpan.FromSeconds(1);
                }
            }

            await _cacheService.SetCachedDecisionAsync(tenantId, userSid, table, decision, ttl, contextHash, epochAtLoad, ct);
        }

        // Casbin ABAC & Row-Level Security (RLS) Pushdown Evaluation for Child/Relation Access
        if (_policyEnforcementService != null && _policyEnforcementService.HasPolicies(tenantId) && _options?.IsConsentBypassed != true)
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in principal.Claims)
            {
                attributes[claim.Type] = claim.Value;
            }

            // SEC H-4: Fail closed to IPAddress.None; never trust 'ip' claims from tokens or loopback fallback
            var clientIp = _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None;

            var purpose = principal.FindFirst("purpose")?.Value ?? principal.FindFirst("purpose_id")?.Value;

            var secContext = new SecurityEvaluationContext(
                UserSid: userSid,
                GroupSids: groupSids,
                Tenant: tenantId,
                TargetTable: table,
                RequestedColumns: metadata.Columns.Select(c => c.ColumnName).ToList(),
                ClientIp: clientIp,
                Timestamp: DateTimeOffset.UtcNow,
                PurposeId: purpose,
                Attributes: attributes,
                TargetDialect: metadata.Dialect
            );

            var casbinDecision = await _policyEnforcementService.EvaluatePolicyAsync(secContext, ct);
            if (!casbinDecision.IsAllowed)
            {
                decision = TableAccessDecision.Denied(table, $"Casbin ABAC Policy Denial: Access denied for subject '{userSid.Value}' in tenant '{tenantId.Value}'.");
            }
            else if (!string.IsNullOrWhiteSpace(casbinDecision.CombinedRowFilterSql))
            {
                var mergedFilter = !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql)
                    ? $"({decision.CombinedRowFilterSql}) AND ({casbinDecision.CombinedRowFilterSql})"
                    : casbinDecision.CombinedRowFilterSql;

                decision = decision with { CombinedRowFilterSql = mergedFilter };
            }
        }

        // F-OPS-02: W3C Trace Correlation
        var traceId = Autheris.Application.Common.TraceContextResolver.GetCurrentTraceId();
        await _auditLogRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = "CHILD_RELATION_CHECK",
            ActorSid = userSid,
            TargetTable = table.ToString(),
            Decision = decision.IsAllowed ? "ALLOW" : "DENY",
            TraceId = traceId,
            DetailsJson = JsonSerializer.Serialize(new { is_allowed = decision.IsAllowed, reasons = decision.DeniedReasons })
        }, ct);

        return decision;
    }

    public async Task<IReadOnlyDictionary<string, List<InvoiceItemRecord>>> LoadInvoiceItemsBatchAsync(
        ClaimsPrincipal? principal,
        IReadOnlyList<string> invoiceIds,
        CancellationToken ct = default)
    {
        using var _ = _drainController?.TrackQuery();
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var decision = await CheckTableAccessAsync(principal, childTableId, ct);
        if (!decision.IsAllowed)
        {
            LastDispatchedChildQueryCount = 0;
            return new Dictionary<string, List<InvoiceItemRecord>>();
        }

        var metadata = await _metadataRepository.GetTableMetadataAsync(childTableId, ct);

        // Sonderfall: Wenn IN-Liste aufgrund vieler IDs zu lang wird (RDBMS Parameter-/Puffer-Limit),
        // teilen wir die Abfrage in mehrere parametrisierte Teilabfragen auf und aggregieren die Ergebnisse.
        var (result, dispatchedQueries) = await _chunkedQueryExecutor.ExecuteGroupedWithMetricsAsync(
            invoiceIds,
            (chunkKeys, _) =>
            {
                var chunkResult = new Dictionary<string, List<InvoiceItemRecord>>(chunkKeys.Count);

                foreach (var invId in chunkKeys)
                {
                    var items = new List<InvoiceItemRecord>();
                    // SEC (Low): effective column access (catalog sensitivity included), tenant-scoped HMAC, row filter on RAW values
                    // (before masking) so a masked value can neither satisfy nor defeat the predicate.
                    var effectiveMeta = metadata ?? new TableMetadata { Identifier = childTableId };
                    var tenantValue = principal.GetTenantId().Value;
                    var hmacDefault = _options?.DataMasking?.HmacKeyId;

                    for (int i = 1; i <= 2; i++)
                    {
                        var rawNote = $"Confidential spec for item {i} of invoice {invId}";
                        var rawProduct = $"Enterprise License Pack {i}";
                        var rawPrice = 1250.00m * i;

                        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
                        {
                            var rawRow = (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["id"] = $"{invId}-ITEM-{i}",
                                ["invoice_id"] = invId,
                                ["product_name"] = rawProduct,
                                ["price"] = rawPrice,
                                ["sensitive_note"] = rawNote
                            };

                            if (FilterRows(new List<IReadOnlyDictionary<string, object?>> { rawRow }, decision.CombinedRowFilterSql, effectiveMeta).Count == 0)
                            {
                                continue;
                            }
                        }

                        object? maskedNote = rawNote;
                        var noteAccess = decision.GetEffectiveColumnAccess("sensitive_note", effectiveMeta);

                        if (noteAccess == ColumnAccessLevel.Deny)
                        {
                            maskedNote = null;
                        }
                        else if (noteAccess == ColumnAccessLevel.Mask)
                        {
                            var rule = metadata != null && metadata.ColumnMaskingRules.TryGetValue("sensitive_note", out var r)
                                ? r
                                : new MaskingRule { RuleType = "REDACT" };
                            rule = ScopeRuleForTenant(rule, tenantValue, hmacDefault);
                            maskedNote = _maskingProvider.MaskValue("sensitive_note", rawNote, rule);
                        }

                        var prodAccess = decision.GetEffectiveColumnAccess("product_name", effectiveMeta);
                        string? prodName = prodAccess switch
                        {
                            ColumnAccessLevel.Clear => rawProduct,
                            ColumnAccessLevel.Mask => "***",
                            _ => null
                        };

                        var priceAccess = decision.GetEffectiveColumnAccess("price", effectiveMeta);
                        decimal price = priceAccess switch
                        {
                            ColumnAccessLevel.Clear => rawPrice,
                            _ => 0m
                        };

                        items.Add(new InvoiceItemRecord
                        {
                            Id = $"{invId}-ITEM-{i}",
                            InvoiceId = invId,
                            ProductName = prodName,
                            Price = price,
                            SensitiveNote = maskedNote?.ToString()
                        });
                    }

                    chunkResult[invId] = items;
                }

                return Task.FromResult<IReadOnlyDictionary<string, List<InvoiceItemRecord>>>(chunkResult);
            },
            chunkSize: _options?.GraphQL?.MaxInClauseBatchSize,
            ct: ct);

        LastDispatchedChildQueryCount = dispatchedQueries;
        return result;
    }

    internal static bool IsHmacRule(MaskingRule rule) =>
        rule.RuleType?.ToUpperInvariant() is "HMAC" or "HMAC_SHA256" or "HASH";

    /// <summary>
    /// SEC D-3: returns a tenant-scoped copy of HMAC rules and the rule itself for every other rule type.
    /// Idempotent: a rule that is already tenant-scoped is never scoped twice.
    /// </summary>
    internal static MaskingRule ScopeRuleForTenant(MaskingRule rule, string? tenant, string? defaultKeyId) =>
        !string.IsNullOrWhiteSpace(tenant) && IsHmacRule(rule) ? CreateTenantScopedHmacRule(rule, tenant, defaultKeyId) : rule;

    internal static MaskingRule CreateTenantScopedHmacRule(MaskingRule rule, string tenant, string? defaultKeyId)
    {
        if (rule.HmacKeyId != null && rule.HmacKeyId.Contains("|tenant:", StringComparison.Ordinal))
        {
            return rule;
        }

        var baseKeyId = !string.IsNullOrWhiteSpace(rule.HmacKeyId) ? rule.HmacKeyId : (defaultKeyId ?? "default");
        return new MaskingRule
        {
            Id = rule.Id,
            TableColumnId = rule.TableColumnId,
            RuleType = "HMAC_SHA256",
            PatternOrFormat = rule.PatternOrFormat,
            Replacement = rule.Replacement,
            HmacKeyId = $"{baseKeyId}|tenant:{tenant}"
        };
    }
}
