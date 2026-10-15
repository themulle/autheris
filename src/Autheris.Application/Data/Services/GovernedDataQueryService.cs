namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Autheris.Domain.Security;

/// <summary>
/// Universal Governed REST Data API service.
/// Enforces ReBAC, ABAC, Mandatory Row Filters, Column Masking and Oracle Inference Guardrails.
/// Routes execution between GovernedSqlExecutionService and FederatedDuckDbExecutionService.
/// </summary>
public sealed class GovernedDataQueryService : IGovernedDataQueryService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 1000;

    /// <summary>
    /// Absolute upper bound of the pagination window (limit + 1 for single-source tables, offset + limit + 1 on the
    /// cross-source path) a single request may span. The effective cap is
    /// lowered further by WebSql.MaxAllowedRows and, on the cross-source path, DuckDbOlap.MaxResultRows
    /// (see <see cref="ResolvePaginationWindowCap"/>).
    /// </summary>
    private const int MaxPaginationWindow = 10_000;

    private const int DefaultDuckDbMaxResultRows = 50_000;

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint _pdp;
    private readonly IGovernedSqlExecutionService _sqlExecutionService;
    private readonly IFederatedQueryExecutionService _federatedExecutionService;
    private readonly IColumnMaskingProvider _maskingProvider;
    private readonly GatewayOptions _options;
    private readonly int _maxOffset;
    private readonly IGovernedTableReadPlanner _planner;

    public GovernedDataQueryService(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        IGovernedSqlExecutionService sqlExecutionService,
        IFederatedQueryExecutionService federatedExecutionService,
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions>? options = null,
        IOptions<AnalyticsOptions>? analyticsOptions = null,
        IGovernedTableReadPlanner? planner = null)
    {
        _planner = planner ?? new GovernedTableReadPlanner();
        _options = options?.Value ?? new GatewayOptions();
        _maxOffset = analyticsOptions?.Value?.DatasetApi?.MaxOffset ?? DatasetApiOptions.DefaultMaxOffset;
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp ?? throw new ArgumentNullException(nameof(pdp));
        _sqlExecutionService = sqlExecutionService ?? throw new ArgumentNullException(nameof(sqlExecutionService));
        _federatedExecutionService = federatedExecutionService ?? throw new ArgumentNullException(nameof(federatedExecutionService));
        _maskingProvider = maskingProvider ?? throw new ArgumentNullException(nameof(maskingProvider));
    }

    public async Task<DatasetQueryEnvelope> ExecuteQueryAsync(
        DatasetQueryRequest request,
        RequestContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // 1. Resolve table metadata from repository or virtual system tables
        var metadata = await _metadataRepository.GetTableMetadataAsync(request.Table, ct).ConfigureAwait(false)
            ?? VirtualSystemTables.Get(request.Table);

        if (metadata == null)
        {
            throw new TableNotFoundException(request.Table);
        }

        if (!metadata.Table.IsActive)
        {
            throw new UnauthorizedAccessException($"Table '{request.Table.ToQualifiedName()}' is not active.");
        }

        // 2. Authorization stage (WP-ARC-B-03): unified PDP (ReBAC, ABAC, Consent, Virtual Filters) plus the non-canonical
        // ClusterAdmin fail-closed check. The row policy is not spliced from this decision (WP-X0n / SEC-X1-17): the rewriter's own
        // decision is the only one that restricts rows, so both must be computed from the same inputs.
        var decision = await GovernedQueryStages.AuthorizeTableAsync(
            _pdp,
            request.Table,
            metadata,
            context.User,
            context.TenantId,
            context.ClientIp,
            request.SelectColumns,
            ct).ConfigureAwait(false);

        // 5. AST & SQL Formulation
        var limit = request.Limit > 0 ? Math.Min(request.Limit, MaxLimit) : DefaultLimit;
        var offset = request.Offset >= 0 ? request.Offset : 0;

        // Single-source SQL tables push the page down as Trino-form pagination (OFFSET n ROWS LIMIT m): the governed compile
        // path parses that grammar and every dialect generator emits its native form, so the gateway reads limit + 1 rows
        // (the extra row populates HasMore). Cross-source tables (staged DuckDB / HTTP sources) cannot take a pushed-down
        // offset and keep the window logic: LIMIT offset + limit + 1, the first <offset> rows are skipped below.
        bool isCrossSource = metadata.DataSourceType is DataSourceType.LakehouseIceberg
            or DataSourceType.LakehouseDelta
            or DataSourceType.HttpDeclarative
            or DataSourceType.HttpPlugin;

        var windowCap = ResolvePaginationWindowCap(isCrossSource);
        var window = isCrossSource ? (long)offset + limit + 1 : limit + 1L;
        if (isCrossSource)
        {
            if (window > windowCap)
            {
                throw new ArgumentException(
                    $"Pagination window too large: offset + limit must not exceed {windowCap - 1}.", nameof(request));
            }
        }
        else
        {
            if (offset > _maxOffset)
            {
                throw new ArgumentException($"Offset too large: offset must not exceed {_maxOffset}.", nameof(request));
            }

            if (window > windowCap)
            {
                throw new ArgumentException($"Limit too large: limit must not exceed {windowCap - 1}.", nameof(request));
            }
        }

        // WP-ARC-B-01: column authorization, the oracle-inference guard and the SQL formulation are the shared planner's job.
        // No policy text is spliced: the decision above authorizes the request; the row policy is applied exactly once, by the
        // rewriter on the single-source branch and by the staging service from its own decision on the cross-source branch.
        TableFilterClause? filter = null;
        if (!string.IsNullOrWhiteSpace(request.FilterExpression))
        {
            var translated = DataApiFilterTranslator.TranslateFilterToTrino(request.FilterExpression, metadata);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                filter = TableFilterClause.FromTrinoText(
                    translated,
                    DataApiFilterTranslator.ExtractReferencedColumns(request.FilterExpression, metadata));
            }
        }

        var orderBy = string.IsNullOrWhiteSpace(request.OrderBy) ? null : DataApiFilterTranslator.ParseOrderBy(request.OrderBy);

        // Cross-source: LIMIT offset + limit + 1 (the skipped window plus one row to populate HasMore); single-source: the page is
        // pushed down as OFFSET n ROWS LIMIT limit + 1.
        var planned = _planner.Plan(
            new TableReadIntent(
                request.Table,
                request.SelectColumns,
                filter,
                orderBy,
                Limit: checked((int)window),
                Offset: isCrossSource ? 0 : offset,
                Transport: request.Transport),
            metadata,
            decision);
        var columnsToSelect = planned.ColumnsToSelect;

        // 6. Execution & Routing
        var queryRequest = new GovernedSqlQueryRequest(planned.Sql, planned.Parameters.Count > 0 ? planned.Parameters : null, Transport: request.Transport);
        GovernedSqlResult sqlResult;

        if (isCrossSource)
        {
            sqlResult = await _federatedExecutionService.ExecuteQueryBufferedAsync(
                queryRequest,
                context.User,
                context.TenantId,
                ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(
                    queryRequest,
                    context.User,
                    context.TenantId,
                    ct).ConfigureAwait(false);
            }
            catch (CrossSourceRoutingException)
            {
                sqlResult = await _federatedExecutionService.ExecuteQueryBufferedAsync(
                    queryRequest,
                    context.User,
                    context.TenantId,
                    ct).ConfigureAwait(false);
            }
        }

        // 7. Paging Envelope & Masking Enforcement
        // Fail closed: a row cap below the requested window (e.g. a lower effective MaxAllowedRows on a path the
        // pre-check could not predict) must never surface as a short page that claims hasMore=false.
        var sourceRows = sqlResult.Rows;
        if (sqlResult.Truncated && sourceRows.Count < window)
        {
            throw new ArgumentException(
                "Pagination window exceeds the row limit of this data source; reduce offset or limit.", nameof(request));
        }

        // Rows already positioned by the database (pushdown) need no in-memory skip.
        int skip = isCrossSource ? offset : 0;
        int available = Math.Max(0, sourceRows.Count - skip);
        bool hasMore = available > limit;
        int pageCount = Math.Min(limit, available);
        var pagedRows = new List<IReadOnlyDictionary<string, object?>>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            pagedRows.Add(sourceRows[skip + i]);
        }

        var columnInfos = new List<DatasetColumnInfo>(columnsToSelect.Count);
        foreach (var col in columnsToSelect)
        {
            var isMasked = decision.GetEffectiveColumnAccess(col, metadata) == ColumnAccessLevel.Mask;
            var colType = metadata.GetColumn(col)?.DataType ?? "varchar";
            columnInfos.Add(new DatasetColumnInfo(col, colType, isMasked));
        }

        var processedRows = new List<IReadOnlyDictionary<string, object?>>(pagedRows.Count);
        foreach (var row in pagedRows)
        {
            var rowDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var colInfo in columnInfos)
            {
                if (row.TryGetValue(colInfo.Name, out var val))
                {
                    if (colInfo.Masked && !sqlResult.InDbColumnMaskingExecuted && !_options.IsColumnMaskingDisabled)
                    {
                        var rule = metadata.ColumnMaskingRules.TryGetValue(colInfo.Name, out var r) ? r : new MaskingRule { RuleType = "REDACT" };
                        var scopedRule = Autheris.Application.Services.GatewayExecutionService.ScopeRuleForTenant(
                            rule,
                            context.TenantId.Value,
                            _options.DataMasking?.HmacKeyId);
                        rowDict[colInfo.Name] = _maskingProvider.MaskValue(colInfo.Name, val, scopedRule);
                    }
                    else
                    {
                        rowDict[colInfo.Name] = val;
                    }
                }
                else
                {
                    rowDict[colInfo.Name] = null;
                }
            }
            processedRows.Add(rowDict);
        }

        return new DatasetQueryEnvelope(
            Dataset: request.Table.ToString(),
            Count: processedRows.Count,
            Offset: offset,
            Limit: limit,
            HasMore: hasMore,
            Columns: columnInfos,
            Data: processedRows);
    }

    /// <summary>
    /// Largest pagination window the request may span: min(10 000, effective WebSql.MaxAllowedRows when &gt; 0,
    /// DuckDbOlap.MaxResultRows on the cross-source path). The SQL layer enforces the same row caps, so a larger
    /// window could never be served completely.
    /// </summary>
    private long ResolvePaginationWindowCap(bool isCrossSource)
    {
        long cap = MaxPaginationWindow;

        var maxAllowed = _options.WebSql?.MaxAllowedRows ?? 0;
        if (maxAllowed > 0)
        {
            cap = Math.Min(cap, maxAllowed);
        }

        if (isCrossSource)
        {
            var duckRows = _options.DuckDbOlap?.MaxResultRows > 0 ? _options.DuckDbOlap.MaxResultRows : DefaultDuckDbMaxResultRows;
            cap = Math.Min(cap, duckRows);
        }

        return cap;
    }
}
