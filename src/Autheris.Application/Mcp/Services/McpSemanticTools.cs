namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

public static class McpSemanticTools
{
    public const string ListMetrics = "list_metrics";
    public const string DescribeMetric = "describe_metric";
    public const string QueryMetrics = "query_metrics";

    public static readonly TableIdentifier SemanticCatalogTable = new("governance", "catalog", "semantic_metrics");

    public static bool IsSemanticTool(string? toolName) =>
        string.Equals(toolName, ListMetrics, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, DescribeMetric, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, QueryMetrics, StringComparison.OrdinalIgnoreCase);

    public static bool ReadsRows(string? toolName) =>
        string.Equals(toolName, QueryMetrics, StringComparison.OrdinalIgnoreCase);

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
            TargetGraphQLOperation: ListMetrics,
            TargetTable: SemanticCatalogTable);

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
            TargetGraphQLOperation: DescribeMetric,
            TargetTable: SemanticCatalogTable);

        yield return new McpToolDefinition(
            Name: QueryMetrics,
            Description: "Executes one or more governed dbt Semantic Layer business metrics with optional dimensions (groupBy), filter predicate, and time granularity. Enforces caller RLS, column masking, and k-anonymity minimum group size.",
            InputJsonSchema: """
            {
              "type": "object",
              "required": ["metrics"],
              "properties": {
                "metrics": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "One or more semantic metric names to compute."
                },
                "groupBy": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional dimension names to group by."
                },
                "where": {
                  "type": "string",
                  "description": "Optional SQL/filter predicate on allowed dimensions."
                },
                "timeGranularity": {
                  "type": "string",
                  "description": "Optional time granularity (e.g. 'day', 'week', 'month', 'quarter', 'year')."
                },
                "limit": {
                  "type": "integer",
                  "description": "Maximum number of groups to return."
                }
              }
            }
            """,
            TargetGraphQLOperation: QueryMetrics,
            TargetTable: SemanticCatalogTable);
    }

    public static async Task<string> ExecuteListMetricsAsync(
        ISemanticCatalogService catalog,
        ClaimsPrincipal? principal,
        SemanticTenancy tenancy,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(tenancy);

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

        // A1-3 / A1-6: the caller's own tenant partition (no SID or no tenant standing = nothing); descriptive fields unless administrator.
        var tenant = tenancy.ResolveCallerTenant(principal);
        IReadOnlyList<DbtMetric> metrics = tenant is { } t
            ? await catalog.ListMetricsAsync(datasetFilter, t, ct).ConfigureAwait(false)
            : [];
        return JsonSerializer.Serialize(new
        {
            total = metrics.Count,
            metrics = SemanticMetricCatalogProjection.Project(metrics, principal)
        }, IndentedOptions);
    }

    public static async Task<string> ExecuteDescribeMetricAsync(
        ISemanticCatalogService catalog,
        ClaimsPrincipal? principal,
        SemanticTenancy tenancy,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(tenancy);

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

        var tenant = tenancy.ResolveCallerTenant(principal);
        var metric = tenant is { } t
            ? await catalog.GetMetricAsync(metricName, t, ct).ConfigureAwait(false)
            : null;
        if (metric == null)
        {
            return JsonSerializer.Serialize(new { error = $"Metric '{metricName}' was not found in the semantic catalog." });
        }

        return JsonSerializer.Serialize(SemanticMetricCatalogProjection.ProjectOne(metric, principal), IndentedOptions);
    }

    /// <summary>
    /// The metric names of a <c>query_metrics</c> call (<c>metrics</c> as array or string, else <c>metric</c>). Shared by the executor
    /// and the guardrail's four-eyes resolution, so both see the same metrics.
    /// </summary>
    public static IReadOnlyList<string> MetricNamesArgument(JsonElement root)
    {
        var metrics = new List<string>();
        if (root.TryGetProperty("metrics", out var mProp))
        {
            if (mProp.ValueKind == JsonValueKind.Array)
            {
                metrics.AddRange(mProp.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            else if (mProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(mProp.GetString()))
            {
                metrics.Add(mProp.GetString()!);
            }
        }
        else if (root.TryGetProperty("metric", out var singleProp) && singleProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(singleProp.GetString()))
        {
            metrics.Add(singleProp.GetString()!);
        }

        return metrics;
    }

    public static async Task<string> ExecuteQueryMetricsAsync(
        ISemanticMetricQueryService metricQueryService,
        ClaimsPrincipal principal,
        string? argumentsJson,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metricQueryService);
        ArgumentNullException.ThrowIfNull(principal);

        var metrics = new List<string>();
        List<string>? groupBy = null;
        string? whereSql = null;
        string? timeGranularity = null;
        int? limit = null;

        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                var root = doc.RootElement;

                metrics.AddRange(MetricNamesArgument(root));

                if (root.TryGetProperty("groupBy", out var gProp) && gProp.ValueKind == JsonValueKind.Array)
                {
                    groupBy = gProp.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                }

                if (root.TryGetProperty("where", out var wProp) && wProp.ValueKind == JsonValueKind.String)
                {
                    whereSql = wProp.GetString();
                }

                if (root.TryGetProperty("timeGranularity", out var tgProp) && tgProp.ValueKind == JsonValueKind.String)
                {
                    timeGranularity = tgProp.GetString();
                }

                if (root.TryGetProperty("limit", out var lProp) && lProp.TryGetInt32(out var lVal))
                {
                    limit = lVal;
                }
            }
            catch (JsonException)
            {
                return JsonSerializer.Serialize(new { error = "INVALID_JSON", message = "Arguments must be valid JSON." }, IndentedOptions);
            }
        }

        if (metrics.Count == 0)
        {
            return JsonSerializer.Serialize(new { error = "INVALID_PARAMS", message = "Argument 'metrics' is required." }, IndentedOptions);
        }

        TableFilterClause? whereClause = !string.IsNullOrWhiteSpace(whereSql)
            ? TableFilterClause.FromTrinoText(whereSql, Array.Empty<string>())
            : null;

        try
        {
            var result = await metricQueryService.QueryMetricsAsync(
                principal,
                new MetricQueryRequest(metrics, groupBy, whereClause, timeGranularity, Limit: limit),
                ct).ConfigureAwait(false);

            return JsonSerializer.Serialize(result, IndentedOptions);
        }
        catch (GatewaySecurityException secEx)
        {
            return JsonSerializer.Serialize(new
            {
                error = secEx.ErrorCode,
                message = secEx.Message
            }, IndentedOptions);
        }
    }
}
