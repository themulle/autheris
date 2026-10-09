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
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;

/// <summary>
/// AR-05: Coordinates governed SQL rewrite and database execution.
/// Delegating pipeline stages to <see cref="GovernedSqlRewriter"/> and execution to <see cref="GovernedSqlExecutor"/>.
/// </summary>
public sealed class GovernedSqlExecutionService : IGovernedSqlExecutionService, ISqlRewritePipeline
{
    internal const string InternalParameterPrefix = "__gql_";
    private const string ClientParameterPlaceholderPrefix = "__param_";

    private readonly GovernedSqlRewriter _rewriter;
    private readonly GovernedSqlExecutor _executor;
    private readonly IMandatoryRowFilterResolver? _rowFilterResolver;

    public IReadOnlyList<SqlRewriteStageOrder> StageOrder => _rewriter.StageOrder;

    public GovernedSqlExecutionService(
        IOptions<GatewayOptions> options,
        IPolicyEnforcementService? policyEnforcement = null,
        IConsentResolutionService? consentResolution = null,
        ITableMetadataRepository? tableRepository = null,
        ISqlConnectionFactory? connectionFactory = null,
        IClientIpResolver? clientIpResolver = null,
        IHostEnvironment? environment = null,
        ILogger<GovernedSqlExecutionService>? logger = null,
        IConsentRepository? consentRepository = null,
        IKeyVaultSecretProvider? secretProvider = null,
        ISqlEngine? sqlEngine = null,
        ICompiledSqlQueryPlanCache? planCache = null,
        ISqlSecurityValidator? sqlSecurityValidator = null,
        ITableReadConcurrencyGate? concurrencyGate = null,
        IDbSessionContextInitializer? sessionInitializer = null,
        IRebacEvaluator? rebacEvaluator = null,
        IConsentCacheService? consentCache = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        IMemoryCache? memoryCache = null)
        : this(options, NullAuditLogRepository.Instance, policyEnforcement, consentResolution, tableRepository, connectionFactory, clientIpResolver, environment, logger, consentRepository, secretProvider, sqlEngine, planCache, sqlSecurityValidator, concurrencyGate, sessionInitializer, rebacEvaluator, consentCache, mandatoryFilters, contractManager, accessProfileRepository, memoryCache)
    {
    }

    public GovernedSqlExecutionService(
        IOptions<GatewayOptions> options,
        IAuditLogRepository auditLogRepository,
        IPolicyEnforcementService? policyEnforcement = null,
        IConsentResolutionService? consentResolution = null,
        ITableMetadataRepository? tableRepository = null,
        ISqlConnectionFactory? connectionFactory = null,
        IClientIpResolver? clientIpResolver = null,
        IHostEnvironment? environment = null,
        ILogger<GovernedSqlExecutionService>? logger = null,
        IConsentRepository? consentRepository = null,
        IKeyVaultSecretProvider? secretProvider = null,
        ISqlEngine? sqlEngine = null,
        ICompiledSqlQueryPlanCache? planCache = null,
        ISqlSecurityValidator? sqlSecurityValidator = null,
        ITableReadConcurrencyGate? concurrencyGate = null,
        IDbSessionContextInitializer? sessionInitializer = null,
        IRebacEvaluator? rebacEvaluator = null,
        IConsentCacheService? consentCache = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        ISchemaContractManager? contractManager = null,
        IAccessProfileRepository? accessProfileRepository = null,
        IMemoryCache? memoryCache = null)
    {
        _rewriter = new GovernedSqlRewriter(
            options,
            policyEnforcement,
            consentResolution,
            tableRepository,
            clientIpResolver,
            logger,
            consentRepository,
            secretProvider,
            sqlEngine,
            planCache,
            sqlSecurityValidator,
            rebacEvaluator,
            consentCache,
            mandatoryFilters,
            contractManager,
            accessProfileRepository,
            memoryCache);

        _rowFilterResolver = mandatoryFilters;

        _executor = new GovernedSqlExecutor(
            _rewriter,
            options,
            auditLogRepository ?? NullAuditLogRepository.Instance,
            connectionFactory,
            environment,
            logger,
            concurrencyGate,
            sessionInitializer);
    }

    public Task<string> RewriteSqlAsync(
        string rawSql,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default) =>
        _rewriter.RewriteSqlAsync(rawSql, user, tenantId, ct);

    public Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        Func<DbDataReader, CancellationToken, Task> rowWriter,
        CancellationToken ct = default) =>
        _executor.ExecuteGovernedQueryAsync(request, user, tenantId, rowWriter, ct);

    public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default) =>
        _executor.ExecuteQueryBufferedAsync(request, user, tenantId, ct);

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
        DataSourceProvider.TryResolveDialect(provider, out dialect) && IsWebSqlSupportedDialect(dialect);

    internal static bool IsWebSqlSupportedDialect(DatabaseDialect dialect) =>
        dialect is DatabaseDialect.PostgreSql or DatabaseDialect.SqlServer or DatabaseDialect.Sqlite;

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

    internal static string RestoreClientParameters(string securedSql, IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
        {
            return securedSql;
        }

        return Regex.Replace(
            securedSql,
            "[\"`\\[]?" + ClientParameterPlaceholderPrefix + "([A-Za-z_][A-Za-z0-9_]*)[\"`\\]]?",
            m => names.Contains(m.Groups[1].Value) ? "@" + m.Groups[1].Value : m.Value,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
