namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// AR-18: Coordinates chunked batch queries for child relations (e.g. invoice items) with data source execution and metric tracking.
/// </summary>
public sealed class BatchRelationLoader
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IEnumerable<IDataSourceExecutor>? _dataSourceExecutors;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly IChunkedQueryExecutor _chunkedQueryExecutor;
    private readonly ITrafficDrainController? _drainController;
    private readonly GatewayOptions? _options;
    private readonly Func<ClaimsPrincipal?, TableIdentifier, CancellationToken, Task<TableAccessDecision>> _checkTableAccess;

    public int LastDispatchedChildQueryCount { get; private set; }

    public BatchRelationLoader(
        ITableMetadataRepository metadataRepository,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors,
        IColumnMaskingProvider maskingProvider,
        IChunkedQueryExecutor chunkedQueryExecutor,
        ITrafficDrainController? drainController,
        GatewayOptions? options,
        Func<ClaimsPrincipal?, TableIdentifier, CancellationToken, Task<TableAccessDecision>> checkTableAccess)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _dataSourceExecutors = dataSourceExecutors;
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
        _chunkedQueryExecutor = chunkedQueryExecutor ?? throw new ArgumentNullException(nameof(chunkedQueryExecutor));
        _drainController = drainController;
        _options = options;
        _checkTableAccess = checkTableAccess ?? throw new ArgumentNullException(nameof(checkTableAccess));
    }

    public async Task<IReadOnlyDictionary<string, List<InvoiceItemRecord>>> LoadInvoiceItemsBatchAsync(
        ClaimsPrincipal? principal,
        IReadOnlyList<string> invoiceIds,
        CancellationToken ct = default)
    {
        using var _ = _drainController?.TrackQuery();
        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var decision = await _checkTableAccess(principal, childTableId, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            LastDispatchedChildQueryCount = 0;
            return new Dictionary<string, List<InvoiceItemRecord>>();
        }

        if (invoiceIds == null || invoiceIds.Count == 0)
        {
            LastDispatchedChildQueryCount = 0;
            return new Dictionary<string, List<InvoiceItemRecord>>();
        }

        var metadata = await _metadataRepository.GetTableMetadataAsync(childTableId, ct).ConfigureAwait(false);
        if (metadata == null || !metadata.Table.IsActive)
        {
            LastDispatchedChildQueryCount = 0;
            return new Dictionary<string, List<InvoiceItemRecord>>();
        }

        var executor = _dataSourceExecutors?.FirstOrDefault(e => e.SupportedType == metadata.Table.DataSourceType);
        if (executor == null)
        {
            LastDispatchedChildQueryCount = 0;
            return invoiceIds.Distinct().ToDictionary(id => id, _ => new List<InvoiceItemRecord>());
        }

        var tenantId = principal.GetTenantId();
        var joinColumn = metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "parent_id", StringComparison.OrdinalIgnoreCase))?.ColumnName
            ?? metadata.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "invoice_id", StringComparison.OrdinalIgnoreCase))?.ColumnName
            ?? "parent_id";

        var (result, dispatchedQueries) = await _chunkedQueryExecutor.ExecuteGroupedWithMetricsAsync<string, InvoiceItemRecord>(
            invoiceIds,
            async (chunkKeys, chunkCt) =>
            {
                var chunkResult = new Dictionary<string, List<InvoiceItemRecord>>(chunkKeys.Count);
                foreach (var k in chunkKeys)
                {
                    chunkResult[k] = new List<InvoiceItemRecord>();
                }

                if (chunkKeys.Count == 0)
                {
                    return chunkResult;
                }

                var filterParams = new Dictionary<string, object?>(chunkKeys.Count);
                var paramNames = new List<string>(chunkKeys.Count);
                for (int i = 0; i < chunkKeys.Count; i++)
                {
                    var pName = $"@p_inv_{i}";
                    paramNames.Add(pName);
                    filterParams[pName] = chunkKeys[i];
                }

                var filterClause = new TableFilterClause(
                    SqlPredicate: $"{joinColumn} IN ({string.Join(", ", paramNames)})",
                    Parameters: filterParams,
                    ReferencedColumns: new[] { joinColumn })
                {
                    DialectSqlFactory = d => $"{d.QuoteIdentifier(joinColumn)} IN ({string.Join(", ", paramNames)})"
                };

                var pageItems = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [TableQueryItems.Filter] = filterClause
                };

                var requestedColumns = metadata.Columns
                    .Where(c => decision.GetColumnAccess(c.ColumnName) != ColumnAccessLevel.Deny)
                    .Select(c => c.ColumnName)
                    .ToList();

                var execContext = new DataSourceExecutionContext(
                    SourceName: metadata.Table.SourceName,
                    Metadata: metadata,
                    Principal: principal ?? new ClaimsPrincipal(new ClaimsIdentity()),
                    AccessDecision: decision,
                    Arguments: new Dictionary<string, object?> { ["limit"] = Math.Max(100, chunkKeys.Count * 10), ["offset"] = 0 },
                    RequestedFields: requestedColumns,
                    RequestHeaders: null,
                    Limit: Math.Max(100, chunkKeys.Count * 10),
                    Offset: 0,
                    Tenant: tenantId,
                    Items: pageItems
                );

                IReadOnlyList<IReadOnlyDictionary<string, object?>> rawRows;
                try
                {
                    rawRows = await executor.ExecuteAsync(execContext, chunkCt).ConfigureAwait(false);
                }
                catch (GatewayNotImplementedException)
                {
                    return chunkResult;
                }

                var rlsPushdownAlreadyOccurred = execContext.Items.TryGetValue("RlsPushdownExecuted", out var p2) && p2 is true;
                var inDbMaskingAlreadyOccurred = execContext.Items.TryGetValue("InDbColumnMaskingExecuted", out var m2) && m2 is true;

                var maxBytes = _options?.GraphQL?.MaxResponseBytes > 0 ? _options.GraphQL.MaxResponseBytes : 10 * 1024 * 1024;
                var processedRows = GovernedConnectorReader.Apply(
                    rawRows,
                    metadata,
                    decision,
                    tenantId.Value,
                    rlsPushdownAlreadyOccurred,
                    inDbMaskingAlreadyOccurred,
                    new GovernedRowPolicy(
                        _maskingProvider,
                        _options?.DataMasking?.HmacKeyId,
                        MaskingDisabled: _options?.IsColumnMaskingDisabled == true,
                        MaxBytes: maxBytes));

                for (int i = 0; i < processedRows.Count; i++)
                {
                    var row = processedRows[i];
                    var raw = i < rawRows.Count ? rawRows[i] : null;

                    var parentKey = row.TryGetValue(joinColumn, out var pkVal) ? pkVal?.ToString() ?? "" : "";
                    if (string.IsNullOrEmpty(parentKey) && raw != null)
                    {
                        parentKey = raw.TryGetValue(joinColumn, out var rPk) ? rPk?.ToString() ?? "" : "";
                    }
                    if (string.IsNullOrEmpty(parentKey) && string.Equals(joinColumn, "parent_id", StringComparison.OrdinalIgnoreCase) && raw != null)
                    {
                        parentKey = raw.TryGetValue("invoice_id", out var altVal) ? altVal?.ToString() ?? "" : "";
                    }

                    if (string.IsNullOrEmpty(parentKey) || !chunkResult.TryGetValue(parentKey, out var list))
                    {
                        continue;
                    }

                    var id = row.TryGetValue("id", out var idVal) ? idVal?.ToString() ?? "" : "";
                    if (string.IsNullOrEmpty(id) && raw != null)
                    {
                        id = raw.TryGetValue("id", out var rawId) ? rawId?.ToString() ?? "" : "";
                    }

                    var prodName = row.TryGetValue("product_name", out var prodVal) ? prodVal?.ToString() : null;
                    var price = 0m;
                    if (row.TryGetValue("price", out var priceVal) && priceVal != null)
                    {
                        if (priceVal is decimal d) price = d;
                        else if (decimal.TryParse(priceVal.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedPrice)) price = parsedPrice;
                    }
                    var sensitiveNote = row.TryGetValue("sensitive_note", out var noteVal) ? noteVal?.ToString() : null;

                    list.Add(new InvoiceItemRecord
                    {
                        Id = id,
                        InvoiceId = parentKey,
                        ProductName = prodName,
                        Price = price,
                        SensitiveNote = sensitiveNote
                    });
                }

                return (IReadOnlyDictionary<string, List<InvoiceItemRecord>>)chunkResult;
            },
            chunkSize: _options?.GraphQL?.MaxInClauseBatchSize,
            ct: ct);

        LastDispatchedChildQueryCount = dispatchedQueries;
        return result;
    }
}
