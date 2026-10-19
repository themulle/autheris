using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class GraphQLOptions
{
    public string EndpointPath { get; init; } = "/graphql";
    [Range(1, 25)] public int MaxAllowedExecutionDepth { get; init; } = 6;
    [Range(100, 10000)] public int MaxAllowedComplexity { get; init; } = 500;
    public bool EnableIntrospection { get; init; }
    public bool PersistedQueriesOnly { get; init; }

    /// <summary>
    /// SEC H-08: Directory containing the trusted GraphQL documents (*.graphql / *.gql) that are allowed
    /// when PersistedQueriesOnly is true. Required when PersistedQueriesOnly is enabled (fail-fast at startup).
    /// </summary>
    public string TrustedDocumentsDirectory { get; init; } = string.Empty;
    public bool EnableBananaCakePop { get; init; }
    public string BananaCakePopPath { get; init; } = "/ui/bcp";
    [Range(100, 100000)] public int MaxResponseRows { get; init; } = 5000;
    [Range(1048576, 104857600)] public long MaxResponseBytes { get; init; } = 10485760;
    [Range(10, 10000)] public int MaxInClauseBatchSize { get; init; } = 500;
    // SEC M-13: Hard upper bound for root fields/aliases per GraphQL operation (alias amplification).
    [Range(1, 200)] public int MaxRootFieldsPerOperation { get; init; } = 10;
    /// <summary>
    /// SEC M-14 (GAP-B): Interval in seconds after which open WebSocket subscriptions re-check whether the
    /// caller's token was revoked (ITokenRevocationService). Revoked sessions are closed.
    /// </summary>
    [Range(5, 3600)] public int SubscriptionRevalidationSeconds { get; init; } = 60;

    /// <summary>
    /// R-GQL-12: interval in which the GraphQL catalog schema checks the catalog for changes and rebuilds itself
    /// (also after the governance database was unreachable at startup). 0 disables the check.
    /// </summary>
    [Range(0, 3600)] public int CatalogSchemaRefreshSeconds { get; init; } = 60;

    /// <summary>GQL-4: maximum number of open CDC subscriptions per subject (tenant + SID).</summary>
    [Range(1, 1000)] public int MaxSubscriptionsPerSubject { get; init; } = 10;
    [Range(100, 1000000)] public int MaxAggregateRowBudget { get; init; } = 50000;
    [Range(0, 10000)] public int MaxAllowedOffset { get; init; } = 10000;
    public List<string> TrustedOrigins { get; init; } = [];

    /// <summary>#112: typed aggregate fields (<c>Gateway:GraphQL:Aggregation</c>).</summary>
    public GraphQLAggregationOptions Aggregation { get; init; } = new();

    /// <summary>Issue #240: GraphQL metrics options (<c>Gateway:GraphQL:Metrics</c>).</summary>
    public GraphQLMetricsOptions Metrics { get; init; } = new();
}

/// <summary>Issue #240: switches for GraphQL metrics read path (queryMetrics).</summary>
public sealed class GraphQLMetricsOptions
{
    /// <summary>Enable the queryMetrics GraphQL root field. Default true.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>#112: switches and bounds of the typed GraphQL aggregation surface.</summary>
public sealed class GraphQLAggregationOptions
{
    public const int MinFieldsPerOperation = 1;
    public const int DefaultMaxFieldsPerOperation = 3;
    public const int MaxFieldsPerOperationUpperBound = 10;

    /// <summary>Generate the typed aggregate fields (restart or catalog change required).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Upper bound for aliased <c>T_aggregate</c> and <c>queryMetrics</c> fields in one operation (SEC-112-19). Out of range (1..10) is a
    /// start-up failure, never clamped.
    /// </summary>
    public int MaxFieldsPerOperation { get; init; } = DefaultMaxFieldsPerOperation;
}

/// <summary>
/// F-PERF-12: Incremental Delivery Options (@defer &amp; @stream).
/// Governs multipart streaming chunk delivery and connection resource bounds.
/// </summary>
public sealed class IncrementalDeliveryOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxDeferredExecutionTimeMs { get; init; } = 30000;
    public int MaxConcurrentStreamsPerClient { get; init; } = 10;
    public int MaxIncrementalChunks { get; init; } = 100;
}

public sealed class GoldenQueryOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxResultsPerRequest { get; init; } = 20;
    public List<GoldenQueryDefinition> InitialQueries { get; init; } = [];
}

public sealed record GoldenQueryDefinition(
    string Id,
    string Domain,
    string TableName,
    string Title,
    string Description,
    string QueryText,
    string? VariablesJson = null,
    List<string>? Tags = null
);
