namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Model;

public static class McpSemanticTools
{
    public const string ListMetrics = "list_metrics";
    public const string DescribeMetric = "describe_metric";

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IEnumerable<McpToolDefinition> Definitions()
    {
        yield return new McpToolDefinition(
            Name: ListMetrics,
            Description: "Lists all governed business metrics from the dbt Semantic Layer, with their dimensions and measures. Optional filter by dataset or semantic model.",
            InputJsonSchema: """
            {
              "type": "object",
              "properties": {
                "dataset": { "type": "string", "description": "Optional dataset/model name filter." }
              }
            }
            """,
            TargetGraphQLOperation: ListMetrics);

        yield return new McpToolDefinition(
            Name: DescribeMetric,
            Description: "Describes a business metric: calculation type, formula/expression, available dimensions, underlying measures, filter predicates and business documentation.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["metric"],
              "properties": {
                "metric": { "type": "string", "description": "Name of the metric (e.g. 'monthly_recurring_revenue')." }
              }
            }
            """,
            TargetGraphQLOperation: DescribeMetric);
    }

    public static async Task<string> ExecuteListMetricsAsync(
        ISemanticCatalogService catalog,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        string? datasetFilter = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("dataset", out var dsProp) && dsProp.ValueKind == JsonValueKind.String)
                {
                    datasetFilter = dsProp.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        var metrics = await catalog.ListMetricsAsync(datasetFilter, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            total = metrics.Count,
            metrics = metrics
        }, IndentedOptions);
    }

    public static async Task<string> ExecuteDescribeMetricAsync(
        ISemanticCatalogService catalog,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        string? metricName = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("metric", out var mProp) && mProp.ValueKind == JsonValueKind.String)
                {
                    metricName = mProp.GetString();
                }
            }
            catch (JsonException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(metricName))
        {
            return JsonSerializer.Serialize(new { error = "Argument 'metric' is required." });
        }

        var metric = await catalog.GetMetricAsync(metricName, ct).ConfigureAwait(false);
        if (metric == null)
        {
            return JsonSerializer.Serialize(new { error = $"Metric '{metricName}' was not found in the semantic catalog." });
        }

        return JsonSerializer.Serialize(metric, IndentedOptions);
    }
}
