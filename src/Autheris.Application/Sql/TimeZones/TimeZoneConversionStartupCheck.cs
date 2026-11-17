namespace Autheris.Application.Sql.TimeZones;

using Microsoft.Extensions.Hosting;

/// <summary>130 (A-7): evaluates the registry self-test once at startup so a failure is logged once. The DuckDB ICU probe stays lazy: it loads the native DuckDB library, which a node that never uses DuckDB must not map.</summary>
public sealed class TimeZoneConversionStartupCheck(TimeZoneConversionState state) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        state.Evaluate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
