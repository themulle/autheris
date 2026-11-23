using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Health;

/// <summary>
/// Fails readiness when the database schema version exceeds the highest governance migration step known to this binary,
/// preventing stale-code nodes from serving after a breaking purge or policy migration.
/// </summary>
public sealed class GovernanceSchemaVersionReadinessCheck
{
    public const string ComponentName = "GovernanceSchemaVersion";

    private readonly Func<CancellationToken, Task<int>> _readAppliedMaxVersionAsync;
    private readonly int _highestKnownVersion;
    private readonly ILogger<GovernanceSchemaVersionReadinessCheck>? _logger;

    public GovernanceSchemaVersionReadinessCheck(
        MigrationPurgeStore purgeStore,
        IGovernanceSqlDialect dialect,
        ILogger<GovernanceSchemaVersionReadinessCheck>? logger = null)
        : this(
            (purgeStore ?? throw new ArgumentNullException(nameof(purgeStore))).GetAppliedSchemaMaxVersionAsync,
            ResolveHighestKnownVersion(dialect ?? throw new ArgumentNullException(nameof(dialect))),
            logger)
    {
    }

    public GovernanceSchemaVersionReadinessCheck(
        Func<CancellationToken, Task<int>> readAppliedMaxVersionAsync,
        int highestKnownVersion,
        ILogger<GovernanceSchemaVersionReadinessCheck>? logger = null)
    {
        _readAppliedMaxVersionAsync = readAppliedMaxVersionAsync ?? throw new ArgumentNullException(nameof(readAppliedMaxVersionAsync));
        _highestKnownVersion = Math.Max(1, highestKnownVersion);
        _logger = logger;
    }

    public int HighestKnownVersion => _highestKnownVersion;

    public async Task<HealthCheckComponentResult> EvaluateAsync(CancellationToken ct = default)
    {
        try
        {
            var appliedMax = await _readAppliedMaxVersionAsync(ct).ConfigureAwait(false);
            if (appliedMax > _highestKnownVersion)
            {
                _logger?.LogError(
                    "Readiness failed: governance database schema version {AppliedVersion} exceeds binary maximum {KnownMax}.",
                    appliedMax,
                    _highestKnownVersion);
                return new HealthCheckComponentResult(
                    ComponentName,
                    false,
                    $"Governance schema version {appliedMax} is newer than this binary supports (max {_highestKnownVersion}).");
            }

            return new HealthCheckComponentResult(
                ComponentName,
                true,
                $"Governance schema version {appliedMax} supported (binary max {_highestKnownVersion}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Governance schema version readiness check failed.");
            return new HealthCheckComponentResult(
                ComponentName,
                false,
                "Governance schema version check failed.");
        }
    }

    public static int ResolveHighestKnownVersion(IGovernanceSqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        var engine = dialect is PostgreSqlGovernanceDialect ? "postgresql" : "sqlserver";
        var steps = SqlMigrationStep.LoadEmbedded(engine);
        return steps.Count == 0 ? 1 : steps.Max(s => s.Version);
    }
}
