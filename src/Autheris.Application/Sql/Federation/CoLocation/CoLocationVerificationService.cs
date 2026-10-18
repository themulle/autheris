namespace Autheris.Application.Sql.Federation.CoLocation;

using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public interface ICoLocationVerificationService
{
    ValueTask<bool> VerifyCoLocationServerAsync(
        DbConnection connection,
        string expectedServerName,
        string connectionPoolKey,
        CancellationToken ct = default);
}

/// <summary>
/// Issue #132 (PLAN-DATA-132, SEC-DATA132-05): Connect-time physical server identity verification service.
/// Queries SELECT @@SERVERNAME and verifies it matches the registered host identity.
/// If physical server identity diverges, returns false to signal graceful fallback to DuckDB staging.
/// </summary>
public sealed class CoLocationVerificationService : ICoLocationVerificationService
{
    private readonly ConcurrentDictionary<string, string> _verifiedServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CoLocationVerificationService>? _logger;

    public CoLocationVerificationService(ILogger<CoLocationVerificationService>? logger = null)
    {
        _logger = logger;
    }

    public async ValueTask<bool> VerifyCoLocationServerAsync(
        DbConnection connection,
        string expectedServerName,
        string connectionPoolKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(expectedServerName);

        if (_verifiedServers.TryGetValue(connectionPoolKey, out var cachedServer))
        {
            return IsServerMatch(cachedServer, expectedServerName);
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT @@SERVERNAME";
            var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var actualServer = scalar as string ?? scalar?.ToString();
            if (string.IsNullOrWhiteSpace(actualServer))
            {
                _logger?.LogWarning("Server identity verification returned empty @@SERVERNAME; co-location disabled.");
                return false;
            }

            _verifiedServers[connectionPoolKey] = actualServer;
            bool match = IsServerMatch(actualServer, expectedServerName);
            if (!match)
            {
                _logger?.LogWarning(
                    "Co-location server identity mismatch for pool '{Pool}': expected '{Expected}', actual @@SERVERNAME '{Actual}'. Falling back to DuckDB staging.",
                    connectionPoolKey, expectedServerName, actualServer);
            }
            return match;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to verify server identity on connection for pool '{Pool}'; falling back to DuckDB staging.", connectionPoolKey);
            return false;
        }
    }

    public static bool IsServerMatch(string actual, string expected)
    {
        if (string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var actualHost = ExtractHost(actual);
        var expectedHost = ExtractHost(expected);
        return string.Equals(actualHost, expectedHost, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractHost(string server)
    {
        var s = server.Trim();
        var backslash = s.IndexOf('\\');
        if (backslash >= 0) s = s[..backslash];
        var comma = s.IndexOf(',');
        if (comma >= 0) s = s[..comma];
        return s;
    }
}
