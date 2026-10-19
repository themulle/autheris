namespace Autheris.Application.Sql.TimeZones;

using Microsoft.Extensions.Hosting;

/// <summary>REQ-SQL-130 (A-7): evaluates the conversion state once at startup, so a failure is logged once and the first request does not pay for the probe.</summary>
public sealed class TimeZoneConversionStartupCheck(TimeZoneConversionState state) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        state.Evaluate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
