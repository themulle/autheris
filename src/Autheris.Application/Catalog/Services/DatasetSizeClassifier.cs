namespace Autheris.Application.Catalog.Services;

using System;
using Autheris.Domain.Model;

/// <summary>
/// Issue #572: Computes coarse dataset size class without querying the underlying source database.
/// Exact counts are never emitted to query clients.
/// </summary>
public static class DatasetSizeClassifier
{
    public static string Classify(long? rowCount)
    {
        if (rowCount is null or < 0)
        {
            return McpDatasetSize.Unknown;
        }

        return rowCount.Value switch
        {
            < 1_000 => McpDatasetSize.Tiny,
            < 100_000 => McpDatasetSize.Small,
            < 10_000_000 => McpDatasetSize.Medium,
            < 1_000_000_000 => McpDatasetSize.Large,
            _ => McpDatasetSize.Huge
        };
    }

    public static McpDatasetSize Resolve(TableMetadata? table)
    {
        if (table is null)
        {
            return new McpDatasetSize(McpDatasetSize.Unknown, McpDatasetSize.SourceUnknown, null);
        }

        var asOf = table.IngestedAt;

        if (table.EstimatedRowCount is { } est and >= 0)
        {
            return new McpDatasetSize(Classify(est), McpDatasetSize.SourceDbtMeta, asOf);
        }

        var declared = table.ExpectedRows ?? table.Table?.ExpectedRows;
        if (declared is { } exp and >= 0)
        {
            return new McpDatasetSize(Classify(exp), McpDatasetSize.SourceDeclared, asOf);
        }

        return new McpDatasetSize(McpDatasetSize.Unknown, McpDatasetSize.SourceUnknown, asOf);
    }
}
