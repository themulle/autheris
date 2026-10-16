using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Lockout protection for SQL Server data sources. A login failure (error 18456/18452/18451, a rejected login or a credential-type managed NTLM
/// failure) opens the breaker for <see cref="DataSourceConnectionOptions.LoginFailureCooldownSeconds"/>; while it is open the factory fails
/// closed with <see cref="SqlSourceLoginBlockedException"/> without contacting the server, so neither the catalog build, query execution nor the
/// connectivity probe can drive the Active Directory bad-password counter. The breaker is keyed by a fingerprint of the credential identity
/// (connection string and WindowsAuth settings, never a resolved secret), so a corrected configuration lifts it. Network and timeout failures
/// never open it. State is per process: every replica has its own breaker.
/// </summary>
internal sealed class SqlLoginCircuitBreaker
{
    private static readonly HashSet<string> CredentialStatuses = new(StringComparer.Ordinal)
    {
        "InvalidCredentials", "UnknownCredentials", "CredentialsExpired"
    };

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;

    public SqlLoginCircuitBreaker(TimeProvider? time = null, ILogger? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Throws <see cref="SqlSourceLoginBlockedException"/> while the breaker of this credential identity is open.</summary>
    public void ThrowIfOpen(DataSourceConnectionOptions options)
    {
        if (options.LoginFailureCooldownSeconds <= 0 || _entries.IsEmpty)
        {
            return;
        }

        string key = Fingerprint(options);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return;
        }

        var remaining = entry.OpenUntil - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            _entries.TryRemove(key, out _);
            return;
        }

        throw new SqlSourceLoginBlockedException(remaining, entry.Reason);
    }

    /// <summary>Clears the breaker after a successful open.</summary>
    public void RecordSuccess(DataSourceConnectionOptions options)
    {
        if (!_entries.IsEmpty)
        {
            _entries.TryRemove(Fingerprint(options), out _);
        }
    }

    /// <summary>Opens the breaker when <paramref name="failure"/> is a login failure; returns whether it did.</summary>
    public bool RecordFailure(DataSourceConnectionOptions options, Exception failure)
    {
        if (options.LoginFailureCooldownSeconds <= 0 || !IsLoginFailure(failure, out string reason))
        {
            return false;
        }

        var cooldown = TimeSpan.FromSeconds(options.LoginFailureCooldownSeconds);
        _entries[Fingerprint(options)] = new Entry(_time.GetUtcNow() + cooldown, reason);
        _logger?.LogWarning(
            "SQL Server login failed ({Reason}); further connections of this data source are blocked for {CooldownSeconds} s to protect the account from lockout (this replica only).",
            reason, options.LoginFailureCooldownSeconds);
        return true;
    }

    /// <summary>True for 18456/18452/18451, <see cref="SqlServerWindowsAuthFailureCategory.LoginRejected"/> and credential-type NTLM statuses.</summary>
    internal static bool IsLoginFailure(Exception failure, out string reason)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SqlServerWindowsAuthException { Category: SqlServerWindowsAuthFailureCategory.LoginRejected } auth:
                    reason = auth.SqlNumber is { } n ? $"LoginRejected, SqlNumber={n}" : "LoginRejected";
                    return true;
                case SqlServerWindowsAuthException { Category: SqlServerWindowsAuthFailureCategory.NtlmNegotiation, NtlmStatus: { } status } when CredentialStatuses.Contains(status):
                    reason = $"NtlmNegotiation, NtlmStatus={status}";
                    return true;
                case SqlException { Number: 18456 or 18452 or 18451 } sql:
                    reason = $"SqlNumber={sql.Number}";
                    return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    internal static string Fingerprint(DataSourceConnectionOptions options)
    {
        var w = options.WindowsAuth;
        string text = string.Join('\u001f',
            options.Provider, options.ConnectionString, options.PasswordKeyVaultRef,
            w?.Mode, w?.Domain, w?.Username, w?.PasswordRef, w?.NtlmUserFilePath, w?.ServicePrincipalName,
            w?.KerberosConfigPath, w?.KerberosKeytabPath, w?.KerberosCachePath, w?.UseManagedNtlm);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private sealed record Entry(DateTimeOffset OpenUntil, string Reason);
}
