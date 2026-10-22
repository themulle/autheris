namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Pipeline;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

/// <summary>
/// AR-05: Coordinates governed SQL rewrite and database execution.
/// Delegating pipeline stages to <see cref="GovernedSqlRewriter"/> and execution to <see cref="GovernedSqlExecutor"/>,
/// and routing cross-source/heterogeneous queries to <see cref="IFederatedQueryExecutionService"/>.
/// </summary>
public sealed class GovernedSqlExecutionService : IGovernedSqlExecutionService, ISqlRewritePipeline
{
    internal const string InternalParameterPrefix = "__gql_";
    internal const string ClientParameterPlaceholderPrefix = "__param_";

    private readonly GovernedSqlRewriter _rewriter;
    private readonly GovernedSqlExecutor _executor;
    private readonly Lazy<IFederatedQueryExecutionService>? _federatedExecution;

    public IReadOnlyList<SqlRewriteStageOrder> StageOrder => _rewriter.StageOrder;

    public GovernedSqlExecutionService(
        IOptions<GatewayOptions> options,
        IAuditWriter auditWriter,
        IProviderExecutionGuard executionGuard,
        GovernedSqlRewriteDependencies rewrite,
        GovernedSqlExecutionDependencies execution,
        Lazy<IFederatedQueryExecutionService>? federatedExecution = null,
        ILogger<GovernedSqlExecutionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(executionGuard);
        ArgumentNullException.ThrowIfNull(rewrite);
        ArgumentNullException.ThrowIfNull(execution);
        _federatedExecution = federatedExecution;

        _rewriter = new GovernedSqlRewriter(
            options,
            rewrite.PolicyEnforcement,
            rewrite.ConsentResolution,
            rewrite.TableRepository,
            rewrite.ClientIpResolver,
            logger,
            rewrite.ConsentRepository,
            rewrite.SqlEngine,
            rewrite.PolicyFactory,
            rewrite.SqlSecurityValidator,
            rewrite.RebacEvaluator,
            rewrite.ConsentCache,
            rewrite.MandatoryFilters,
            rewrite.ContractManager,
            rewrite.AccessProfileRepository,
            rewrite.MemoryCache,
            typedPolicies: rewrite.TypedPolicies,
            timeZoneState: rewrite.TimeZoneState);

        _executor = new GovernedSqlExecutor(
            _rewriter,
            options,
            auditWriter ?? throw new ArgumentNullException(nameof(auditWriter)),
            executionGuard,
            execution.ConnectionFactory,
            execution.Environment,
            logger,
            execution.ConcurrencyGate,
            execution.SessionInitializer,
            execution.SecretProvider,
            execution.AuditDigester);
    }

    /// <summary>Fail-closed: a cross-source query without a federated executor is rejected, never executed locally.</summary>
    private IFederatedQueryExecutionService ResolveFederatedService() =>
        _federatedExecution?.Value
        ?? throw new WebSqlPolicyException("Cross-source query execution service is not configured.");

    public async Task ValidateAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        try
        {
            await _rewriter.ValidateAsync(rawSql, user, tenantId, ct).ConfigureAwait(false);
        }
        catch (CrossSourceRoutingException)
        {
            var federatedService = ResolveFederatedService();
            await federatedService.ValidateAsync(rawSql, user, tenantId, ct).ConfigureAwait(false);
        }
    }

    internal Task<TrinoSqlEngine.Ast.Emit.CompiledSql> CompileAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
        _rewriter.CompileAsync(rawSql, user, tenantId, ct);

    public async Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default)
    {
        try
        {
            await _executor.ExecuteGovernedQueryAsync(request, user, tenantId, rowWriter, ct).ConfigureAwait(false);
        }
        catch (CrossSourceRoutingException)
        {
            var federatedService = ResolveFederatedService();
            await federatedService.ExecuteGovernedQueryAsync(request, user, tenantId, rowWriter, ct).ConfigureAwait(false);
        }
    }

    public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        try
        {
            return await _executor.ExecuteQueryBufferedAsync(request, user, tenantId, ct).ConfigureAwait(false);
        }
        catch (CrossSourceRoutingException)
        {
            var federatedService = ResolveFederatedService();
            return await federatedService.ExecuteQueryBufferedAsync(request, user, tenantId, ct).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamRowsAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = System.Threading.Channels.Channel.CreateBounded<IReadOnlyDictionary<string, object?>>(
            new System.Threading.Channels.BoundedChannelOptions(1)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerToken = producerCts.Token;

        var executionTask = Task.Run(async () =>
        {
            try
            {
                await ExecuteGovernedQueryAsync(
                    request,
                    user,
                    tenantId,
                    async (reader, token) =>
                    {
                        var columnDescriptions = SqlResultColumns.Describe(reader);
                        var columns = new string[columnDescriptions.Count];
                        for (int i = 0; i < columnDescriptions.Count; i++)
                        {
                            columns[i] = columnDescriptions[i].Name;
                        }

                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                            }

                            await channel.Writer.WriteAsync(row, token).ConfigureAwait(false);
                        }
                    },
                    producerToken).ConfigureAwait(false);

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, CancellationToken.None);

        bool completedEnumerating = false;
        try
        {
            await foreach (var row in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return row;
            }
            completedEnumerating = true;
        }
        finally
        {
            if (!completedEnumerating)
            {
                await producerCts.CancelAsync().ConfigureAwait(false);
            }
        }

        await executionTask.ConfigureAwait(false);
    }

    // -------------------------------------------------------------
    // Backward-Compatible Static Helper Methods
    // -------------------------------------------------------------

    internal static bool IsDataSourceAllowedForTenant(WebSqlOptions webSqlOptions, TenantId tenantId, string dataSourceName)
    {
        var tenantAllowlist = webSqlOptions.TenantDataSourceAllowlist;
        if (tenantAllowlist == null || tenantAllowlist.Count == 0 || string.IsNullOrEmpty(tenantId.Value))
        {
            return true;
        }

        foreach (var entry in tenantAllowlist)
        {
            if (!string.Equals(entry.Key, tenantId.Value, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool listed = false;
            if (entry.Value != null)
            {
                foreach (var candidate in entry.Value)
                {
                    if (string.Equals(candidate, dataSourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        listed = true;
                        break;
                    }
                }
            }

            if (!listed)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsDataSourceQueryable(WebSqlOptions webSqlOptions, TenantId tenantId, string dataSourceName)
    {
        ArgumentNullException.ThrowIfNull(webSqlOptions);
        return webSqlOptions.Enabled &&
               !string.IsNullOrWhiteSpace(dataSourceName) &&
               SqlDataSourceResolver.TryResolveGloballyAllowedDataSource(webSqlOptions, dataSourceName, out var resolved) &&
               IsDataSourceAllowedForTenant(webSqlOptions, tenantId, resolved);
    }

    internal static bool TryMapProviderToDialect(string? provider, out DatabaseDialect dialect) =>
        DataSourceProvider.TryResolveDialect(provider, out dialect) && SqlDialectMapper.IsExecutable(dialect);

    internal static bool ReferencesColumn(string? tableOrAlias, string referencedColumn, string columnName, TableAccessTarget target) =>
        SqlDataSourceResolver.ReferencesColumn(tableOrAlias, referencedColumn, columnName, target);

    internal static void EnforceMaskedColumnGuardrails(
        TableAccessTarget target,
        TableMetadata tableMeta,
        TableAccessDecision decision,
        SqlQueryMetadata metadata)
    {
        foreach (var col in tableMeta.Columns)
        {
            var lvl = decision.GetEffectiveColumnAccess(col.ColumnName, tableMeta);
            bool isEffectiveHmac = false;
            if (lvl == ColumnAccessLevel.Mask && tableMeta.ColumnMaskingRules != null && tableMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var rule))
            {
                if (string.Equals(rule.RuleType, "HMAC", StringComparison.OrdinalIgnoreCase))
                {
                    isEffectiveHmac = true;
                }
            }

            if (lvl != ColumnAccessLevel.Clear && !isEffectiveHmac)
            {
                bool isUsedInJoin = false;
                if (metadata.JoinColumnReferences != null && metadata.JoinColumnReferences.Count > 0)
                {
                    isUsedInJoin = metadata.JoinColumnReferences.Any(jc => ReferencesColumn(jc.TableOrAlias, jc.ColumnName, col.ColumnName, target));
                }

                if (isUsedInJoin)
                {
                    string ruleDesc = tableMeta.ColumnMaskingRules != null && tableMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule)
                        ? mRule.RuleType ?? "REDACT"
                        : (lvl == ColumnAccessLevel.Deny ? "DENY" : "ABAC_MASK");

                    throw ColumnPolicyViolation(lvl,
                        $"Security Policy Violation: Column '{col.ColumnName}' in table '{target.FullName}' is protected by static redaction ('{ruleDesc}') and cannot be used in a relational JOIN predicate. Joining on static constants produces false Cartesian cross-products and enables side-channel join inference attacks. Configure deterministic HMAC pseudonymization (RuleType = 'HMAC') or join on surrogate foreign keys (e.g. ID).");
                }

                bool isUsedInFilter = false;
                if (metadata.FilterColumnReferences != null && metadata.FilterColumnReferences.Count > 0)
                {
                    isUsedInFilter = metadata.FilterColumnReferences.Any(fc => ReferencesColumn(fc.TableOrAlias, fc.ColumnName, col.ColumnName, target));
                }

                if (isUsedInFilter)
                {
                    string ruleDesc = tableMeta.ColumnMaskingRules != null && tableMeta.ColumnMaskingRules.TryGetValue(col.ColumnName, out var mRule)
                        ? mRule.RuleType ?? "REDACT"
                        : (lvl == ColumnAccessLevel.Deny ? "DENY" : "ABAC_MASK");

                    throw ColumnPolicyViolation(lvl,
                        $"Security Policy Violation: Column '{col.ColumnName}' in table '{target.FullName}' is protected by static redaction or access policy ('{ruleDesc}') and cannot be used in a filter predicate (WHERE/HAVING/ORDER BY). Filtering or sorting on masked or denied columns would run against the redacted value and is forbidden to prevent oracle inference attacks.");
                }
            }
        }
    }

    // SEC-X1-05: a denied (hidden) column answers like a column that does not exist (SQL_POLICY_DENIED); a masked column keeps the statement-level code.
    private static WebSqlPolicyException ColumnPolicyViolation(ColumnAccessLevel level, string message) =>
        level == ColumnAccessLevel.Deny ? new WebSqlTableDeniedException(message) : new WebSqlPolicyException(message);

    internal static bool MatchesPostgreSqlCatalogName(TableAccessTarget target, TableIdentifier catalogTable)
    {
        var physicalTable = target.TableNameQuoted ? target.TableName : target.TableName.ToLowerInvariant();
        if (!string.Equals(physicalTable, catalogTable.TableName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(target.Schema))
        {
            var physicalSchema = target.SchemaQuoted ? target.Schema : target.Schema.ToLowerInvariant();
            if (!string.Equals(physicalSchema, catalogTable.Schema, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static void EnsureClientParameterNames(IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters == null || parameters.Count == 0) return;

        foreach (var paramName in parameters.Keys)
        {
            var trimmed = paramName.TrimStart('@');
            if (trimmed.StartsWith(InternalParameterPrefix, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("p_rls_", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("rls_", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith(ClientParameterPlaceholderPrefix, StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("p_pd_", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("__rls_ks", StringComparison.OrdinalIgnoreCase))
            {
                throw new WebSqlPolicyException("A request parameter uses a reserved gateway parameter name.");
            }
        }
    }

    /// <summary>
    /// Issue #280: the client parameters plus the gateway-generated parameters of a planned read, for name normalization only.
    /// Fail-closed: a gateway map on a non-planned read, a collision, a list value, a reserved or non-<c>__gql_tf_</c> name, or too many
    /// parameters throws. Messages never carry a name or a value.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?>? MergeGatewayParameters(GovernedSqlQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gateway = request.GatewayParameters;
        if (gateway == null || gateway.Count == 0)
        {
            return request.Parameters;
        }

        if (!request.PlannedTableRead)
        {
            throw new System.Security.SecurityException("Gateway parameters require a planned table read (fail-closed).");
        }

        if (gateway.Count > Autheris.Application.Sql.Tree.TreeSqlCompiler.MaxParameters)
        {
            throw new System.Security.SecurityException("Too many gateway parameters (fail-closed).");
        }

        var tfPrefix = Autheris.Application.Sql.Tree.TreeFilterClauseBuilder.ParameterPrefix.TrimStart('@');
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (request.Parameters != null)
        {
            foreach (var (name, value) in request.Parameters)
            {
                merged.TryAdd(name.TrimStart('@'), value);
            }
        }

        foreach (var (name, value) in gateway)
        {
            var trimmed = name.TrimStart('@');
            if (string.IsNullOrEmpty(trimmed) ||
                !trimmed.StartsWith(tfPrefix, StringComparison.OrdinalIgnoreCase) ||
                Autheris.Application.Identity.Attributes.CallerAttributeParameterName.IsReserved(trimmed) ||
                Autheris.Application.Sql.ListParameters.ListParameterNormalizer.IsListCandidate(value) ||
                !IsGatewayScalar(value))
            {
                throw new InvalidOperationException("A gateway parameter is not a scalar planned-read parameter.");
            }

            if (!merged.TryAdd(trimmed, value))
            {
                throw new System.Security.SecurityException("A statement parameter name is bound twice (fail-closed).");
            }
        }

        return merged;
    }

    private static bool IsGatewayScalar(object? value) =>
        value is null or string or decimal or Guid or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan ||
        (value.GetType() is { IsPrimitive: true } or { IsEnum: true });

    internal static string NormalizeClientParameters(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        out IReadOnlyCollection<string> replacedNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        replacedNames = names;
        if (parameters == null || parameters.Count == 0 || sql.IndexOf('@') < 0)
        {
            return sql;
        }

        if (sql.Contains(ClientParameterPlaceholderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebSqlPolicyException("The SQL statement uses a reserved identifier prefix.");
        }

        var supplied = new HashSet<string>(parameters.Keys.Select(k => k.TrimStart('@')), StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(sql.Length + 16);
        char quote = '\0';
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote)
                    {
                        sb.Append(sql[++i]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            bool startsParameter = c == '@'
                && i + 1 < sql.Length && (char.IsAsciiLetter(sql[i + 1]) || sql[i + 1] == '_')
                && (i == 0 || !(char.IsAsciiLetterOrDigit(sql[i - 1]) || sql[i - 1] is '_' or '@'));
            if (startsParameter)
            {
                int end = i + 1;
                while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_'))
                {
                    end++;
                }

                string name = sql[(i + 1)..end];
                if (supplied.Contains(name))
                {
                    sb.Append(ClientParameterPlaceholderPrefix).Append(name);
                    names.Add(name);
                    i = end - 1;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
