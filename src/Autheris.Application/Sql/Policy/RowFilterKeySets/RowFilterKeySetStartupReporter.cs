namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #107 (SEC-RF107-01 c, D-WPC-9 a): logs once at start which data sources carry the operator attestation
/// <c>KeySetCacheAllowed=true</c> (names sorted ordinally, <c>none</c> when empty), so that the opt-in is visible in the operational log.
/// </summary>
public sealed class RowFilterKeySetStartupReporter(IOptions<GatewayOptions> options, ILogger<RowFilterKeySetStartupReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var names = (options.Value.DataSources?.Connections ?? [])
            .Where(static c => c.Value.KeySetCacheAllowed)
            .Select(static c => c.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        logger.LogInformation("Row-filter key-set cache allowed on data sources: {DataSources}", names.Count == 0 ? "none" : string.Join(", ", names));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
