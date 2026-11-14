using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Health;

public sealed class GatewayHealthCheckService : IGatewayHealthCheckService
{
    private readonly IGovernanceRepository? _governanceRepository;
    private readonly IConnectionMultiplexer? _redisMultiplexer;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<GatewayHealthCheckService> _logger;
    private readonly IHostEnvironment? _environment;
    private readonly AuditChainIntegrityMonitor? _auditChainMonitor;
    private readonly ICatalogSearchEngine? _catalogSearchEngine;
    private readonly ICatalogVersionProvider? _catalogVersionProvider;
    private readonly IOptions<CatalogSearchOptions>? _catalogSearchOptions;
    private readonly TimeProvider _timeProvider;

    public GatewayHealthCheckService(
        IOptions<GatewayOptions> options,
        ILogger<GatewayHealthCheckService> logger,
        IGovernanceRepository? governanceRepository = null,
        IConnectionMultiplexer? redisMultiplexer = null,
        IHostEnvironment? environment = null,
        AuditChainIntegrityMonitor? auditChainMonitor = null,
        ICatalogSearchEngine? catalogSearchEngine = null,
        ICatalogVersionProvider? catalogVersionProvider = null,
        IOptions<CatalogSearchOptions>? catalogSearchOptions = null,
        TimeProvider? timeProvider = null)
    {
        _auditChainMonitor = auditChainMonitor;
        _options = options;
        _logger = logger;
        _governanceRepository = governanceRepository;
        _redisMultiplexer = redisMultiplexer;
        _environment = environment;
        _catalogSearchEngine = catalogSearchEngine;
        _catalogVersionProvider = catalogVersionProvider;
        _catalogSearchOptions = catalogSearchOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GatewayHealthReport> CheckHealthAsync(CancellationToken ct = default)
    {
        var components = new List<HealthCheckComponentResult>();
        bool overallHealthy = true;

        // 1. Governance Database Check
        bool dbHealthy = false;
        string? dbDesc = null;
        try
        {
            if (_governanceRepository is GovernanceRepository repo)
            {
                var engineName = repo.Dialect is PostgreSqlGovernanceDialect ? "PostgreSQL" : "SQL Server";
                dbHealthy = await repo.PingAsync(ct).ConfigureAwait(false);
                if (repo.IsAuditPipelineFaulted)
                {
                    dbHealthy = false;
                    dbDesc = $"{engineName} Governance DB: Audit pipeline is faulted.";
                }
                else
                {
                    dbDesc = dbHealthy ? $"{engineName} Governance DB connection active." : $"{engineName} query returned null.";
                }
            }
            else if (_governanceRepository != null)
            {
                // Fallback check: verify catalog is queryable
                _ = await _governanceRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
                dbHealthy = true;
                dbDesc = "Governance DB query succeeded.";
            }
            else
            {
                dbHealthy = true;
                dbDesc = "No governance repository registered.";
            }
        }
        catch (Exception ex)
        {
            dbHealthy = false;
            // The exception text (connection strings, host names, SQL) stays in the log; health details are fixed messages.
            dbDesc = "Governance DB query failed (see gateway logs).";
            _logger.LogError(ex, "Governance DB health check failed.");
        }
        components.Add(new HealthCheckComponentResult("GovernanceDb", dbHealthy, dbDesc));
        if (!dbHealthy) overallHealthy = false;

        // Review E-11: result of the periodic audit hash chain verification.
        if (_auditChainMonitor != null)
        {
            bool failReadiness = _options.Value.Audit.FailReadinessOnChainViolation;
            bool chainViolated = _auditChainMonitor.IsViolated;
            bool chainRunFailed = _auditChainMonitor.LastRunFailed;

            // A run that could not complete leaves the chain state unknown: unhealthy, like a violation (fail closed). A violation
            // detected earlier stays reported even when later runs fail.
            bool chainHealthy = !chainViolated && !chainRunFailed;
            string chainDesc = chainViolated
                ? $"Audit hash chain verification FAILED (OnChainViolation policy: {_options.Value.Audit.OnChainViolation?.ToString() ?? "environment default"}; restart after forensic review)."
                : chainRunFailed
                    ? "Last audit hash chain verification could not complete; chain state unknown (see gateway logs)."
                    : _auditChainMonitor.LastCheckedAt == null
                        ? "Audit hash chain not verified yet."
                        : "Audit hash chain verified.";
            components.Add(new HealthCheckComponentResult("AuditChain", chainHealthy, chainDesc));
            if (!chainHealthy && failReadiness) overallHealthy = false;
        }

        // 2. Redis Check (if enabled)
        if (_options.Value.Caching.Redis.Enabled)
        {
            bool redisHealthy = false;
            string? redisDesc = null;
            try
            {
                if (_redisMultiplexer != null && _redisMultiplexer.IsConnected)
                {
                    var ping = await _redisMultiplexer.GetDatabase().PingAsync().WaitAsync(ct).ConfigureAwait(false);
                    redisHealthy = true;
                    redisDesc = $"Redis ping successful ({ping.TotalMilliseconds:F1}ms).";
                }
                else
                {
                    redisHealthy = false;
                    redisDesc = "Redis multiplexer is not connected.";
                }
            }
            catch (Exception ex)
            {
                redisHealthy = false;
                redisDesc = "Redis ping failed (see gateway logs).";
                _logger.LogError(ex, "Redis health check failed.");
            }
            components.Add(new HealthCheckComponentResult("Redis", redisHealthy, redisDesc));
            if (!redisHealthy) overallHealthy = false;
        }

        // 3. Security Invariants Check (H-5)
        // DANGER entries make the component unhealthy (outside Development additionally the whole report, so that
        // /health/ready returns 503 - defense in depth, startup validation already blocks DANGER there).
        // WARN entries keep the component healthy but are reported as "degraded: ...".
        // The names of the active switches are never part of the description (it reaches health and metrics responses and would tell
        // a caller which protections are off); they are logged instead.
        var dangerBypasses = _options.Value.GetActiveDangerBypasses();
        var warnings = _options.Value.GetActiveWarnings();
        bool securityHealthy = dangerBypasses.Count == 0;
        string securityDesc;
        if (!securityHealthy)
        {
            securityDesc = "WARNING: Security bypasses are active (see gateway logs).";
            if (warnings.Count > 0)
            {
                securityDesc += " degraded: security-relevant settings are active (see gateway logs).";
            }
        }
        else if (warnings.Count > 0)
        {
            securityDesc = "degraded: security-relevant settings are active (see gateway logs).";
        }
        else
        {
            securityDesc = "No security bypasses active (Zero-Trust enforced).";
        }

        components.Add(new HealthCheckComponentResult("SecurityConfiguration", securityHealthy, securityDesc));
        if (!securityHealthy)
        {
            _logger.LogWarning("Health check detected active security bypasses: {Bypasses}", string.Join(", ", dangerBypasses));
            if (_environment != null && !_environment.IsDevelopment())
            {
                overallHealthy = false;
            }
        }
        else if (warnings.Count > 0)
        {
            _logger.LogDebug("Health check: security-relevant settings active (permitted): {Warnings}", string.Join(", ", warnings));
        }

        // 4. Catalog Search Index (Issue #458, #501)
        if (_catalogSearchEngine != null)
        {
            var searchDesc = await EvaluateCatalogSearchHealthAsync(ct).ConfigureAwait(false);
            components.Add(new HealthCheckComponentResult("CatalogSearchIndex", true, searchDesc));
        }

        return new GatewayHealthReport(overallHealthy, components);
    }

    private const int CatalogSearchStalenessMultiplier = 3;

    private async Task<string> EvaluateCatalogSearchHealthAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var status = _catalogSearchEngine!.GetStatus(now);
        var lastErrorCode = status?.LastErrorCode;
        var lastIndexed = status?.LastSuccessUtc ?? _catalogSearchEngine.LastIndexedAt;
        var indexAge = status?.IndexAge ?? (lastIndexed.HasValue ? now - lastIndexed.Value : (TimeSpan?)null);

        if (!string.IsNullOrEmpty(lastErrorCode))
        {
            return $"degraded: catalog search index rebuild failed ({lastErrorCode}; see gateway logs).";
        }

        var refreshSeconds = _catalogSearchOptions?.Value.RefreshSeconds ?? 15;
        if (refreshSeconds > 0 &&
            indexAge.HasValue &&
            indexAge.Value > TimeSpan.FromSeconds((long)refreshSeconds * CatalogSearchStalenessMultiplier) &&
            _catalogVersionProvider != null)
        {
            try
            {
                var currentVersion = await _catalogVersionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
                var indexedVersion = (_catalogSearchEngine as ICatalogSearchStatusReader)?.LastIndexedVersion;
                if (indexedVersion is null ||
                    indexedVersion.Value != (currentVersion.StructureVersion, currentVersion.ContentVersion))
                {
                    return "degraded: catalog search index is stale (see gateway logs).";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Health check could not read catalog version for staleness check.");
            }
        }

        var isReady = _catalogSearchEngine.IsIndexReady;
        var tableCount = _catalogSearchEngine.IndexedTableCount;
        return isReady
            ? $"Catalog search index ready: {tableCount} tables indexed at {lastIndexed:O}."
            : "Catalog search index warming or not initialized.";
    }
}
