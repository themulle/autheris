using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class RateLimitingOptions
{
    [Required] public PreAuthIpRateLimitOptions PreAuthIpRateLimit { get; init; } = new();
    [Required] public PostAuthSidRateLimitOptions PostAuthSidRateLimit { get; init; } = new();

    /// <summary>Per-role, per-scope and per-API-key client tier rate limit overrides (<c>Gateway:RateLimiting:ClientTiers</c>).</summary>
    public ClientTierOptions ClientTiers { get; init; } = new();
}

/// <summary>Client tier rate limit and per-scope execution limit definitions and mappings.</summary>
public sealed class ClientTierOptions
{
    /// <summary>Named rate limit and execution cap overrides keyed by tier name (supports MaxCostPerQuery, MaxComplexityDepth, MaxTokensCapacity, TokenRefillRatePerSecond, ExposeCostExtensions, MaxConcurrentQueries, MaxQueryDurationSeconds, and MaxResultRows).</summary>
    public Dictionary<string, ClientTierLimitOverride> TierLimits { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mapping from gateway role names to client rate-limit tier names.</summary>
    public Dictionary<string, string> RoleTierMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mapping from API key identifiers to client rate-limit tier names.</summary>
    public Dictionary<string, string> ApiKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mapping from OAuth scope names to client rate-limit tier names.</summary>
    public Dictionary<string, string> ScopeTierMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Direct rate-limit and query execution overrides keyed by OAuth scope name (supports MaxCostPerQuery, MaxComplexityDepth, MaxTokensCapacity, TokenRefillRatePerSecond, ExposeCostExtensions, MaxConcurrentQueries, MaxQueryDurationSeconds, and MaxResultRows; the strictest lower bound wins).</summary>
    public Dictionary<string, ClientTierLimitOverride> ScopeLimits { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ClientTierLimitOverride
{
    /// <summary>Maximum allowed cost for a single query.</summary>
    public int? MaxCostPerQuery { get; init; }

    /// <summary>Maximum allowed GraphQL query complexity depth.</summary>
    public int? MaxComplexityDepth { get; init; }

    /// <summary>Token bucket capacity for cost rate limiting.</summary>
    public int? MaxTokensCapacity { get; init; }

    /// <summary>Cost token refill rate per second.</summary>
    public double? TokenRefillRatePerSecond { get; init; }

    /// <summary>Whether cost extensions are exposed in GraphQL responses.</summary>
    public bool? ExposeCostExtensions { get; init; }

    /// <summary>Optional maximum concurrent read queries permitted per subject across GraphQL, WebSQL, REST data, OData, MCP, and export paths (the lower of tier, scope, and global limits wins).</summary>
    public int? MaxConcurrentQueries { get; init; }

    /// <summary>Optional maximum query execution duration in seconds, enforced down to the data source cancellation token and command timeout (the lower of tier, scope, and global limits wins).</summary>
    public int? MaxQueryDurationSeconds { get; init; }

    /// <summary>Optional maximum result rows per query across all read paths (the lower of tier, scope, and global limits wins).</summary>
    public int? MaxResultRows { get; init; }
}

public sealed class TierLimitDefinition : ClientTierLimitOverride
{
}

public sealed class PreAuthIpRateLimitOptions
{
    [Range(1, 100000)] public int PermitLimit { get; init; } = 100;
    [Range(1, 3600)] public int WindowSeconds { get; init; } = 60;

    /// <summary>Maximum number of queued pre-auth requests permitted when the IP bucket is exhausted.</summary>
    public int QueueLimit { get; init; }

    /// <summary>
    /// When true, requests carrying an Authorization: Bearer token are exempt from the pre-auth IP bucket
    /// and are instead governed by the per-identity post-auth SID and cost rate limiters.
    /// Prevents IP rate exhaustion for multi-user client services behind a single NAT/gateway IP.
    /// </summary>
    public bool ExemptAuthenticatedRequests { get; init; } = true;
}

public sealed class PostAuthSidRateLimitOptions
{
    [Range(10, 100000)] public int TokenBucketCapacity { get; init; } = 500;
    [Range(1, 10000)] public int TokensPerSecond { get; init; } = 50;
}
