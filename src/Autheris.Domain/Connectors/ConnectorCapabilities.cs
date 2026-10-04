using System;

namespace Autheris.Domain.Connectors;

[Flags]
public enum ConnectorFeatures
{
    None = 0,
    FilterPushdown = 1 << 0,
    ProjectionPushdown = 1 << 1,
    LimitPushdown = 1 << 2,
    AggregationPushdown = 1 << 3,
    PartitionSplits = 1 << 4,
    StreamingExecution = 1 << 5
}

public sealed record ConnectorCapabilities(
    ConnectorFeatures Features,
    int MaxBatchSize = 5000,
    bool SupportsTransactions = false)
{
    public static readonly ConnectorCapabilities DefaultSql = new(
        ConnectorFeatures.FilterPushdown | ConnectorFeatures.ProjectionPushdown | ConnectorFeatures.LimitPushdown | ConnectorFeatures.StreamingExecution,
        MaxBatchSize: 5000,
        SupportsTransactions: true);

    public static readonly ConnectorCapabilities DefaultHttp = new(
        ConnectorFeatures.FilterPushdown | ConnectorFeatures.LimitPushdown,
        MaxBatchSize: 1000,
        SupportsTransactions: false);

    public bool HasFeature(ConnectorFeatures feature) => (Features & feature) == feature;
}
