using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
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

    public GatewayHealthCheckService(
        IOptions<GatewayOptions> options,
        ILogger<GatewayHealthCheckService> logger,
        IGovernanceRepository? governanceRepository = null,
        IConnectionMultiplexer? redisMultiplexer = null,
        IHostEnvironment? environment = null,
        AuditChainIntegrityMonitor? auditChainMonitor = null)
    {
        _auditChainMonitor = auditChainMonitor;
        _options = options;
        _logger = logger;
        _governanceRepository = governanceRepository;
        _redisMultiplexer = redisMultiplexer;
        _environment = environment;
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
            if (_governanceRepository is SqliteGovernanceRepository sqliteRepo)
            {
                // SEC E-12: Use thread-safe PingAsync behind _lock to avoid concurrency collisions with running transactions
                dbHealthy = await sqliteRepo.PingAsync(ct).ConfigureAwait(false);
                if (sqliteRepo.IsAuditPipelineFaulted)
                {
                    dbHealthy = false;
                    dbDesc = "SQLite Governance DB: Audit pipeline is faulted.";
                }
                else
                {
                    dbDesc = dbHealthy ? "SQLite Governance DB connection active." : "SQLite query returned null.";
                }
            }
            else if (_governanceRepository is PostgreSqlGovernanceRepository pgRepo)
            {
                dbHealthy = await pgRepo.PingAsync(ct).ConfigureAwait(false);
                if (pgRepo.IsAuditPipelineFaulted)
                {
                    dbHealthy = false;
                    dbDesc = "PostgreSQL Governance DB: Audit pipeline is faulted.";
                }
                else
                {
                    dbDesc = dbHealthy ? "PostgreSQL Governance DB connection active." : "PostgreSQL query returned null.";
                }
            }
            else if (_governanceRepository is SqlServerGovernanceRepository msRepo)
            {
                dbHealthy = await msRepo.PingAsync(ct).ConfigureAwait(false);
                if (msRepo.IsAuditPipelineFaulted)
                {
                    dbHealthy = false;
                    dbDesc = "SQL Server Governance DB: Audit pipeline is faulted.";
                }
                else
                {
                    dbDesc = dbHealthy ? "SQL Server Governance DB connection active." : "SQL Server query returned null.";
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
                    var ping = await _redisMultiplexer.GetDatabase().PingAsync().ConfigureAwait(false);
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

        return new GatewayHealthReport(overallHealthy, components);
    }
}
