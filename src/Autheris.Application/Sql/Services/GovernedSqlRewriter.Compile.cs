namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security;
using System.Threading;
using Autheris.Application.Common;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

public sealed partial class GovernedSqlRewriter
{
    private static readonly ConcurrentDictionary<TargetSqlDialect, byte> ClampWarned = new();

    private IReadOnlyList<WebSqlIndexHint>? EstimateQueryIndexHints(
        SqlQueryMetadata metadata,
        SqlRowLimit? rowLimit,
        WebSqlOptions webSqlOptions,
        IReadOnlyDictionary<TableIdentity, TableMetadata> decidedTableMetadata,
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions)
    {
        var costOpts = _options.Value.QueryCost ?? new QueryCostOptions();
        var analysisOpts = new Autheris.Application.Governance.Analysis.QueryAnalysisOptions();
        var webSqlCost = _options.Value.WebSql?.Cost ?? new WebSqlCostOptions();
        var effectiveRowLimit = rowLimit ?? SqlRowLimit.For(webSqlOptions);

        TableMetadata? GetTableMeta(string name) =>
            decidedTableMetadata.Values.FirstOrDefault(m =>
                string.Equals(m.Table.TableName, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Identifier.ToString(), name, StringComparison.OrdinalIgnoreCase));

        IReadOnlySet<string>? GetDenied(string name) =>
            decisions.FirstOrDefault(kv => string.Equals(kv.Key.Table, name, StringComparison.OrdinalIgnoreCase)).Value?.ColumnAccess
                .Where(cl => cl.Value == ColumnAccessLevel.Deny)
                .Select(cl => cl.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var (_, hints) = Autheris.Application.Sql.Cost.SqlStatementCostEstimator.EstimateCostWithHints(
            metadata,
            effectiveRowLimit,
            costOpts,
            analysisOpts,
            webSqlCost,
            getTableMetadata: GetTableMeta,
            getDeniedColumns: GetDenied);

        return hints.Count > 0 ? hints : null;
    }

    /// <summary>
    /// Stage 2 of the list limits: the dialect is known now, so each list is checked against
    /// <c>min(configured, MaxBindParameters - 100, MaxInListItems)</c>. A configured value above that is clamped at runtime and logged once
    /// per dialect, with numbers only. Nothing is truncated: a larger list is refused.
    /// </summary>
    private void EnforceEffectiveListMaximum(ClientListShapes? clientLists, TargetSqlDialect dialect, WebSqlOptions webSqlOptions)
    {
        if (clientLists is null)
        {
            return;
        }

        int largest = clientLists.MaxCount();
        if (largest == 0)
        {
            return;
        }

        var limits = Autheris.Application.Sql.ListParameters.ListParameterLimits.From(webSqlOptions);
        int effective = Autheris.Application.Sql.ListParameters.ListParameterLimits.EffectiveMax(
            limits.ConfiguredMaxValues, DialectCapabilityTable.Default.Get(dialect));
        if (effective < limits.ConfiguredMaxValues && ClampWarned.TryAdd(dialect, 0))
        {
            _logger?.LogWarning("WebSQL list parameter maximum {Configured} is clamped to {Effective} for dialect {Dialect}.", limits.ConfiguredMaxValues, effective, dialect);
        }

        if (largest > effective)
        {
            throw new ClientListException(GovernedSqlErrorCodes.ListParamTooLarge);
        }
    }

    private CompileRequest CreateCompileRequest(
        GovernancePolicy policy,
        Dictionary<TableIdentity, TableAccessTarget> decidedTargets,
        TargetSqlDialect targetSqlDialect,
        WebSqlOptions webSqlOptions,
        bool isDml,
        long enforcedMaxRows,
        IReadOnlySet<string> allowedFunctions,
        ClientListShapes? clientLists,
        IReadOnlySet<string>? gatewayLikePatternParameters,
        bool bindPageOffset = false,
        long? pageOffset = null,
        long maxOffset = 1_000_000)
    {
        // A table that stage 3 admitted but the assembly dropped (no typed policy, catalog load failure, missing column)
        // is denied here with the same exception as every other "table not usable" cause, before the compiler could answer differently.
        foreach (var (identity, deniedTarget) in decidedTargets)
        {
            if (policy.Catalog.Resolve(identity.ToQualifiedName()) is null)
            {
                _logger?.LogWarning("WebSQL rejected table {Table}: it is not part of the verified governance catalog.", deniedTarget.FullName);
                throw SqlDataSourceResolver.TableDenied(deniedTarget);
            }
        }

        return CompileRequestFactory.Create(policy, targetSqlDialect, webSqlOptions, isDml, enforcedMaxRows, allowedFunctions, clientLists,
            timeZoneConversionAvailable: _timeZoneState?.IsAvailable(targetSqlDialect) ?? false,
            translateDateFunctions: true, gatewayLikePatternParameters: gatewayLikePatternParameters,
            bindPageOffset: bindPageOffset, pageOffset: pageOffset, maxOffset: maxOffset);
    }

    private CompiledSql CompileStatement(string rawSql, CompileRequest request, bool mayRetry, CancellationToken ct)
    {
        try
        {
            return _sqlEngine.Compile(rawSql.AsMemory(), request, ct);
        }
        catch (Exception ex) when (ex is SecurityException or TrinoSqlEngine.Ast.Builder.AstBuildException
                                   && !(mayRetry && ex is SqlLimitExceededException limit && IsRetryableLimit(limit.Kind)))
        {
            // A retryable limit of a statement with substituted key sets is handled by the caller without logging.
            _logger?.Warning(ex, "WebSQL statement rejected by the SQL compiler.");
            throw;
        }
    }

    private static bool IsRetryableLimit(SqlLimitKind kind) => kind is SqlLimitKind.BindParameters or SqlLimitKind.PolicyExpansionFactor;

    private static void RecordRetryMetrics(RowFilterKeySetReport report, TargetSqlDialect dialect)
    {
        foreach (var o in report.Outcomes)
        {
            if (o.Reason is KeySetFallbackReason.BindLimitRetry or KeySetFallbackReason.ExpansionLimitRetry)
            {
                GatewayDiagnostics.RowFilterKeySetFallbacks.Add(
                    1,
                    new KeyValuePair<string, object?>("dialect", dialect.ToString()),
                    new KeyValuePair<string, object?>("outcome", "Fallback"),
                    new KeyValuePair<string, object?>("reason", o.Reason.ToString()));
            }
        }
    }

    /// <summary>One Debug line per site after the compiled SQL line. Counts are allowed here; never values or hashes.</summary>
    private void LogKeySets(RowFilterKeySetReport report)
    {
        foreach (var o in report.Outcomes)
        {
            _logger?.LogDebug(
                "GovernedSqlExecutionService: Row-filter key set site {Table} outcome={Outcome} reason={Reason} keys={Keys} padded={Padded} ageSeconds={Age}",
                o.Table, o.Outcome, o.Reason, o.KeyCount, o.PaddedCount, o.AgeSeconds);
        }
    }

    private IPAddress ResolveClientIp() => _clientIpResolver?.ResolveClientIp() ?? IPAddress.None;
}
