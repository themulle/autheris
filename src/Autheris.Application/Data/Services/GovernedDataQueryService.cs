namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
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
    /// Absolute upper bound of the pagination window (offset + limit + 1) a single request may span. The effective cap is
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

    public GovernedDataQueryService(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        IGovernedSqlExecutionService sqlExecutionService,
        IFederatedQueryExecutionService federatedExecutionService,
        IColumnMaskingProvider maskingProvider,
        IOptions<GatewayOptions>? options = null)
    {
        _options = options?.Value ?? new GatewayOptions();
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

        // 2. Evaluate unified PDP (ReBAC, ABAC, Consent, Virtual Filters)
        var clientIp = !string.IsNullOrWhiteSpace(context.ClientIp) && IPAddress.TryParse(context.ClientIp, out var ip) ? ip : null;
        var secContext = SecurityPrincipalContext.FromPrincipal(context.User, context.TenantId, clientIp: clientIp);

        var decision = await _pdp.EvaluateAccessAsync(
            request.Table,
            metadata,
            secContext,
            request.SelectColumns,
            ct).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by governance policy.";
            throw new UnauthorizedAccessException($"Access to table '{request.Table.ToQualifiedName()}' is forbidden: {reasons}");
        }

        // WP-X0n / SEC-X1-17: the row policy is no longer spliced from this decision, so the rewriter's own decision is the only
        // one that restricts rows. It must be computed from the same inputs. The unified PDP drops a ClusterAdmin role claim of a
        // non-canonical principal while the rewriter keeps every role claim; that one input difference fails closed here.
        if (context.User.GetUserRoles().Any(ClusterAdminPolicy.IsClusterAdminRole) && !ClusterAdminPolicy.IsCanonicalClusterAdmin(context.User))
        {
            throw new UnauthorizedAccessException(
                $"Access to table '{request.Table.ToQualifiedName()}' is forbidden: the cluster administrator role claim is not canonical.");
        }

        // 3. Resolve columns to select and enforce column permissions
        IReadOnlyList<string> columnsToSelect;
        if (request.SelectColumns != null && request.SelectColumns.Count > 0)
        {
            foreach (var col in request.SelectColumns)
            {
                var colMeta = metadata.GetColumn(col);
                if (colMeta == null)
                {
                    throw new ArgumentException($"Column '{col}' does not exist on table '{request.Table.ToQualifiedName()}'.", nameof(request));
                }

                var access = decision.GetEffectiveColumnAccess(col, metadata);
                if (access == ColumnAccessLevel.Deny)
                {
                    throw new ArgumentException($"Access to column '{col}' is denied on table '{request.Table.ToQualifiedName()}'.", nameof(request));
                }
            }
            columnsToSelect = request.SelectColumns;
        }
        else
        {
            columnsToSelect = metadata.Columns
                .Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) != ColumnAccessLevel.Deny)
                .Select(c => c.ColumnName)
                .ToList();

            if (columnsToSelect.Count == 0)
            {
                throw new UnauthorizedAccessException($"No accessible columns available on table '{request.Table.ToQualifiedName()}'.");
            }
        }

        // 4. Guard against Oracle Inference Attacks on Filter and OrderBy
        if (!string.IsNullOrWhiteSpace(request.FilterExpression))
        {
            var filterCols = DataApiFilterTranslator.ExtractReferencedColumns(request.FilterExpression, metadata);
            foreach (var col in filterCols)
            {
                var access = decision.GetEffectiveColumnAccess(col, metadata);
                if (access != ColumnAccessLevel.Clear)
                {
                    throw new ArgumentException(
                        $"Filtering on masked or denied column '{col}' is rejected to prevent oracle inference attacks.",
                        nameof(request));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(request.OrderBy))
        {
            var orderCols = DataApiFilterTranslator.ExtractOrderByColumns(request.OrderBy, metadata);
            foreach (var col in orderCols)
            {
                var access = decision.GetEffectiveColumnAccess(col, metadata);
                if (access != ColumnAccessLevel.Clear)
                {
                    throw new ArgumentException(
                        $"Ordering on masked or denied column '{col}' is rejected to prevent oracle inference attacks.",
                        nameof(request));
                }
            }
        }

        // 5. AST & SQL Formulation
        var limit = request.Limit > 0 ? Math.Min(request.Limit, MaxLimit) : DefaultLimit;
        var offset = request.Offset >= 0 ? request.Offset : 0;

        // Pagination is applied here, not as OFFSET text in the generated SQL: the SQL engine's grammar only accepts the
        // Trino form (OFFSET n LIMIT m) while the legacy token-stream rewriter hands the text unchanged to SQLite,
        // SQL Server, PostgreSQL and the cross-source planner, none of which share that form. A plain LIMIT is
        // understood and translated per target by every path, so the query reads offset + limit + 1 rows and the
        // first <offset> rows are skipped below (the extra row populates HasMore).
        bool isCrossSource = metadata.DataSourceType is DataSourceType.LakehouseIceberg
            or DataSourceType.LakehouseDelta
            or DataSourceType.HttpDeclarative
            or DataSourceType.HttpPlugin;

        var windowCap = ResolvePaginationWindowCap(isCrossSource);
        var window = (long)offset + limit + 1;
        if (window > windowCap)
        {
            throw new ArgumentException(
                $"Pagination window too large: offset + limit must not exceed {windowCap - 1}.", nameof(request));
        }

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(", ", columnsToSelect.Select(DataApiFilterTranslator.QuoteIdentifier))).Append(" FROM ").Append(request.Table.ToString());

        // WP-X0n (G-5): no policy text is spliced here. The decision above authorizes the request and drives column access;
        // the row policy is applied exactly once, by the rewriter on the single-source branch and by the staging service
        // from its own decision on the cross-source branch. Identifiers are emitted as quoted catalog spellings.
        if (!string.IsNullOrWhiteSpace(request.FilterExpression))
        {
            var translated = DataApiFilterTranslator.TranslateFilterToTrino(request.FilterExpression, metadata);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                sb.Append(" WHERE ").Append(translated);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.OrderBy))
        {
            sb.Append(" ORDER BY ").Append(DataApiFilterTranslator.BuildOrderByClause(request.OrderBy, metadata));
        }
        else if (metadata.PrimaryKeyColumns.Count > 0)
        {
            sb.Append(" ORDER BY ").Append(string.Join(", ", metadata.PrimaryKeyColumns.Select(DataApiFilterTranslator.QuoteIdentifier)));
        }

        // Fetch offset + limit + 1 rows: the skipped window plus one row to accurately populate HasMore
        sb.Append(" LIMIT ").Append(offset + limit + 1);
        var sql = sb.ToString();

        // 6. Execution & Routing
        var queryRequest = new GovernedSqlQueryRequest(sql, Transport: request.Transport);
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

        int available = Math.Max(0, sourceRows.Count - offset);
        bool hasMore = available > limit;
        int pageCount = Math.Min(limit, available);
        var pagedRows = new List<IReadOnlyDictionary<string, object?>>(pageCount);
        for (int i = 0; i < pageCount; i++)
        {
            pagedRows.Add(sourceRows[offset + i]);
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
                    if (colInfo.Masked)
                    {
                        var rule = metadata.ColumnMaskingRules.TryGetValue(colInfo.Name, out var r) ? r : new MaskingRule { RuleType = "REDACT" };
                        rowDict[colInfo.Name] = _maskingProvider.MaskValue(colInfo.Name, val, rule);
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
