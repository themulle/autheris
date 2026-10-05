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

    public GatewayHealthCheckService(
        IOptions<GatewayOptions> options,
        ILogger<GatewayHealthCheckService> logger,
        IGovernanceRepository? governanceRepository = null,
        IConnectionMultiplexer? redisMultiplexer = null,
        IHostEnvironment? environment = null)
    {
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
            dbDesc = $"Governance DB query failed: {ex.Message}";
            _logger.LogError(ex, "Governance DB health check failed.");
        }
        components.Add(new HealthCheckComponentResult("GovernanceDb", dbHealthy, dbDesc));
        if (!dbHealthy) overallHealthy = false;

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
                redisDesc = $"Redis ping failed: {ex.Message}";
                _logger.LogError(ex, "Redis health check failed.");
            }
            components.Add(new HealthCheckComponentResult("Redis", redisHealthy, redisDesc));
            if (!redisHealthy) overallHealthy = false;
        }

        // 3. Security Invariants Check (H-5)
        // DANGER entries make the component unhealthy (outside Development additionally the whole report, so that
        // /health/ready returns 503 - defense in depth, startup validation already blocks DANGER there).
        // WARN entries keep the component healthy but are listed as "degraded: ...".
        var dangerBypasses = _options.Value.GetActiveDangerBypasses();
        var warnings = _options.Value.GetActiveWarnings();
        bool securityHealthy = dangerBypasses.Count == 0;
        string securityDesc;
        if (!securityHealthy)
        {
            securityDesc = $"WARNING: Active security bypasses: {string.Join(", ", dangerBypasses)}";
            if (warnings.Count > 0)
            {
                securityDesc += $" degraded: {string.Join(", ", warnings)}";
            }
        }
        else if (warnings.Count > 0)
        {
            securityDesc = $"degraded: {string.Join(", ", warnings)}";
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
