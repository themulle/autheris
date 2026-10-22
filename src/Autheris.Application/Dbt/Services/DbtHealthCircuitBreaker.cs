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
    private readonly IDbtTestTargetMap? _testTargetMap;
    private readonly ILogger<DbtHealthCircuitBreaker>? _logger;

    public DbtHealthCircuitBreaker(
        IDbtTestTargetMap? testTargetMap = null,
        ILogger<DbtHealthCircuitBreaker>? logger = null)
    {
        _testTargetMap = testTargetMap;
        _logger = logger;
    }

    public DbtHealthCircuitBreaker(ILogger<DbtHealthCircuitBreaker>? logger)
        : this(null, logger)
    {
    }

    public ValueTask<DbtHealthState> GetTableHealthAsync(TableIdentifier table, CancellationToken ct = default)
    {
        // EXT-08: Exact match only; eliminate cross-tenant and prefix collisions
        if (_states.TryGetValue(table, out var exactState))
        {
            return ValueTask.FromResult(exactState);
        }

        // Default to Healthy if no adverse test run recorded
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

    public Task<DbtRunResultsReport> RecordRunResultsAsync(Stream runResultsStream, CancellationToken ct = default)
        => RecordRunResultsAsync(runResultsStream, TenantId.LegacySingleTenant, ct);

    public async Task<DbtRunResultsReport> RecordRunResultsAsync(Stream runResultsStream, TenantId tenant, CancellationToken ct = default)
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
        var tableFailures = new Dictionary<TableIdentifier, List<DbtTestFailure>>();
        var tableStatuses = new Dictionary<TableIdentifier, DbtModelHealthStatus>();
        var unattributedFailures = new List<string>();

        var knownModels = _testTargetMap?.GetKnownModels(tenant) ?? new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);

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
                    if ((_testTargetMap == null || !_testTargetMap.TryResolveModel(tenant, uniqueId, out var tableId)) &&
                        (_testTargetMap == null || !_testTargetMap.TryResolveModel(tenant, modelName, out tableId)) &&
                        !knownModels.TryGetValue(modelName, out tableId))
                    {
                        tableId = new TableIdentifier(tenant.Value, "default", modelName);
                    }

                    if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(status, "fail", StringComparison.OrdinalIgnoreCase))
                    {
                        tableStatuses[tableId] = DbtModelHealthStatus.Quarantined;
                        EnsureFailureList(tableFailures, tableId).Add(new DbtTestFailure(
                            TestName: "model_execution",
                            ModelName: tableId.TableName,
                            ColumnName: null,
                            Severity: "error",
                            Message: message,
                            FailedRowsCount: failuresCount));
                    }
                    else if (string.Equals(status, "warn", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!tableStatuses.TryGetValue(tableId, out var existing) || existing != DbtModelHealthStatus.Quarantined)
                        {
                            tableStatuses[tableId] = DbtModelHealthStatus.Degraded;
                        }
                    }
                    else if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(status, "pass", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!tableStatuses.ContainsKey(tableId))
                        {
                            tableStatuses[tableId] = DbtModelHealthStatus.Healthy;
                        }
                    }
                }
                else if (uniqueId.StartsWith("test.", StringComparison.OrdinalIgnoreCase))
                {
                    var isFail = string.Equals(status, "fail", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
                    var isWarn = string.Equals(status, "warn", StringComparison.OrdinalIgnoreCase);

                    if (isFail || isWarn)
                    {
                        var parts = uniqueId.Split('.');
                        var testPart = parts.Length >= 3 ? parts[2] : uniqueId;

                        IReadOnlyList<TableIdentifier> targetTables;
                        if (_testTargetMap != null && _testTargetMap.TryResolveTest(tenant, uniqueId, out var resolved) && resolved.Count > 0)
                        {
                            targetTables = resolved;
                        }
                        else
                        {
                            // Unresolvable test: candidate matching against tenant known model names
                            var candidates = FindCandidateModels(uniqueId, knownModels);
                            if (candidates.Count > 0)
                            {
                                targetTables = candidates;
                            }
                            else
                            {
                                unattributedFailures.Add(uniqueId);
                                _logger?.LogWarning("Unattributed test failure for {UniqueId}: no matching model found for tenant {Tenant}.", uniqueId, tenant);
                                targetTables = Array.Empty<TableIdentifier>();
                            }
                        }

                        foreach (var targetTable in targetTables)
                        {
                            var (testType, columnName) = ParseTestTypeAndColumn(testPart, targetTable.TableName);

                            if (isFail)
                            {
                                tableStatuses[targetTable] = DbtModelHealthStatus.Quarantined;
                                EnsureFailureList(tableFailures, targetTable).Add(new DbtTestFailure(
                                    TestName: testType,
                                    ModelName: targetTable.TableName,
                                    ColumnName: columnName,
                                    Severity: "error",
                                    Message: message,
                                    FailedRowsCount: failuresCount));
                            }
                            else if (isWarn)
                            {
                                if (!tableStatuses.TryGetValue(targetTable, out var current) || current != DbtModelHealthStatus.Quarantined)
                                {
                                    tableStatuses[targetTable] = DbtModelHealthStatus.Degraded;
                                }
                                EnsureFailureList(tableFailures, targetTable).Add(new DbtTestFailure(
                                    TestName: testType,
                                    ModelName: targetTable.TableName,
                                    ColumnName: columnName,
                                    Severity: "warn",
                                    Message: message,
                                    FailedRowsCount: failuresCount));
                            }
                        }
                    }
                }
            }
        }

        // Apply health state updates
        foreach (var (tableId, status) in tableStatuses)
        {
            var failures = tableFailures.TryGetValue(tableId, out var list) ? list : (IReadOnlyList<DbtTestFailure>)Array.Empty<DbtTestFailure>();
            await SetTableHealthAsync(tableId, status, failures, ct).ConfigureAwait(false);
        }

        return new DbtRunResultsReport(dbtVersion, generatedAt, totalElapsedTime, resultsList, unattributedFailures);
    }

    public Task ResetTableHealthAsync(TableIdentifier table, CancellationToken ct = default)
    {
        // EXT-08: Exact match only
        _states.TryRemove(table, out _);
        _logger?.LogInformation("dbt Circuit Breaker reset table health for {Table}.", table);
        return Task.CompletedTask;
    }

    public Task ResetAllAsync(CancellationToken ct = default)
    {
        _states.Clear();
        _logger?.LogInformation("dbt Circuit Breaker reset all table health states.");
        return Task.CompletedTask;
    }

    private static List<DbtTestFailure> EnsureFailureList(Dictionary<TableIdentifier, List<DbtTestFailure>> dict, TableIdentifier table)
    {
        if (!dict.TryGetValue(table, out var list))
        {
            list = new List<DbtTestFailure>();
            dict[table] = list;
        }
        return list;
    }

    private static string ExtractModelNameFromUniqueId(string uniqueId)
    {
        var parts = uniqueId.Split('.');
        return parts.Length > 0 ? parts[^1] : uniqueId;
    }

    private static List<TableIdentifier> FindCandidateModels(string testUniqueId, IReadOnlyDictionary<string, TableIdentifier> knownModels)
    {
        var parts = testUniqueId.Split('.');
        var testPart = parts.Length >= 3 ? parts[2] : testUniqueId;

        var candidates = new List<TableIdentifier>();
        string[] standardPrefixes = ["not_null_", "unique_", "accepted_values_", "relationships_"];

        foreach (var (modelName, tableId) in knownModels)
        {
            if (candidates.Contains(tableId)) continue;

            if (testPart.Equals(modelName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(tableId);
                continue;
            }

            var matched = false;
            foreach (var prefix in standardPrefixes)
            {
                if (testPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var rem = testPart[prefix.Length..];
                    if (rem.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
                        rem.StartsWith(modelName + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(tableId);
                        matched = true;
                        break;
                    }
                }
            }

            if (!matched)
            {
                if (testPart.Contains("_" + modelName + "_", StringComparison.OrdinalIgnoreCase) ||
                    testPart.EndsWith("_" + modelName, StringComparison.OrdinalIgnoreCase) ||
                    testPart.StartsWith(modelName + "_", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(tableId);
                }
            }
        }

        return candidates;
    }

    private static (string TestType, string? ColumnName) ParseTestTypeAndColumn(string testPart, string targetModelName)
    {
        string[] standardPrefixes = ["not_null_", "unique_", "accepted_values_", "relationships_"];
        foreach (var prefix in standardPrefixes)
        {
            if (testPart.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var testType = prefix.TrimEnd('_');
                var rem = testPart[prefix.Length..];
                if (rem.StartsWith(targetModelName + "_", StringComparison.OrdinalIgnoreCase))
                {
                    var col = rem[(targetModelName.Length + 1)..];
                    return (testType, col);
                }
                return (testType, null);
            }
        }

        var split = testPart.Split('_');
        return (split[0], null);
    }
}
