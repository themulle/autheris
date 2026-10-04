using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Garnet;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Garnet;

public interface IGarnetServerManager : IDisposable
{
    bool IsRunning { get; }

    /// <summary>
    /// Password the in-process RESP client must present (Garnet runs with <c>--auth Password</c>).
    /// </summary>
    string ClientPassword { get; }

    void StartServer();
    void StopServer();
}

/// <summary>
/// Manages the lifecycle of an embedded Microsoft Garnet cache server (RESP-compatible, Tsavorite-powered).
/// Can be operated as an IHostedService in ASP.NET Core or started imperatively.
/// </summary>
public sealed class GarnetServerManager : IGarnetServerManager, IHostedService
{
    private readonly GarnetOptions _options;
    private readonly ILogger<GarnetServerManager>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly object _syncLock = new();
    private readonly string? _environmentName;
    private readonly string? _tlsCertPassword;
    private GarnetServer? _server;
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public string ClientPassword { get; }

    public GarnetServerManager(
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<GarnetServerManager>? logger = null,
        ILoggerFactory? loggerFactory = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IHostEnvironment? environment = null)
    {
        _options = gatewayOptions?.Value?.Caching?.Garnet ?? new GarnetOptions();
        _logger = logger;
        _loggerFactory = loggerFactory;
        _environmentName = environment?.EnvironmentName
                           ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                           ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        ClientPassword = ResolvePassword(_options, secretProvider);
        _tlsCertPassword = _options.EnableTls && !string.IsNullOrWhiteSpace(_options.TlsCertPasswordSecretRef)
            ? ResolveSecret(_options.TlsCertPasswordSecretRef, secretProvider, "Garnet-TLS-Zertifikatspasswort")
            : null;
    }

    /// <summary>
    /// SEC H-01: Garnet always runs with password authentication. Without an explicit secret reference an
    /// ephemeral random password is generated per process (only the in-process client needs to know it).
    /// </summary>
    private static string ResolvePassword(GarnetOptions options, IKeyVaultSecretProvider? secretProvider)
    {
        var secretRef = options.PasswordSecretRef;
        if (!string.IsNullOrWhiteSpace(secretRef))
        {
            return ResolveSecret(secretRef, secretProvider, "Garnet-Passwort");
        }

        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    private static string ResolveSecret(string secretRef, IKeyVaultSecretProvider? secretProvider, string description)
    {
        byte[]? secretBytes = null;
        if (secretProvider != null)
        {
            secretBytes = secretProvider.GetSecretBytes(secretRef);
        }
        else
        {
            var envValue = Environment.GetEnvironmentVariable(secretRef.Replace(":", "__").Replace("-", "_"))
                           ?? Environment.GetEnvironmentVariable(secretRef);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                secretBytes = Encoding.UTF8.GetBytes(envValue);
            }
        }

        if (secretBytes == null || secretBytes.Length == 0)
        {
            throw new InvalidOperationException($"Sicherheitsfehler: Das {description} ('{secretRef}') konnte nicht aufgelöst werden.");
        }

        return Encoding.UTF8.GetString(secretBytes);
    }

    /// <summary>
    /// Builds the Garnet command line arguments. SEC H-01: always <c>--auth Password</c>; with
    /// <see cref="GarnetOptions.EnableTls"/> additionally <c>--tls --cert-file-name &lt;pfx&gt; [--cert-password &lt;pw&gt;]</c>.
    /// </summary>
    internal static List<string> BuildServerArguments(GarnetOptions options, string password, string? tlsCertPassword)
    {
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string>
        {
            "--port", options.Port.ToString(),
            "--bind", options.Host,
            "--auth", "Password",
            "--password", password
        };

        if (options.EnableTls)
        {
            if (string.IsNullOrWhiteSpace(options.TlsCertFile))
            {
                throw new InvalidOperationException("Sicherheitsfehler: Caching.Garnet.EnableTls erfordert Caching.Garnet.TlsCertFile (PFX).");
            }

            args.Add("--tls");
            args.Add("--cert-file-name");
            args.Add(options.TlsCertFile);
            if (!string.IsNullOrEmpty(tlsCertPassword))
            {
                args.Add("--cert-password");
                args.Add(tlsCertPassword);
            }
        }

        if (!string.IsNullOrWhiteSpace(options.CheckpointDir))
        {
            args.Add("--checkpointdir");
            args.Add(options.CheckpointDir);
        }

        return args;
    }

    internal static bool IsLoopbackBinding(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim().Trim('[', ']');
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(trimmed, out var ip) && IPAddress.IsLoopback(ip);
    }

    private bool IsDevelopmentEnvironment() =>
        string.Equals(_environmentName, "Development", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_environmentName, "Testing", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_environmentName, "Test", StringComparison.OrdinalIgnoreCase);

    public void StartServer()
    {
        lock (_syncLock)
        {
            if (_isRunning || !_options.EnableEmbeddedServer)
            {
                return;
            }

            // SEC H-01: outside Development the embedded cache must never be reachable from the network
            // (TLS is optional, see EnableTls; consent decisions and epochs live there).
            if (!IsLoopbackBinding(_options.Host) && !IsDevelopmentEnvironment())
            {
                throw new InvalidOperationException(
                    $"Sicherheitsfehler: Der eingebettete Garnet-Server darf außerhalb von Development nur an Loopback gebunden werden (konfiguriert: '{_options.Host}').");
            }

            _logger?.LogInformation("Starting embedded Microsoft Garnet server on {Host}:{Port}...", _options.Host, _options.Port);

            // SEC H-01: optional TLS (defense in depth; the server is loopback-only outside Development)
            if (_options.EnableTls && (string.IsNullOrWhiteSpace(_options.TlsCertFile) || !File.Exists(_options.TlsCertFile)))
            {
                throw new InvalidOperationException(
                    $"Sicherheitsfehler: Caching.Garnet.EnableTls ist aktiv, aber das Zertifikat '{_options.TlsCertFile}' wurde nicht gefunden.");
            }

            var args = BuildServerArguments(_options, ClientPassword, _tlsCertPassword);

            _server = _loggerFactory != null
                ? new GarnetServer(args.ToArray(), _loggerFactory)
                : new GarnetServer(args.ToArray());

            _server.Start();
            _isRunning = true;

            _logger?.LogInformation("Embedded Microsoft Garnet server is listening on {Host}:{Port}.", _options.Host, _options.Port);
        }
    }

    public void StopServer()
    {
        lock (_syncLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _logger?.LogInformation("Stopping embedded Microsoft Garnet server...");
            try
            {
                _server?.Dispose();
                _server = null;
                _isRunning = false;
                _logger?.LogInformation("Embedded Microsoft Garnet server stopped.");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Exception occurred while stopping Garnet server.");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartServer();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopServer();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopServer();
    }
}
