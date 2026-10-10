namespace Autheris.Application.VirtualFilters.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default implementation of <see cref="IVirtualFilterKeyProvider"/> that extracts authorized keys
/// from the virtual filter's backing data source under strict Fail-Closed security invariants.
/// </summary>
public sealed class DefaultVirtualFilterKeyProvider : IVirtualFilterKeyProvider
{
    private readonly IAutherisConnectorRegistry? _connectorRegistry;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly IMemoryCache? _cache;
    private readonly ILogger<DefaultVirtualFilterKeyProvider>? _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> _inMemoryTuples = new(StringComparer.OrdinalIgnoreCase);

    public DefaultVirtualFilterKeyProvider(
        IAutherisConnectorRegistry? connectorRegistry = null,
        ITableMetadataRepository? tableRepository = null,
        IMemoryCache? cache = null,
        ILogger<DefaultVirtualFilterKeyProvider>? logger = null)
    {
        _connectorRegistry = connectorRegistry;
        _tableRepository = tableRepository;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// For testing and in-memory scenarios: registers pre-configured authorized tuples for a filter.
    /// </summary>
    public void SetInMemoryTuples(string filterName, IReadOnlyList<IReadOnlyDictionary<string, object?>> tuples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filterName);
        ArgumentNullException.ThrowIfNull(tuples);
        _inMemoryTuples[filterName] = tuples;
    }

    public async ValueTask<IReadOnlySet<string>> GetAllowedKeysAsync(
        VirtualFilter filter,
        ClaimsPrincipal user,
        TenantId tenantId,
        string keyColumn,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);

        var tuples = await GetAllowedKeyTuplesAsync(filter, user, tenantId, [keyColumn], ct).ConfigureAwait(false);
        var targetColName = VirtualFilterNames.ColumnOf(keyColumn);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tuple in tuples)
        {
            if (tuple.TryGetValue(targetColName, out var val) && val != null)
            {
                keys.Add(val.ToString() ?? string.Empty);
            }
            else if (tuple.TryGetValue(keyColumn, out var val2) && val2 != null)
            {
                keys.Add(val2.ToString() ?? string.Empty);
            }
        }

        return keys;
    }

    public async ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GetAllowedKeyTuplesAsync(
        VirtualFilter filter,
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyList<string> keyColumns,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(keyColumns);

        var userSid = user.GetUserSid()?.Value ?? "anonymous";
        var cacheKey = $"vf_tuples:{tenantId.Value}:{filter.Name}:{userSid}";

        if (_cache != null && _cache.TryGetValue(cacheKey, out IReadOnlyList<IReadOnlyDictionary<string, object?>>? cached) && cached != null)
        {
            return cached;
        }

        if (_inMemoryTuples.TryGetValue(filter.Name, out var registered))
        {
            if (_cache != null)
            {
                _cache.Set(cacheKey, registered, TimeSpan.FromSeconds(30));
            }
            return registered;
        }

        try
        {
            var tuples = await LoadTuplesFromSourceAsync(filter, user, tenantId, keyColumns, ct).ConfigureAwait(false);
            if (_cache != null)
            {
                _cache.Set(cacheKey, tuples, TimeSpan.FromSeconds(30));
            }
            return tuples;
        }
        catch (GatewaySecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load authorized key tuples for virtual filter {Filter} on source {Source} (fail-closed).", filter.Name, filter.Source);
            throw new GatewaySecurityException(
                $"Virtual filter source '{filter.Source}' is unreachable or failed to provide allowed keys (fail-closed): {ex.Message}",
                "VIRTUAL_FILTER_SOURCE_UNAVAILABLE",
                ex);
        }
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> LoadTuplesFromSourceAsync(
        VirtualFilter filter,
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyList<string> keyColumns,
        CancellationToken ct)
    {
        if (filter.Structured == null)
        {
            throw new GatewaySecurityException($"Virtual filter '{filter.Name}' has no structured definition for key extraction.", "INVALID_FILTER_DEFINITION");
        }

        var fromTable = filter.Structured.From;
        if (_connectorRegistry == null || _tableRepository == null)
        {
            throw new GatewaySecurityException($"Data connectors or metadata repository not configured for virtual filter '{filter.Name}'.", "CONNECTOR_UNAVAILABLE");
        }

        var metadata = await _tableRepository.GetTableMetadataAsync(fromTable, ct).ConfigureAwait(false);
        if (metadata == null)
        {
            throw new GatewaySecurityException($"Table metadata for virtual filter source table '{fromTable}' was not found.", "TABLE_NOT_FOUND");
        }

        if (!_connectorRegistry.TryGetConnectorForTable(fromTable, out var connector) || connector == null)
        {
            throw new GatewaySecurityException($"Connector for virtual filter source table '{fromTable}' was not found.", "CONNECTOR_UNAVAILABLE");
        }

        var targetColNames = keyColumns.Select(VirtualFilterNames.ColumnOf).ToList();
        var decision = TableAccessDecision.Allowed(fromTable, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var session = new ConnectorSessionContext(
            Principal: user,
            Tenant: tenantId,
            AccessDecision: decision,
            ProjectedColumns: targetColNames,
            Arguments: new Dictionary<string, object?> { ["limit"] = 50_000 },
            Limit: 50_000,
            Offset: 0);

        var rowPolicy = new GovernedRowPolicy(
            MaskingProvider: new ColumnMaskingProvider(),
            HmacKeyId: null,
            MaskingDisabled: true,
            MaxRows: 50_000,
            MaxBytes: 32 * 1024 * 1024);

        var readResult = await GovernedConnectorReader.ReadAsync(connector, session, metadata, rowPolicy, ct).ConfigureAwait(false);
        var rawRows = readResult.Rows;

        // Apply where conditions from the structured filter definition in-memory if needed
        var resultTuples = new List<IReadOnlyDictionary<string, object?>>(rawRows.Count);

        foreach (var row in rawRows)
        {
            if (!MatchesFilterWhereConditions(filter.Structured.Where, row))
            {
                continue;
            }

            var tuple = new Dictionary<string, object?>(targetColNames.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var col in targetColNames)
            {
                if (row.TryGetValue(col, out var val))
                {
                    tuple[col] = val;
                }
            }
            resultTuples.Add(tuple);
        }

        return resultTuples;
    }

    private static bool MatchesFilterWhereConditions(IReadOnlyList<FilterCondition>? conditions, IReadOnlyDictionary<string, object?> row)
    {
        if (conditions == null || conditions.Count == 0)
        {
            return true;
        }

        foreach (var cond in conditions)
        {
            var colName = VirtualFilterNames.ColumnOf(cond.Column);
            row.TryGetValue(colName, out var cellValue);

            switch (cond.Operator)
            {
                case FilterConditionOperator.Eq:
                    if (!string.Equals(cellValue?.ToString(), cond.Value, StringComparison.OrdinalIgnoreCase))
                        return false;
                    break;
                case FilterConditionOperator.NotEq:
                    if (string.Equals(cellValue?.ToString(), cond.Value, StringComparison.OrdinalIgnoreCase))
                        return false;
                    break;
                case FilterConditionOperator.IsNull:
                    if (cellValue != null && cellValue is not DBNull)
                        return false;
                    break;
                case FilterConditionOperator.IsNotNull:
                    if (cellValue == null || cellValue is DBNull)
                        return false;
                    break;
            }
        }

        return true;
    }
}
