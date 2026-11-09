namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Common;

public static class UsageAttribution
{
    public const string NoneGroupSetId = "none";
    public const string UnassignedBillingGroup = "unassigned";
    public const int DefaultMaxGroupsPerUser = 256;

    public static readonly IReadOnlyList<string> DefaultExcludedGroupSidPrefixes =
    [
        "S-1-1-",
        "S-1-2-",
        "S-1-3-",
        "S-1-5-32-",
        "S-1-5-11",
        "S-1-5-2",
        "S-1-5-4",
        "S-1-5-15",
        "S-1-5-64-",
        "S-1-16-",
        "S-1-18-"
    ];

    public static string ComputeUserKey(Sid userSid, bool pseudonymizeUsers = false, byte[]? hmacKey = null)
    {
        if (!pseudonymizeUsers)
        {
            return userSid.Value;
        }

        if (hmacKey == null || hmacKey.Length == 0)
        {
            throw new InvalidOperationException("Pseudonymization HMAC key is required when PseudonymizeUsers is enabled.");
        }

        using var hmac = new HMACSHA256(hmacKey);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(userSid.Value));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return "h:" + hex[..32];
    }

    public static (string GroupSetId, IReadOnlyList<Sid> FilteredGroups) FilterAndComputeGroupSet(
        IEnumerable<Sid>? groupSids,
        IReadOnlyCollection<string>? excludedPrefixes = null,
        IReadOnlyCollection<string>? includedPrefixes = null,
        int maxGroupsPerUser = DefaultMaxGroupsPerUser)
    {
        if (groupSids == null)
        {
            return (NoneGroupSetId, Array.Empty<Sid>());
        }

        var effectiveExcluded = excludedPrefixes ?? DefaultExcludedGroupSidPrefixes;
        var candidates = new HashSet<Sid>();

        foreach (var sid in groupSids)
        {
            if (string.IsNullOrEmpty(sid.Value))
            {
                continue;
            }

            bool isExcluded = false;
            foreach (var prefix in effectiveExcluded)
            {
                if (MatchesSidPrefix(sid.Value, prefix))
                {
                    isExcluded = true;
                    break;
                }
            }

            if (isExcluded)
            {
                continue;
            }

            if (includedPrefixes != null && includedPrefixes.Count > 0)
            {
                bool isIncluded = false;
                foreach (var prefix in includedPrefixes)
                {
                    if (MatchesSidPrefix(sid.Value, prefix))
                    {
                        isIncluded = true;
                        break;
                    }
                }

                if (!isIncluded)
                {
                    continue;
                }
            }

            candidates.Add(sid);
        }

        if (candidates.Count == 0)
        {
            return (NoneGroupSetId, Array.Empty<Sid>());
        }

        var sorted = candidates.OrderBy(s => s.Value, StringComparer.Ordinal).ToList();

        if (sorted.Count > maxGroupsPerUser)
        {
            UsageDiagnostics.GroupsTruncatedCounter.Add(1);
            sorted = sorted.Take(maxGroupsPerUser).ToList();
        }

        string payload = string.Join('\n', sorted.Select(s => s.Value));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        string groupSetId = hex[..32];

        return (groupSetId, sorted);
    }

    public static string ResolveBillingGroup(
        TenantId tenant,
        IReadOnlyCollection<Sid>? userGroupSids,
        IReadOnlyDictionary<string, List<string>>? billingGroups)
    {
        if (billingGroups == null || billingGroups.Count == 0 || userGroupSids == null || userGroupSids.Count == 0)
        {
            return UnassignedBillingGroup;
        }

        List<string>? configured = null;
        if (billingGroups.TryGetValue(tenant.Value, out var tenantList))
        {
            configured = tenantList;
        }
        else if (billingGroups.TryGetValue("*", out var wildcardList))
        {
            configured = wildcardList;
        }

        if (configured == null || configured.Count == 0)
        {
            return UnassignedBillingGroup;
        }

        var userSidSet = new HashSet<string>(userGroupSids.Select(s => s.Value), StringComparer.OrdinalIgnoreCase);

        foreach (var sid in configured)
        {
            if (userSidSet.Contains(sid))
            {
                return sid;
            }
        }

        return UnassignedBillingGroup;
    }

    public static UsageOutcome MapStatusCodeToOutcome(int statusCode, string? problemCode = null)
    {
        if (statusCode is >= 200 and < 400)
        {
            return UsageOutcome.Allow;
        }

        if (statusCode == 403)
        {
            return UsageOutcome.DenyAccess;
        }

        if (statusCode == 429)
        {
            if (string.Equals(problemCode, "USAGE_QUOTA_EXCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                return UsageOutcome.DenyQuota;
            }

            return UsageOutcome.DenyRateLimit;
        }

        if (statusCode is >= 400 and < 500)
        {
            return UsageOutcome.DenyOther;
        }

        if (statusCode >= 500)
        {
            return UsageOutcome.Error;
        }

        return UsageOutcome.Allow;
    }

    private static bool MatchesSidPrefix(string sidValue, string prefix)
    {
        if (prefix.EndsWith('-'))
        {
            return sidValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return sidValue.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
               sidValue.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase);
    }
}
