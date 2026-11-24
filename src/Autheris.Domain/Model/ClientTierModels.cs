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
    bool ExposeCostExtensions,
    int? MaxConcurrentQueries = null,
    int? MaxQueryDurationSeconds = null,
    int? MaxResultRows = null)
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
                ExposeCostExtensions: overrideLimit.ExposeCostExtensions ?? defaultPolicy.ExposeCostExtensions,
                MaxConcurrentQueries: overrideLimit.MaxConcurrentQueries,
                MaxQueryDurationSeconds: overrideLimit.MaxQueryDurationSeconds,
                MaxResultRows: overrideLimit.MaxResultRows);
        }

        return defaultPolicy;
    }

    public static ClientQuotaPolicy ForScopeLimit(ClientTier tier, Options.ClientTierLimitOverride scopeLimit, Options.ClientTierOptions? tierOptions = null)
    {
        var basePolicy = ForTier(tier, tierOptions);
        if (scopeLimit == null) return basePolicy;

        return new ClientQuotaPolicy(
            tier,
            MaxCostPerQuery: scopeLimit.MaxCostPerQuery ?? basePolicy.MaxCostPerQuery,
            MaxComplexityDepth: scopeLimit.MaxComplexityDepth ?? basePolicy.MaxComplexityDepth,
            MaxTokensCapacity: scopeLimit.MaxTokensCapacity ?? basePolicy.MaxTokensCapacity,
            TokenRefillRatePerSecond: scopeLimit.TokenRefillRatePerSecond ?? basePolicy.TokenRefillRatePerSecond,
            ExposeCostExtensions: scopeLimit.ExposeCostExtensions ?? basePolicy.ExposeCostExtensions,
            MaxConcurrentQueries: MinNullable(basePolicy.MaxConcurrentQueries, scopeLimit.MaxConcurrentQueries),
            MaxQueryDurationSeconds: MinNullable(basePolicy.MaxQueryDurationSeconds, scopeLimit.MaxQueryDurationSeconds),
            MaxResultRows: MinNullable(basePolicy.MaxResultRows, scopeLimit.MaxResultRows));
    }

    public static ClientQuotaPolicy CombineStrictestScopeLimits(
        ClientTier tier,
        IReadOnlyList<Options.ClientTierLimitOverride> scopeLimits,
        Options.ClientTierOptions? tierOptions = null)
    {
        ArgumentNullException.ThrowIfNull(scopeLimits);
        if (scopeLimits.Count == 0) return ForTier(tier, tierOptions);
        if (scopeLimits.Count == 1) return ForScopeLimit(tier, scopeLimits[0], tierOptions);

        var current = ForScopeLimit(tier, scopeLimits[0], tierOptions);
        for (int i = 1; i < scopeLimits.Count; i++)
        {
            var next = scopeLimits[i];
            if (next is null) continue;
            current = new ClientQuotaPolicy(
                tier,
                MaxCostPerQuery: next.MaxCostPerQuery.HasValue ? Math.Min(current.MaxCostPerQuery, next.MaxCostPerQuery.Value) : current.MaxCostPerQuery,
                MaxComplexityDepth: next.MaxComplexityDepth.HasValue ? Math.Min(current.MaxComplexityDepth, next.MaxComplexityDepth.Value) : current.MaxComplexityDepth,
                MaxTokensCapacity: next.MaxTokensCapacity.HasValue ? Math.Min(current.MaxTokensCapacity, next.MaxTokensCapacity.Value) : current.MaxTokensCapacity,
                TokenRefillRatePerSecond: next.TokenRefillRatePerSecond.HasValue ? Math.Min(current.TokenRefillRatePerSecond, next.TokenRefillRatePerSecond.Value) : current.TokenRefillRatePerSecond,
                ExposeCostExtensions: current.ExposeCostExtensions && (next.ExposeCostExtensions ?? true),
                MaxConcurrentQueries: MinNullable(current.MaxConcurrentQueries, next.MaxConcurrentQueries),
                MaxQueryDurationSeconds: MinNullable(current.MaxQueryDurationSeconds, next.MaxQueryDurationSeconds),
                MaxResultRows: MinNullable(current.MaxResultRows, next.MaxResultRows));
        }

        return current;
    }

    private static int? MinNullable(int? current, int? candidate) => (current, candidate) switch
    {
        (null, null) => null,
        (not null, null) => current,
        (null, not null) => candidate,
        (not null, not null) => Math.Min(current.Value, candidate.Value)
    };
}

public readonly record struct CostQuotaResult(bool Allowed, int RemainingTokens, int RetryAfterSeconds);
