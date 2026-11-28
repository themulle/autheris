namespace Autheris.Domain.Model;

/// <summary>
/// One effective limit of the calling token. <see cref="Value"/> is null when nothing bounds it. <see cref="Source"/> names where the value
/// comes from: <c>installation</c> (configuration or built-in default), <c>tier</c> (the tier policy of the caller) or <c>scope</c> (a scope
/// limit of the caller's own token lowered it). Never carries a scope name, another tier or another caller's value.
/// </summary>
public sealed record LimitValue(long? Value, string Source)
{
    public bool Unlimited => Value is null;
}

/// <summary>Reads that run at the same time and the wait for a free slot.</summary>
public sealed record ConcurrencyLimits(LimitValue ReadsPerTable, LimitValue TableReadWaitMs, LimitValue QueriesAtOnce);

/// <summary>Limits of a governed SQL statement (WebSQL, the MCP SQL tool).</summary>
public sealed record SqlQueryLimits(
    LimitValue DefaultRows,
    LimitValue MaxRowsWithLimit,
    LimitValue MaxOffset,
    LimitValue MaxListValues,
    LimitValue MaxListParameters,
    LimitValue ExecutionTimeoutSeconds,
    LimitValue PageTokenLifetimeMinutes);

/// <summary>Limits of a GraphQL operation.</summary>
public sealed record GraphQlQueryLimits(
    LimitValue MaxResponseRows,
    LimitValue MaxResponseBytes,
    LimitValue MaxOffset,
    LimitValue MaxCostPerQuery);

/// <summary>The effective query limits of the calling token after installation, tier and scope are resolved. The SQL section is absent when WebSQL is disabled.</summary>
public sealed record CallerQueryLimits(
    string Tier,
    ConcurrencyLimits Concurrency,
    LimitValue MaxQueryDurationSeconds,
    LimitValue MaxResultRows,
    SqlQueryLimits? Sql,
    GraphQlQueryLimits GraphQl);
