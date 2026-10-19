namespace Autheris.Application.Sql.TimeZones;

using Microsoft.Extensions.Logging;
using TrinoSqlEngine;

/// <summary>
/// REQ-SQL-130 (A-7): evaluates once per process whether the closed zone registry is intact (self-test) and, for DuckDB, whether the ICU probe passes.
/// Before <see cref="Evaluate"/> has run, and after any failure, the answer is false; a failure is logged once as a Warning with a check id and no data
/// and stays until restart (BC-130-7). SQL Server and PostgreSQL convert in the database, so they depend on the registry only.
/// </summary>
public sealed class TimeZoneConversionState : ITimeZoneConversionState
{
    private readonly ILogger<TimeZoneConversionState>? _logger;
    private readonly Func<TimeZoneSelfTestResult> _selfTest;
    private readonly Func<DuckDbProbeResult> _duckDbProbe;
    private readonly object _gate = new();
    private volatile Snapshot? _snapshot;

    public TimeZoneConversionState(ILogger<TimeZoneConversionState>? logger = null)
        : this(logger, TimeZoneRegistry.SelfTest, DuckDbIcuProbe.Run)
    {
    }

    internal TimeZoneConversionState(ILogger<TimeZoneConversionState>? logger, Func<TimeZoneSelfTestResult> selfTest, Func<DuckDbProbeResult> duckDbProbe)
    {
        _logger = logger;
        _selfTest = selfTest;
        _duckDbProbe = duckDbProbe;
    }

    public bool IsAvailable(TargetSqlDialect dialect)
    {
        var snapshot = _snapshot;
        if (snapshot is null) return false;
        return dialect switch
        {
            TargetSqlDialect.SqlServer or TargetSqlDialect.PostgreSql => snapshot.Registry,
            TargetSqlDialect.DuckDb => snapshot.Registry && snapshot.DuckDb,
            _ => false
        };
    }

    /// <summary>Runs the checks once; later calls return immediately. Never throws.</summary>
    public void Evaluate()
    {
        if (_snapshot is not null) return;
        lock (_gate)
        {
            if (_snapshot is not null) return;
            _snapshot = Compute();
        }
    }

    private Snapshot Compute()
    {
        bool registry;
        try
        {
            var result = _selfTest();
            registry = result.Passed;
            if (!registry) Warn("registry self-test failed", result.FailedCheck ?? "unknown");
        }
        catch (Exception)
        {
            registry = false;
            Warn("registry self-test failed", "exception");
        }

        if (!registry) return new Snapshot(false, false);

        bool duckDb;
        try
        {
            var probe = _duckDbProbe();
            duckDb = probe == DuckDbProbeResult.Ok;
            if (!duckDb) Warn("DuckDB ICU probe failed", probe.ToString());
        }
        catch (Exception)
        {
            duckDb = false;
            Warn("DuckDB ICU probe failed", nameof(DuckDbProbeResult.Failed));
        }

        return new Snapshot(true, duckDb);
    }

    // The check id is a constant of this assembly or the engine; no exception text, path or value is ever logged.
    private void Warn(string what, string check) =>
        _logger?.LogWarning("Time zone conversion is unavailable: {What} (check {Check}).", what, check);

    private sealed record Snapshot(bool Registry, bool DuckDb);
}
