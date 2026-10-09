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

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint _pdp;
    private readonly IGovernedSqlExecutionService _sqlExecutionService;
    private readonly IFederatedQueryExecutionService _federatedExecutionService;
    private readonly IColumnMaskingProvider _maskingProvider;

    public GovernedDataQueryService(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint pdp,
        IGovernedSqlExecutionService sqlExecutionService,
        IFederatedQueryExecutionService federatedExecutionService,
        IColumnMaskingProvider maskingProvider)
    {
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

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(", ", columnsToSelect)).Append(" FROM ").Append(request.Table.ToQualifiedName());

        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.FilterExpression))
        {
            var translated = DataApiFilterTranslator.TranslateFilterToSql(request.FilterExpression);
            if (!string.IsNullOrWhiteSpace(translated))
            {
                predicates.Add(translated);
            }
        }

        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            predicates.Add($"({decision.CombinedRowFilterSql})");
        }

        if (predicates.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", predicates));
        }

        if (!string.IsNullOrWhiteSpace(request.OrderBy))
        {
            sb.Append(" ORDER BY ").Append(request.OrderBy);
        }
        else if (metadata.PrimaryKeyColumns.Count > 0)
        {
            sb.Append(" ORDER BY ").Append(string.Join(", ", metadata.PrimaryKeyColumns));
        }

        // Fetch Limit + 1 to accurately populate HasMore
        sb.Append(" LIMIT ").Append(limit + 1).Append(" OFFSET ").Append(offset);
        var sql = sb.ToString();

        // 6. Execution & Routing
        var queryRequest = new GovernedSqlQueryRequest(sql);
        GovernedSqlResult sqlResult;
        bool isCrossSource = metadata.DataSourceType is DataSourceType.LakehouseIceberg
            or DataSourceType.LakehouseDelta
            or DataSourceType.HttpDeclarative
            or DataSourceType.HttpPlugin;

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
        var rawRows = sqlResult.Rows;
        bool hasMore = rawRows.Count > limit;
        var pagedRows = rawRows.Take(limit).ToList();

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
}
