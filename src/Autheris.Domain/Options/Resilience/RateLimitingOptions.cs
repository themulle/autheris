using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class RateLimitingOptions
{
    [Required] public PreAuthIpRateLimitOptions PreAuthIpRateLimit { get; init; } = new();
    [Required] public PostAuthSidRateLimitOptions PostAuthSidRateLimit { get; init; } = new();
    public ClientTierOptions ClientTiers { get; init; } = new();
}

public sealed class ClientTierOptions
{
    public Dictionary<string, ClientTierLimitOverride> TierLimits { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RoleTierMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ApiKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ScopeTierMappings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ClientTierLimitOverride> ScopeLimits { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ClientTierLimitOverride
{
    public int? MaxCostPerQuery { get; init; }
    public int? MaxComplexityDepth { get; init; }
    public int? MaxTokensCapacity { get; init; }
    public double? TokenRefillRatePerSecond { get; init; }
    public bool? ExposeCostExtensions { get; init; }
}

public sealed class TierLimitDefinition : ClientTierLimitOverride
{
}

public sealed class PreAuthIpRateLimitOptions
{
    [Range(1, 100000)] public int PermitLimit { get; init; } = 100;
    [Range(1, 3600)] public int WindowSeconds { get; init; } = 60;
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
