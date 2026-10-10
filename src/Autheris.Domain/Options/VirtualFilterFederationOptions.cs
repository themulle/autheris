using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>
/// F-GOV-14: Virtual Filters on Web-API tables in WebSQL cross-source queries (<c>VirtualFilters:Federation</c>).
/// Authorized key sets are loaded from the filter's SQL key source, bounded and cached per tenant and definition.
/// </summary>
public sealed class VirtualFilterFederationOptions
{
    /// <summary>Lifetime (seconds) of a cached authorized key set; 1..300.</summary>
    [Range(1, 300)] public int KeyCacheTtlSeconds { get; init; } = 30;

    /// <summary>Largest authorized key set; one more key denies with <c>VF_KEY_SET_TOO_LARGE</c> (never truncated).</summary>
    [Range(1, 1_000_000)] public int MaxAuthorizedKeys { get; init; } = 50_000;

    /// <summary>Command timeout (seconds) of the key query; 1..300.</summary>
    [Range(1, 300)] public int KeyQueryTimeoutSeconds { get; init; } = 10;

    /// <summary>Size limit (bytes) of the dedicated key set cache (separate from the shared memory cache).</summary>
    [Range(1L * 1024 * 1024, 4L * 1024 * 1024 * 1024)] public long KeyCacheSizeLimitBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Allows the gateway to narrow what is fetched upstream (point-lookup short-circuit, key pushdown). When off, every
    /// protected table is read completely (Tier 3) and filtered by the gateway; enforcement is unaffected. What the
    /// switch cannot do is read a table the bindings only allow to be narrowed: a <c>ShortCircuitOnly</c> or
    /// <c>PushdownOnly</c> binding needs the narrowing and is denied (<c>VF_PUSHDOWN_UNAVAILABLE</c>) while it is off, and
    /// an endpoint with a path placeholder (path lookup) is never read as a full scan
    /// (<c>VF_PATH_TEMPLATE_UNFILLABLE</c>). It is a kill switch for the narrowing, not a way to get more rows.
    /// </summary>
    public bool PushdownEnabled { get; init; } = true;

    /// <summary>Largest number of key tuples a point lookup may expand to (IN lists of composite keys); more is a scan.</summary>
    [Range(1, 10_000)] public int MaxPointLookupTuples { get; init; } = 1_000;

    /// <summary>Largest number of keys pushed to an upstream in one lookup (a binding may lower it); more is Tier 3 or a denial.</summary>
    [Range(1, 10_000)] public int MaxPushdownKeys { get; init; } = 1_000;

    /// <summary>Largest estimated request URL (characters) of a query-parameter pushdown.</summary>
    [Range(256, 65_536)] public int MaxPushdownUrlLength { get; init; } = 2_048;

    /// <summary>Largest number of single-key requests of a path lookup pushdown.</summary>
    [Range(1, 100)] public int MaxPathLookupRequests { get; init; } = 10;

    /// <summary>Throws when a value is out of range (fail closed at startup instead of at query time).</summary>
    public void Validate()
    {
        Check(KeyCacheTtlSeconds, 1, 300, nameof(KeyCacheTtlSeconds));
        Check(MaxAuthorizedKeys, 1, 1_000_000, nameof(MaxAuthorizedKeys));
        Check(KeyQueryTimeoutSeconds, 1, 300, nameof(KeyQueryTimeoutSeconds));
        Check(MaxPointLookupTuples, 1, 10_000, nameof(MaxPointLookupTuples));
        Check(MaxPushdownKeys, 1, 10_000, nameof(MaxPushdownKeys));
        Check(MaxPushdownUrlLength, 256, 65_536, nameof(MaxPushdownUrlLength));
        Check(MaxPathLookupRequests, 1, 100, nameof(MaxPathLookupRequests));
        Check(KeyCacheSizeLimitBytes, 1L * 1024 * 1024, 4L * 1024 * 1024 * 1024, nameof(KeyCacheSizeLimitBytes));
    }

    private static void Check(long value, long min, long max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"VirtualFilters:Federation:{name} must be between {min} and {max}.");
        }
    }
}
