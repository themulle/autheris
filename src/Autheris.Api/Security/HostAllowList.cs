namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options;

/// <summary>
/// SR-L-02: single matcher for the host allow-list (<c>Gateway:Hosting:AllowedHosts</c>). Used by the host filter
/// middleware and by the CSRF origin check, so a rebound domain is rejected by both.
/// Entries: exact host (<c>gateway.corp.example</c>), <c>host:port</c>, <c>*.suffix</c> (sub-domains only) or <c>*</c>.
/// An entry without a port matches the host on any port; an entry with a port matches that port only.
/// </summary>
public sealed class HostAllowList
{
    /// <summary>Host names accepted in Development when no list is configured.</summary>
    public static readonly IReadOnlyList<string> LoopbackDefaults = ["localhost", "127.0.0.1", "[::1]", "*.localhost"];

    private readonly string[] _entries;

    public HostAllowList(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .ToArray();
    }

    public int Count => _entries.Length;

    /// <summary>The configured list; outside Development an empty list stays empty (fail-closed, see the startup validator).</summary>
    public static HostAllowList Resolve(HostingLimitsOptions options, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.AllowedHosts.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        return new HostAllowList(configured.Count == 0 && isDevelopment ? LoopbackDefaults : configured);
    }

    /// <summary>True when <paramref name="authority"/> (<c>host</c> or <c>host:port</c>, no scheme) matches an entry.</summary>
    public bool IsAllowed(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority) || !TrySplit(authority.Trim(), out var host, out var port))
        {
            return false;
        }

        foreach (var entry in _entries)
        {
            if (entry == "*")
            {
                return true;
            }

            if (!TrySplit(entry, out var entryHost, out var entryPort))
            {
                continue;
            }

            if (entryPort is not null && !string.Equals(entryPort, port, StringComparison.Ordinal))
            {
                continue;
            }

            if (entryHost.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = entryHost.AsSpan(1); // ".example.com"
                if (host.Length > suffix.Length && host.AsSpan().EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(entryHost, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySplit(string value, out string host, out string? port)
    {
        host = string.Empty;
        port = null;
        if (value.Length == 0)
        {
            return false;
        }

        if (value[0] == '[')
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = value[..(close + 1)];
            var rest = value[(close + 1)..];
            if (rest.Length == 0)
            {
                return true;
            }

            if (rest[0] != ':' || rest.Length == 1)
            {
                return false;
            }

            port = rest[1..];
            return true;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            host = value;
            return true;
        }

        if (colon != value.LastIndexOf(':') || colon == 0 || colon == value.Length - 1)
        {
            return false;
        }

        host = value[..colon];
        port = value[(colon + 1)..];
        return true;
    }
}
