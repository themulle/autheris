namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;

/// <summary>
/// Directory access for the LDAPS provider. The implementation owns the connection: LDAPS only (no StartTLS downgrade, no cleartext
/// fallback), mandatory certificate validation, the realm domain base DN, and a connection and request time limit of at most 3 seconds.
/// </summary>
public interface ILdapDirectoryAdapter
{
    /// <summary>Searches users by an already escaped filter; returns at most <paramref name="maxResults"/> distinguished names (size limit).</summary>
    Task<IReadOnlyList<string>> FindUserDnsAsync(string escapedFilter, int maxResults, CancellationToken ct = default);

    /// <summary>Reads the transitive <c>tokenGroups</c> (binary SIDs) of one user with a base-scope search.</summary>
    Task<IReadOnlyList<byte[]>> ReadTokenGroupsAsync(string userDistinguishedName, CancellationToken ct = default);
}

public sealed class LdapsRoleProvider : IRoleProvider, IDisposable
{
    public const string Id = "ldaps-ad";
    public string ProviderId => Id;

    private static readonly string[] UserSidClaimTypes =
    [
        ClaimTypes.PrimarySid, "objectSid", "onprem_sid", "primarysid",
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid"
    ];

    private readonly ILdapDirectoryAdapter? _adapter;
    private readonly TimeSpan _timeout;
    private readonly int _failureThreshold;
    private readonly TimeSpan _circuitBreakDuration;
    private readonly int _maxGroups;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _lock = new();
    private int _consecutiveFailures;
    private long _circuitOpenUntilTick;

    public LdapsRoleProvider(
        ILdapDirectoryAdapter? adapter = null,
        TimeSpan? timeout = null,
        int failureThreshold = 5,
        TimeSpan? circuitBreakDuration = null,
        int maxGroups = 1024,
        int maxConcurrency = 16)
    {
        _adapter = adapter;
        _timeout = timeout is { } t && t > TimeSpan.Zero && t <= TimeSpan.FromSeconds(3) ? t : TimeSpan.FromSeconds(3);
        _failureThreshold = failureThreshold;
        _circuitBreakDuration = circuitBreakDuration ?? TimeSpan.FromSeconds(30);
        _maxGroups = maxGroups;
        _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    public void Dispose() => _concurrency.Dispose();

    public bool IsCircuitBreakerOpen
    {
        get
        {
            lock (_lock)
            {
                return Environment.TickCount64 < _circuitOpenUntilTick;
            }
        }
    }

    /// <summary>
    /// Escapes a filter assertion value (RFC 4515): the filter metacharacters, every ASCII control character and every non-ASCII
    /// character (as UTF-8 octets; lone surrogates become U+FFFD) are written as <c>\xx</c>.
    /// </summary>
    public static string SanitizeLdapFilter(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder(input.Length * 2);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in input.EnumerateRunes())
        {
            if (rune.Value is '\\' or '*' or '(' or ')' || rune.Value < 0x20 || rune.Value == 0x7f)
            {
                sb.Append('\\').Append(rune.Value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (rune.Value < 0x7f)
            {
                sb.Append((char)rune.Value);
            }
            else
            {
                var written = rune.EncodeToUtf8(utf8);
                for (var i = 0; i < written; i++)
                {
                    sb.Append('\\').Append(utf8[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>Escapes a binary value as <c>\xx</c> octets for an <c>objectSid</c> equality assertion.</summary>
    public static string EscapeBinary(ReadOnlySpan<byte> value)
    {
        var sb = new StringBuilder(value.Length * 3);
        foreach (var b in value)
        {
            sb.Append('\\').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public async Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (_adapter == null || IsCircuitBreakerOpen)
        {
            return ProviderRoleResult.FailedResult;
        }

        var filter = BuildUserFilter(identity);
        if (filter is null)
        {
            return ProviderRoleResult.FailedResult;
        }

        if (!await _concurrency.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // The concurrency limit protects the threads of the gateway from a slow directory.
            return ProviderRoleResult.FailedResult;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            // A lookup that finds no user or more than one is unknown, not "no groups".
            var users = await _adapter.FindUserDnsAsync(filter, 2, cts.Token).ConfigureAwait(false);
            if (users.Count != 1)
            {
                return ProviderRoleResult.FailedResult;
            }

            var tokenGroups = await _adapter.ReadTokenGroupsAsync(users[0], cts.Token).ConfigureAwait(false);
            if (tokenGroups.Count > _maxGroups)
            {
                return ProviderRoleResult.FailedResult;
            }

            var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var malformed = 0;
            foreach (var entry in tokenGroups)
            {
                if (SidFormat.TryFromBinary(entry, out var sid))
                {
                    sids.Add(sid.Value);
                }
                else
                {
                    malformed++;
                }
            }

            IdentityGroupMetrics.RecordRejected(IdentityGroupMetrics.SourceResolver, malformed);
            RecordSuccess();
            var result = new ProviderRoleResult(
                sids,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return malformed > 0 ? result with { Failed = true } : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Fail closed: timeout, directory error or certificate failure.
            RecordFailure();
            return ProviderRoleResult.FailedResult;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>The lookup uses the validated user SID (objectSid equality) or the UPN, never a name claim (not unique across a forest).</summary>
    private static string? BuildUserFilter(ResolvedIdentity identity)
    {
        foreach (var type in UserSidClaimTypes)
        {
            foreach (var claim in identity.RawClaims)
            {
                if (string.Equals(claim.Type, type, StringComparison.OrdinalIgnoreCase)
                    && SidFormat.TryToBinary(claim.Value, out var binary))
                {
                    return $"(&(objectCategory=person)(objectClass=user)(objectSid={EscapeBinary(binary)}))";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(identity.Upn))
        {
            return $"(&(objectCategory=person)(objectClass=user)(userPrincipalName={SanitizeLdapFilter(identity.Upn)}))";
        }

        return null;
    }

    private void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }
    }

    private void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _failureThreshold)
            {
                _circuitOpenUntilTick = Environment.TickCount64 + (long)_circuitBreakDuration.TotalMilliseconds;
            }
        }
    }
}
