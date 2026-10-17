namespace Autheris.Api.Middleware;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autheris.Domain.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// PLAN-SEC-115 A-9 / C-115-5: registered only when the REST data switch is off. For a request to the closed REST data family
/// (<c>/api/v1/data</c> and below, plus the dataset sample-rows route) it increments <c>autheris_disabled_path_requests_total{path="rest_data"}</c>
/// and, for an authenticated principal only, writes a rate-limited warning (the "leaked token tried the closed path" signal).
/// State is fixed-size (one global per-minute budget, a capped per-principal table), the principal is identified by a per-process
/// keyed hash (never the raw id, token, query string or path suffix), and the middleware never writes a response, never
/// short-circuits and never throws: the router answers the normal 404.
/// </summary>
public sealed class DisabledReadPathObserverMiddleware
{
    /// <summary>Process-wide cap of log lines per minute.</summary>
    public const int MaxLogLinesPerMinute = 20;

    /// <summary>Capacity of the per-principal rate table.</summary>
    public const int MaxTrackedPrincipals = 256;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly PathString DataPrefix = new("/api/v1/data");
    private static readonly PathString DatasetsPrefix = new("/api/v1/catalog/datasets");

    private readonly RequestDelegate _next;
    private readonly ILogger<DisabledReadPathObserverMiddleware> _logger;
    private readonly TimeProvider _time;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastLogged = new(MaxTrackedPrincipals, StringComparer.Ordinal);
    private long _windowStart;
    private int _linesInWindow;

    public DisabledReadPathObserverMiddleware(RequestDelegate next, ILogger<DisabledReadPathObserverMiddleware> logger, TimeProvider? timeProvider = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
        _windowStart = _time.GetTimestamp();
    }

    /// <summary>Number of principals currently held in the rate table (test aid; never above <see cref="MaxTrackedPrincipals"/>).</summary>
    public int TrackedPrincipalCount
    {
        get { lock (_gate) { return _lastLogged.Count; } }
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            if (IsClosedRestPath(context.Request.Path))
            {
                GatewayDiagnostics.DisabledPathRequestCounter.Add(1, new KeyValuePair<string, object?>("path", "rest_data"));
                if (context.User.Identity?.IsAuthenticated == true)
                {
                    TryLog(context);
                }
            }
        }
        catch
        {
            // Fail open for the request, but make the failure visible without touching the request.
            try
            {
                GatewayDiagnostics.DisabledPathRequestCounter.Add(1, new KeyValuePair<string, object?>("path", "observer_error"));
            }
            catch
            {
                // Metrics must never affect the request either.
            }
        }

        return _next(context);
    }

    /// <summary>Segment-aware, ordinal-ignore-case match of the REST data family (no <c>/api/v1/database</c>).</summary>
    internal static bool IsClosedRestPath(PathString path)
    {
        if (path.StartsWithSegments(DataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // GET /api/v1/catalog/datasets/{dataset}/sample-rows
        return path.StartsWithSegments(DatasetsPrefix, StringComparison.OrdinalIgnoreCase, out var rest) &&
               rest.Value is { } value &&
               value.EndsWith("/sample-rows", StringComparison.OrdinalIgnoreCase) &&
               value.Count(c => c == '/') == 2;
    }

    private void TryLog(HttpContext context)
    {
        var now = _time.GetTimestamp();
        string? hash = null;

        lock (_gate)
        {
            if (_time.GetElapsedTime(_windowStart, now) >= Window)
            {
                _windowStart = now;
                _linesInWindow = 0;
            }

            // Cheapest check first: an exhausted budget costs no hashing and no table access.
            if (_linesInWindow >= MaxLogLinesPerMinute)
            {
                return;
            }
        }

        hash = PrincipalHash(context.User);

        lock (_gate)
        {
            if (_linesInWindow >= MaxLogLinesPerMinute)
            {
                return;
            }

            if (_lastLogged.TryGetValue(hash, out var last) && _time.GetElapsedTime(last, now) < Window)
            {
                return;
            }

            if (!_lastLogged.ContainsKey(hash) && _lastLogged.Count >= MaxTrackedPrincipals)
            {
                EvictOldest(now);
            }

            _lastLogged[hash] = now;
            _linesInWindow++;
        }

        _logger.LogWarning(
            "Request to a read path that is switched off by configuration (rest_data): method={Method} principal={PrincipalHash}.",
            context.Request.Method,
            hash);
    }

    private void EvictOldest(long now)
    {
        string? oldestKey = null;
        var oldestAge = TimeSpan.MinValue;
        foreach (var (key, stamp) in _lastLogged)
        {
            var age = _time.GetElapsedTime(stamp, now);
            if (age > oldestAge)
            {
                oldestAge = age;
                oldestKey = key;
            }
        }

        if (oldestKey is not null)
        {
            _lastLogged.Remove(oldestKey);
        }
    }

    private string PrincipalHash(ClaimsPrincipal user)
    {
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? user.FindFirst("sub")?.Value
                 ?? user.Identity?.Name
                 ?? "unknown";
        var digest = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(digest.AsSpan(0, 6));
    }
}
