namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class DbtHealthCircuitBreaker : IDbtHealthCircuitBreaker
{
    private readonly ConcurrentDictionary<TableIdentifier, DbtHealthState> _states = new();
    private readonly ILogger<DbtHealthCircuitBreaker>? _logger;

    public DbtHealthCircuitBreaker(ILogger<DbtHealthCircuitBreaker>? logger = null)
    {
        _logger = logger;
    }

    public ValueTask<DbtHealthState> GetTableHealthAsync(TableIdentifier table, CancellationToken ct = default)
    {
        // 1. Direct exact match
        if (_states.TryGetValue(table, out var exactState))
        {
            return ValueTask.FromResult(exactState);
        }

        // 2. Fallback match by Table name (case-insensitive) if schema/database were not part of run_results
        foreach (var (storedTable, state) in _states)
        {
            if (string.Equals(storedTable.TableName, table.TableName, StringComparison.OrdinalIgnoreCase) ||
                storedTable.TableName.StartsWith(table.TableName + "_", StringComparison.OrdinalIgnoreCase))
            {
                return ValueTask.FromResult(state);
            }
        }

        // 3. Default to Healthy if no adverse test run recorded
        var defaultHealthy = new DbtHealthState(
            table,
            DbtModelHealthStatus.Healthy,
            Array.Empty<DbtTestFailure>(),
            DateTimeOffset.UtcNow);

        return ValueTask.FromResult(defaultHealthy);
    }

    public Task<DbtHealthState> SetTableHealthAsync(
        TableIdentifier table,
        DbtModelHealthStatus status,
        IReadOnlyList<DbtTestFailure> failures,
        CancellationToken ct = default)
    {
        var newState = new DbtHealthState(table, status, failures, DateTimeOffset.UtcNow);
        _states[table] = newState;

        _logger?.LogInformation(
            "dbt Circuit Breaker updated table {Table} to status {Status} with {Count} failure(s).",
            table, status, failures.Count);

        return Task.FromResult(newState);
    }

    public Task<IReadOnlyDictionary<TableIdentifier, DbtHealthState>> GetAllHealthStatesAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<TableIdentifier, DbtHealthState> snapshot =
            new Dictionary<TableIdentifier, DbtHealthState>(_states);
        return Task.FromResult(snapshot);
    }

    public async Task<DbtRunResultsReport> RecordRunResultsAsync(Stream runResultsStream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runResultsStream);

        var jsonOptions = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        };

        using var document = await JsonDocument.ParseAsync(runResultsStream, jsonOptions, ct).ConfigureAwait(false);
        var root = document.RootElement;

        // Parse Metadata
        var dbtVersion = "unknown";
        var generatedAt = DateTimeOffset.UtcNow;
        var totalElapsedTime = TimeSpan.Zero;

        if (root.TryGetProperty("metadata", out var metaElem))
        {
            if (metaElem.TryGetProperty("dbt_version", out var verProp))
            {
                dbtVersion = verProp.GetString() ?? "unknown";
            }
            if (metaElem.TryGetProperty("generated_at", out var genProp) && genProp.TryGetDateTimeOffset(out var dt))
            {
                generatedAt = dt;
            }
        }

        if (root.TryGetProperty("elapsed_time", out var elProp) && elProp.TryGetDouble(out var elSec))
        {
            totalElapsedTime = TimeSpan.FromSeconds(elSec);
        }

        var resultsList = new List<DbtModelExecutionResult>();
        var modelFailures = new Dictionary<string, List<DbtTestFailure>>(StringComparer.OrdinalIgnoreCase);
        var modelStatuses = new Dictionary<string, DbtModelHealthStatus>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("results", out var resultsElem) && resultsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in resultsElem.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                var uniqueId = item.TryGetProperty("unique_id", out var uidProp) ? uidProp.GetString() ?? "" : "";
                var status = item.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "" : "";
                var message = item.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : null;
                var execTime = TimeSpan.Zero;
                if (item.TryGetProperty("execution_time", out var extProp) && extProp.TryGetDouble(out var sec))
                {
                    execTime = TimeSpan.FromSeconds(sec);
                }

                int? failuresCount = null;
                if (item.TryGetProperty("failures", out var fProp) && fProp.TryGetInt32(out var fc))
                {
                    failuresCount = fc;
                }

                resultsList.Add(new DbtModelExecutionResult(uniqueId, status, execTime, failuresCount, message));

                if (uniqueId.StartsWith("model.", StringComparison.OrdinalIgnoreCase) ||
                    uniqueId.StartsWith("seed.", StringComparison.OrdinalIgnoreCase))
                {
                    var modelName = ExtractModelNameFromUniqueId(uniqueId);
                    if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "fail", StringComparison.OrdinalIgnoreCase))
                    {
                        modelStatuses[modelName] = DbtModelHealthStatus.Quarantined;
                        EnsureFailureList(modelFailures, modelName).Add(new DbtTestFailure(
                            TestName: "model_execution",
                            ModelName: modelName,
                            ColumnName: null,
                            Severity: "error",
                            Message: message,
                            FailedRowsCount: failuresCount));
                    }
                    else if (string.Equals(status, "warn", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!modelStatuses.TryGetValue(modelName, out var existing) || existing != DbtModelHealthStatus.Quarantined)
                        {
                            modelStatuses[modelName] = DbtModelHealthStatus.Degraded;
                        }
                    }
                    else if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(status, "pass", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!modelStatuses.ContainsKey(modelName))
                        {
                            modelStatuses[modelName] = DbtModelHealthStatus.Healthy;
                        }
                    }
                }
                else if (uniqueId.StartsWith("test.", StringComparison.OrdinalIgnoreCase))
                {
                    var (testName, targetModel, columnName) = ParseTestUniqueId(uniqueId);
                    var isFail = string.Equals(status, "fail", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
                    var isWarn = string.Equals(status, "warn", StringComparison.OrdinalIgnoreCase);

                    if (isFail)
                    {
                        modelStatuses[targetModel] = DbtModelHealthStatus.Quarantined;
                        EnsureFailureList(modelFailures, targetModel).Add(new DbtTestFailure(
                            TestName: testName,
                            ModelName: targetModel,
                            ColumnName: columnName,
                            Severity: "error",
                            Message: message,
                            FailedRowsCount: failuresCount));
                    }
                    else if (isWarn)
                    {
                        if (!modelStatuses.TryGetValue(targetModel, out var current) || current != DbtModelHealthStatus.Quarantined)
                        {
                            modelStatuses[targetModel] = DbtModelHealthStatus.Degraded;
                        }
                        EnsureFailureList(modelFailures, targetModel).Add(new DbtTestFailure(
                            TestName: testName,
                            ModelName: targetModel,
                            ColumnName: columnName,
                            Severity: "warn",
                            Message: message,
                            FailedRowsCount: failuresCount));
                    }
                }
            }
        }

        // Apply health state updates
        foreach (var (modelName, status) in modelStatuses)
        {
            var failures = modelFailures.TryGetValue(modelName, out var list) ? list : (IReadOnlyList<DbtTestFailure>)Array.Empty<DbtTestFailure>();
            var tableId = new TableIdentifier("default", "default", modelName);
            await SetTableHealthAsync(tableId, status, failures, ct).ConfigureAwait(false);
        }

        return new DbtRunResultsReport(dbtVersion, generatedAt, totalElapsedTime, resultsList);
    }

    public Task ResetTableHealthAsync(TableIdentifier table, CancellationToken ct = default)
    {
        _states.TryRemove(table, out _);

        // Also clean up partial entries matching table name
        var matchingKeys = _states.Keys
            .Where(k => string.Equals(k.TableName, table.TableName, StringComparison.OrdinalIgnoreCase) ||
                        k.TableName.StartsWith(table.TableName + "_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in matchingKeys)
        {
            _states.TryRemove(key, out _);
        }

        _logger?.LogInformation("dbt Circuit Breaker reset table health for {Table}.", table);
        return Task.CompletedTask;
    }

    public Task ResetAllAsync(CancellationToken ct = default)
    {
        _states.Clear();
        _logger?.LogInformation("dbt Circuit Breaker reset all table health states.");
        return Task.CompletedTask;
    }

    private static List<DbtTestFailure> EnsureFailureList(Dictionary<string, List<DbtTestFailure>> dict, string modelName)
    {
        if (!dict.TryGetValue(modelName, out var list))
        {
            list = new List<DbtTestFailure>();
            dict[modelName] = list;
        }
        return list;
    }

    private static string ExtractModelNameFromUniqueId(string uniqueId)
    {
        // e.g. "model.analytics.monthly_revenue" -> "monthly_revenue"
        var parts = uniqueId.Split('.');
        return parts.Length > 0 ? parts[^1] : uniqueId;
    }

    private static (string TestName, string TargetModel, string? ColumnName) ParseTestUniqueId(string uniqueId)
    {
        // dbt test unique_id format: "test.<project>.<test_name_with_model_and_column>.<hash>"
        var parts = uniqueId.Split('.');
        if (parts.Length < 3)
        {
            return (uniqueId, "unknown", null);
        }

        var fullTestIdentifier = parts[2];

        // Check common test prefixes
        string[] standardPrefixes = ["not_null_", "unique_", "accepted_values_", "relationships_"];
        foreach (var prefix in standardPrefixes)
        {
            if (fullTestIdentifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var testType = prefix.TrimEnd('_');
                var remainder = fullTestIdentifier[prefix.Length..];

                // Split at the last underscore: everything before is model name, after is column name
                var lastUnderscore = remainder.LastIndexOf('_');
                if (lastUnderscore > 0)
                {
                    var model = remainder[..lastUnderscore];
                    var col = remainder[(lastUnderscore + 1)..];
                    return (testType, model, col);
                }

                return (testType, remainder, null);
            }
        }

        // Generic fallback
        var testParts = fullTestIdentifier.Split('_');
        var fallbackModel = testParts.Length > 1 ? testParts[^1] : parts[1];
        return (fullTestIdentifier, fallbackModel, null);
    }
}
