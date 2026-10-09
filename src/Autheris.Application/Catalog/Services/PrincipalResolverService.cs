namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class PrincipalResolverService : IPrincipalResolverService
{
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<PrincipalResolverService> _logger;

    public PrincipalResolverService(
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<PrincipalResolverService>? logger = null)
    {
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PrincipalResolverService>.Instance;
    }

    public Task<IReadOnlyList<PrincipalResolutionItem>> ResolvePrincipalAsync(string query, RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<PrincipalResolutionItem>>([]);
        }

        ct.ThrowIfCancellationRequested();

        var normalizedQuery = query.Trim();
        var callerTenant = context.TenantId.Value;

        // Filter users belonging to caller's tenant
        var users = _gatewayOptions.Value.Authentication.BasicAuth.Users
            .Where(u => string.Equals(u.TenantId, callerTenant, StringComparison.OrdinalIgnoreCase) ||
                        (string.IsNullOrWhiteSpace(u.TenantId) && callerTenant == Autheris.Domain.Common.TenantId.LegacySingleTenant.Value))
            .ToList();

        var matches = new List<(BasicAuthUserConfig User, bool IsExact, int Distance)>();

        foreach (var user in users)
        {
            var username = user.Username;
            if (string.IsNullOrWhiteSpace(username)) continue;

            if (string.Equals(username, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((user, true, 0));
                continue;
            }

            if (username.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                normalizedQuery.Contains(username, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((user, false, 1));
                continue;
            }

            var dist = LevenshteinDistance(normalizedQuery, username);
            var maxAllowedDist = normalizedQuery.Length <= 4 ? 1 : 2;
            if (dist <= maxAllowedDist)
            {
                matches.Add((user, false, dist));
            }
        }

        if (matches.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PrincipalResolutionItem>>([]);
        }

        // R-61: Ambiguous matches must always have ExactMatch = false so callers never pick silently
        var isAmbiguous = matches.Count > 1;

        var results = matches
            .OrderBy(m => m.Distance)
            .ThenBy(m => m.User.Username)
            .Select(m => new PrincipalResolutionItem(
                Sid: m.User.Sid ?? $"user:{m.User.Username.ToLowerInvariant()}",
                DisplayName: m.User.Username,
                PrincipalType: "User",
                ExactMatch: !isAmbiguous && m.IsExact,
                Groups: m.User.GroupSids
            ))
            .ToList();

        return Task.FromResult<IReadOnlyList<PrincipalResolutionItem>>(results);
    }

    private static int LevenshteinDistance(string s, string t)
    {
        if (string.IsNullOrEmpty(s)) return t?.Length ?? 0;
        if (string.IsNullOrEmpty(t)) return s.Length;

        int n = s.Length;
        int m = t.Length;
        int[,] d = new int[n + 1, m + 1];

        for (int i = 0; i <= n; d[i, 0] = i++) { }
        for (int j = 0; j <= m; d[0, j] = j++) { }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = char.ToLowerInvariant(s[i - 1]) == char.ToLowerInvariant(t[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }
}
