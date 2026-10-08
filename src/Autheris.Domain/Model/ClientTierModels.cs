namespace Autheris.Domain.Model;

public enum ClientTier
{
    Free = 1,
    Standard = 2,
    Enterprise = 3,
    Internal = 4
}

public sealed record ClientQuotaPolicy(
    ClientTier Tier,
    int MaxCostPerQuery,
    int MaxComplexityDepth,
    int MaxTokensCapacity,
    double TokenRefillRatePerSecond,
    bool ExposeCostExtensions)
{
    public static ClientQuotaPolicy ForTier(ClientTier tier, Options.ClientTierOptions? tierOptions = null)
    {
        var defaultPolicy = tier switch
        {
            ClientTier.Free => new ClientQuotaPolicy(tier, MaxCostPerQuery: 50, MaxComplexityDepth: 5, MaxTokensCapacity: 100, TokenRefillRatePerSecond: 2.0, ExposeCostExtensions: false),
            ClientTier.Standard => new ClientQuotaPolicy(tier, MaxCostPerQuery: 250, MaxComplexityDepth: 10, MaxTokensCapacity: 1000, TokenRefillRatePerSecond: 20.0, ExposeCostExtensions: true),
            ClientTier.Enterprise => new ClientQuotaPolicy(tier, MaxCostPerQuery: 1000, MaxComplexityDepth: 20, MaxTokensCapacity: 10000, TokenRefillRatePerSecond: 200.0, ExposeCostExtensions: true),
            ClientTier.Internal => new ClientQuotaPolicy(tier, MaxCostPerQuery: 5000, MaxComplexityDepth: 30, MaxTokensCapacity: 50000, TokenRefillRatePerSecond: 1000.0, ExposeCostExtensions: true),
            _ => throw new ArgumentOutOfRangeException(nameof(tier))
        };

        if (tierOptions?.TierLimits != null &&
            tierOptions.TierLimits.TryGetValue(tier.ToString(), out var overrideLimit) &&
            overrideLimit != null)
        {
            return new ClientQuotaPolicy(
                tier,
                MaxCostPerQuery: overrideLimit.MaxCostPerQuery ?? defaultPolicy.MaxCostPerQuery,
                MaxComplexityDepth: overrideLimit.MaxComplexityDepth ?? defaultPolicy.MaxComplexityDepth,
                MaxTokensCapacity: overrideLimit.MaxTokensCapacity ?? defaultPolicy.MaxTokensCapacity,
                TokenRefillRatePerSecond: overrideLimit.TokenRefillRatePerSecond ?? defaultPolicy.TokenRefillRatePerSecond,
                ExposeCostExtensions: overrideLimit.ExposeCostExtensions ?? defaultPolicy.ExposeCostExtensions);
        }

        return defaultPolicy;
    }
}

public readonly record struct CostQuotaResult(bool Allowed, int RemainingTokens, int RetryAfterSeconds);
