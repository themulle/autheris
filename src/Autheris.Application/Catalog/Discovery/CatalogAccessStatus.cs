namespace Autheris.Application.Catalog.Discovery;

using System;
using Autheris.Domain.Model;

/// <summary>
/// Issues #798 / #804: the caller's access status as the catalog tools report it (<c>accessStatus</c> of <c>list_datasets</c>,
/// <c>search_catalog</c>, <c>find_relevant_tables</c>) and the parser of the <c>accessStatus</c> filter. The status is the caller's own
/// (<see cref="DiscoveryAccessState"/> of the discovery service); it never names another caller or a grant.
/// </summary>
public static class CatalogAccessStatus
{
    public const string Granted = "GRANTED";
    public const string Pending = "PENDING";
    public const string Expired = "EXPIRED";
    public const string RequestAccess = "REQUEST_ACCESS";

    /// <summary>Legacy name of <see cref="RequestAccess"/> (<c>CatalogVisibility.ComputeAccessStatus</c>); accepted as a filter value.</summary>
    public const string None = "NONE";

    /// <summary>Fixed error text of an unknown filter value; the request value is never echoed.</summary>
    public const string InvalidFilterMessage = "Invalid accessStatus filter. Allowed values: GRANTED, PENDING, EXPIRED, REQUEST_ACCESS.";

    /// <summary>The status string of <paramref name="state"/> as the MCP catalog tools report it.</summary>
    public static string Of(DiscoveryAccessState state) => state switch
    {
        DiscoveryAccessState.Granted => Granted,
        DiscoveryAccessState.Requested => Pending,
        DiscoveryAccessState.Expired => Expired,
        _ => RequestAccess
    };

    /// <summary>
    /// Parses a filter value, case-insensitively: the MCP names (<c>GRANTED</c>, <c>PENDING</c>, <c>EXPIRED</c>, <c>REQUEST_ACCESS</c>), the
    /// legacy <c>NONE</c> and the REST <c>callerState.access</c> names (<c>granted</c>, <c>requested</c>, <c>expired</c>, <c>none</c>).
    /// Null or blank means "no filter" (<paramref name="state"/> null, result true); any other value is invalid (false).
    /// </summary>
    public static bool TryParseFilter(string? value, out DiscoveryAccessState? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var v = value.Trim();
        if (v.Equals(Granted, StringComparison.OrdinalIgnoreCase))
        {
            state = DiscoveryAccessState.Granted;
        }
        else if (v.Equals(Pending, StringComparison.OrdinalIgnoreCase) || v.Equals("REQUESTED", StringComparison.OrdinalIgnoreCase))
        {
            state = DiscoveryAccessState.Requested;
        }
        else if (v.Equals(Expired, StringComparison.OrdinalIgnoreCase))
        {
            state = DiscoveryAccessState.Expired;
        }
        else if (v.Equals(RequestAccess, StringComparison.OrdinalIgnoreCase) || v.Equals(None, StringComparison.OrdinalIgnoreCase))
        {
            state = DiscoveryAccessState.None;
        }
        else
        {
            return false;
        }

        return true;
    }

    /// <summary>As <see cref="TryParseFilter"/>; an invalid value throws <see cref="ArgumentException"/> with <see cref="InvalidFilterMessage"/>.</summary>
    public static DiscoveryAccessState? ParseFilter(string? value) =>
        TryParseFilter(value, out var state) ? state : throw new ArgumentException(InvalidFilterMessage, nameof(value));

    /// <summary>True when the reported <paramref name="status"/> (either vocabulary) is the state <paramref name="filter"/> asks for.</summary>
    public static bool Matches(string? status, DiscoveryAccessState filter) =>
        TryParseFilter(status, out var state) && state == filter;
}
