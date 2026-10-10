namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>Outcome class of <see cref="VirtualFilterPushdownSelector.Select"/>.</summary>
internal enum PushdownStatus
{
    /// <summary>The keys can be pushed.</summary>
    Pushable,

    /// <summary>The endpoint, the binding format or a key value rules a pushdown out.</summary>
    Unavailable,

    /// <summary>A pushdown is possible but exceeds a key, URL or request budget.</summary>
    OverLimit,

    /// <summary>The keys of the pairs on the key column have no common member: nothing can be authorized.</summary>
    Disjoint
}

/// <summary><paramref name="PathKeyField"/> is set when the values are sent one per request into the path template.</summary>
internal sealed record PushdownCandidate(
    PushdownStatus Status, string Reason, string? ParameterName = null, IReadOnlyList<string>? Values = null, string? PathKeyField = null);

/// <summary>One (filter, binding) pair with its key set and, for a point lookup, the queried keys.</summary>
internal sealed record PushdownPair(
    AppliedVirtualFilterBinding Pair,
    string[] Columns,
    VirtualFilterKeySet Keys,
    VirtualFilterKey[]? Queried);

/// <summary>
/// F-GOV-14 (WI-8, M7, SEC-15, SEC-21): chooses the values to push to an upstream. Only single-key pairs on the
/// endpoint's key column contribute; the pushed values are the intersection of their key sets (narrowed to the queried
/// keys for a point lookup), so an unauthorized key is never sent. The gateway enforcement still filters every row.
/// </summary>
internal static class VirtualFilterPushdownSelector
{
    public static PushdownCandidate Select(TableMetadata table, IReadOnlyList<PushdownPair> pairs, VirtualFilterFederationOptions options)
    {
        var capability = HttpKeyPushdownCapability.Resolve(table.HttpEndpoint);
        if (capability.Kind == HttpKeyPushdownKind.None)
        {
            return new(PushdownStatus.Unavailable, "no_capability");
        }

        var contributing = pairs
            .Where(p => p.Columns.Length == 1 && string.Equals(p.Columns[0], capability.KeyField, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (contributing.Count == 0)
        {
            return new(PushdownStatus.Unavailable, "no_key_column");
        }

        if (contributing.Any(p => p.Pair.Binding.PushdownFormat is { } format && !Satisfies(capability.Kind, format)))
        {
            return new(PushdownStatus.Unavailable, "format_unsupported");
        }

        HashSet<VirtualFilterKey>? common = null;
        foreach (var pair in contributing)
        {
            var candidates = pair.Queried != null ? pair.Queried.Where(pair.Keys.Contains) : pair.Keys.Keys;
            if (common == null)
            {
                common = [.. candidates];
            }
            else
            {
                common.IntersectWith(candidates);
            }
        }

        if (common!.Count == 0)
        {
            return new(PushdownStatus.Disjoint, "key_sets_disjoint");
        }

        int limit = Math.Min(options.MaxPushdownKeys, contributing.Min(p => p.Pair.Binding.MaxPushdownKeys));
        if (common.Count > limit)
        {
            return new(PushdownStatus.OverLimit, "key_count_over_limit");
        }

        var values = common.Select(k => VirtualFilterKeyCanonicalizer.FormatCanonical(k[0])).OrderBy(v => v, StringComparer.Ordinal).ToList();
        if (values.Any(v => !capability.IsValueEligible(v)))
        {
            return new(PushdownStatus.Unavailable, "value_not_pushable");
        }

        if (capability.Kind == HttpKeyPushdownKind.PathLookup && values.Count > options.MaxPathLookupRequests)
        {
            return new(PushdownStatus.OverLimit, "path_requests_over_limit");
        }

        if (capability.Kind == HttpKeyPushdownKind.QueryParameterList &&
            EstimateUrlLength(table.HttpEndpoint!, capability.ParameterName, values) > options.MaxPushdownUrlLength)
        {
            return new(PushdownStatus.OverLimit, "url_too_long");
        }

        return new(PushdownStatus.Pushable, "pushdown", capability.ParameterName, values, capability.Kind == HttpKeyPushdownKind.PathLookup ? capability.KeyField : null);
    }

    private static bool Satisfies(HttpKeyPushdownKind kind, PushdownParameterFormat format) =>
        (kind == HttpKeyPushdownKind.QueryParameterList && format == PushdownParameterFormat.CommaSeparated) ||
        (kind == HttpKeyPushdownKind.JsonBodyArray && format == PushdownParameterFormat.PostBatch);

    private static int EstimateUrlLength(HttpEndpointDescriptor descriptor, string parameter, IReadOnlyList<string> values)
    {
        // Base + path + "?" + name + "=" + encoded values and separators, plus headroom for the tenant parameter.
        int length = descriptor.BaseUrl.Length + descriptor.PathTemplate.Length + parameter.Length + 2 + 64;
        foreach (var value in values)
        {
            length += Uri.EscapeDataString(value).Length + 1;
        }

        return length;
    }
}
