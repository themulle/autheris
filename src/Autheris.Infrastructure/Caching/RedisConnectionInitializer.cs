namespace Autheris.Infrastructure.Caching;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Garnet;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

/// <summary>
/// Hosted initializer that establishes the Redis/Garnet <see cref="IConnectionMultiplexer"/> asynchronously
/// via <see cref="ConnectionMultiplexer.ConnectAsync(ConfigurationOptions, System.IO.TextWriter?)"/> so DI container
/// resolution never blocks synchronously on network I/O (ARC-I-10).
/// </summary>
public sealed class RedisConnectionInitializer : IRedisConnectionProvider, IHostedService, IAsyncDisposable, IDisposable
{
    private readonly GatewayOptions _options;
    private readonly IGarnetServerManager? _garnetManager;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<RedisConnectionInitializer>? _logger;
    private readonly Func<ConfigurationOptions, CancellationToken, Task<IConnectionMultiplexer>> _connectAsync;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile IConnectionMultiplexer? _multiplexer;
    private bool _disposed;

    public RedisConnectionInitializer(
        IOptions<GatewayOptions> options,
        IGarnetServerManager? garnetManager = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null,
        ILogger<RedisConnectionInitializer>? logger = null,
        Func<ConfigurationOptions, CancellationToken, Task<IConnectionMultiplexer>>? connectAsync = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _garnetManager = garnetManager;
        _secretProvider = secretProvider;
        _environment = environment;
        _logger = logger;
        _connectAsync = connectAsync ?? DefaultConnectAsync;
    }

    private static async Task<IConnectionMultiplexer> DefaultConnectAsync(ConfigurationOptions cfg, CancellationToken _) =>
        await ConnectionMultiplexer.ConnectAsync(cfg).ConfigureAwait(false);

    public bool IsConfigured =>
        _options.Caching.Garnet.EnableEmbeddedServer || _options.Caching.Redis.Enabled;

    public bool IsConnected => _multiplexer is { IsConnected: true };

    public IConnectionMultiplexer? TryGetMultiplexer() => _multiplexer;

    public async ValueTask<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        if (_multiplexer is { } existing)
        {
            return existing;
        }

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return _multiplexer;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "Asynchronous Redis/Garnet connection initialization did not complete during startup; continuing in degraded/fail-closed mode until reachable.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var mux = Interlocked.Exchange(ref _multiplexer, null);
        if (mux != null)
        {
            await mux.CloseAsync().ConfigureAwait(false);
            await mux.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_multiplexer != null)
        {
            return;
        }

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_multiplexer != null)
            {
                return;
            }

            var config = BuildConfigurationOptions();
            _multiplexer = await _connectAsync(config, ct).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private ConfigurationOptions BuildConfigurationOptions()
    {
        if (_options.Caching.Garnet.EnableEmbeddedServer)
        {
            _garnetManager?.StartServer();
            var garnetConfig = new ConfigurationOptions
            {
                EndPoints = { $"{_options.Caching.Garnet.Host}:{_options.Caching.Garnet.Port}" },
                Password = _garnetManager?.ClientPassword,
                ConnectTimeout = _options.Caching.Redis.ConnectTimeoutMs,
                SyncTimeout = _options.Caching.Redis.SyncTimeoutMs,
                AbortOnConnectFail = false
            };

            return RedisConnectionSecurity.ApplyGarnetClientTls(garnetConfig, _options.Caching.Garnet);
        }

        var redisConfig = ConfigurationOptions.Parse(_options.Caching.Redis.Configuration);
        redisConfig.ConnectTimeout = _options.Caching.Redis.ConnectTimeoutMs;
        redisConfig.SyncTimeout = _options.Caching.Redis.SyncTimeoutMs;
        redisConfig.AbortOnConnectFail = false;

        return RedisConnectionSecurity.Apply(redisConfig, _options.Caching.Redis, _secretProvider, _environment);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var mux = Interlocked.Exchange(ref _multiplexer, null);
        if (mux != null)
        {
            await mux.DisposeAsync().ConfigureAwait(false);
        }

        _connectLock.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _multiplexer, null)?.Dispose();
        _connectLock.Dispose();
    }
}
