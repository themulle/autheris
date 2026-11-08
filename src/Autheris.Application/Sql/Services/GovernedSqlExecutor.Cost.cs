namespace Autheris.Application.Sql.Services;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Sql.Cost;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class GovernedSqlExecutor
{
    private readonly IQueryCostQuota? _costQuota;

    private int CalculateEstimatedCost(
        GovernedSqlQueryRequest request,
        SqlQueryMetadata? metadata)
    {
        if (metadata == null) return 1;

        var rowLimit = request.RowLimit ?? SqlRowLimit.For(_options.Value.WebSql ?? new WebSqlOptions());
        var costOpts = _options.Value.QueryCost ?? new QueryCostOptions();
        var analysisOpts = new QueryAnalysisOptions();
        var webSqlCost = _options.Value.WebSql?.Cost ?? new WebSqlCostOptions();

        return SqlStatementCostEstimator.EstimateCost(
            metadata,
            rowLimit,
            costOpts,
            analysisOpts,
            webSqlCost,
            pageOffset: request.PageOffset ?? 0,
            isPaged: request.IsPaged,
            maxAffectedRows: _options.Value.WebSql?.MaxAffectedRows ?? 1000);
    }

    private async Task<QueryCostAdmission?> AdmitCostAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        SqlQueryMetadata? metadata,
        CancellationToken ct)
    {
        bool isMeteredTransport = string.Equals(request.Transport, "WebSql", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(request.Transport, "Trino", StringComparison.OrdinalIgnoreCase);

        if (!isMeteredTransport || request.PlannedTableRead)
        {
            return null;
        }

        if (request.Admission != null)
        {
            return request.Admission;
        }

        if (_costQuota == null || !(_options.Value.WebSql?.Cost?.Enabled ?? true))
        {
            return null;
        }

        int cEst = CalculateEstimatedCost(request, metadata);
        int maxCost = _options.Value.WebSql?.Cost?.MaxStatementCost ?? 2000;

        QueryCostAdmission admission;
        try
        {
            admission = await _costQuota.AdmitAsync(
                new QueryCostRequest(
                    Principal: user,
                    ApiKey: null,
                    ClientIp: null,
                    EstimatedCost: cEst,
                    TransportMaxCost: maxCost,
                    CapByTierMaxCostPerQuery: false),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SqlQuotaUnavailableException();
        }

        if (admission.Outcome == QueryCostOutcome.OverCost)
        {
            throw new SqlCostExceededException(cEst, admission.MaxAllowedCost);
        }
        if (admission.Outcome == QueryCostOutcome.QuotaExhausted)
        {
            throw new SqlQuotaExhaustedException(admission.RetryAfterSeconds);
        }

        return admission;
    }

    private async Task SettleCostAsync(
        QueryCostAdmission? admission,
        long rowsDeliveredOrAffected,
        double elapsedSeconds,
        bool isFailureOrAbort)
    {
        if (admission == null || _costQuota == null)
        {
            return;
        }

        try
        {
            var costOpts = _options.Value.QueryCost ?? new QueryCostOptions();
            var webSqlCost = _options.Value.WebSql?.Cost ?? new WebSqlCostOptions();

            int cAct = SqlStatementCostEstimator.CalculateSettlementCost(
                rowsDeliveredOrAffected,
                elapsedSeconds,
                costOpts,
                webSqlCost,
                isFailureOrAbort);

            await _costQuota.SettleAsync(admission, cAct, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception settleEx)
        {
            _logger?.LogWarning(settleEx, "Failed to settle WebSQL cost quota.");
        }
    }

    private async Task ChargeFailureFeeOnDenialAsync(ClaimsPrincipal user)
    {
        if (_costQuota == null)
        {
            return;
        }

        try
        {
            await _costQuota.ChargeFailureFeeAsync(user, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception feeEx)
        {
            _logger?.LogWarning(feeEx, "Failed to charge failure fee on policy denial.");
        }
    }
}
