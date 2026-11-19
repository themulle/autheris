namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>The static JWKS key sources of the realms that configure <c>Jwks</c>, keyed by realm id. Created from the validated realm options; a bad file throws.</summary>
public sealed class StaticJwksKeySources
{
    private readonly Dictionary<string, StaticJwksKeySource> _byRealm = new(StringComparer.OrdinalIgnoreCase);

    public StaticJwksKeySources(IOptions<IdentityRealmsOptions> options, ILoggerFactory loggerFactory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger<StaticJwksKeySource>();
        foreach (var (realmId, config) in options.Value.Realms)
        {
            if (config.Jwks is { File: { Length: > 0 } file } jwks)
            {
                _byRealm[realmId] = new StaticJwksKeySource(file, TimeSpan.FromMinutes(jwks.ReloadIntervalMinutes), time ?? TimeProvider.System, logger);
            }
        }
    }

    public StaticJwksKeySource? Get(string realmId) => _byRealm.GetValueOrDefault(realmId);
}

/// <summary>Resolves the key sources when the host starts so a missing or invalid JWKS file fails startup before any request is served.</summary>
public sealed class StaticJwksStartupLoader(StaticJwksKeySources sources) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
