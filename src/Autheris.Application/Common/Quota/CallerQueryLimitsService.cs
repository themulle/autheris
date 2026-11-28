namespace Autheris.Application.Common.Quota;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.State.Caching.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>The effective query limits of one caller, resolved exactly as the execution guard resolves them.</summary>
public interface ICallerQueryLimitsService
{
    Task<CallerQueryLimits> ResolveAsync(ClaimsPrincipal principal, string? apiKey, string? clientIp, CancellationToken ct = default);
}

/// <summary>
/// Reports the limits that apply to the calling token. The values come from the same tier resolver and options the enforcing code reads, so
/// the report equals what is enforced. Only the caller's own resolved values are returned.
/// </summary>
public sealed class CallerQueryLimitsService(IClientTierResolver tierResolver, IOptionsMonitor<GatewayOptions> options) : ICallerQueryLimitsService
{
    private const string Installation = "installation";
    private const string TierSource = "tier";
    private const string ScopeSource = "scope";

    public async Task<CallerQueryLimits> ResolveAsync(ClaimsPrincipal principal, string? apiKey, string? clientIp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedAccessException("Authentication is required to read the query limits.");
        }

        var o = options.CurrentValue;
        var context = await tierResolver.ResolveAsync(principal, apiKey, clientIp, ct).ConfigureAwait(false);
        var policy = context.Policy;
        var tierPolicy = ClientQuotaPolicy.ForTier(context.Tier, o.RateLimiting.ClientTiers);

        LimitValue FromPolicy(int? effective, int? tierValue) =>
            effective is > 0
                ? new LimitValue(effective, effective == tierValue ? TierSource : ScopeSource)
                : new LimitValue(null, Installation);

        var maxRows = FromPolicy(policy.MaxResultRows, tierPolicy.MaxResultRows);
        var duration = FromPolicy(policy.MaxQueryDurationSeconds, tierPolicy.MaxQueryDurationSeconds);
        var reads = o.DataSources.MaxConcurrentReadsPerUserAndTable;
        var concurrency = new ConcurrencyLimits(
            reads > 0 ? new LimitValue(reads, Installation) : new LimitValue(null, Installation),
            reads > 0 ? new LimitValue(o.DataSources.TableReadWaitTimeoutMs, Installation) : new LimitValue(null, Installation),
            FromPolicy(policy.MaxConcurrentQueries, tierPolicy.MaxConcurrentQueries));

        return new CallerQueryLimits(
            context.Tier.ToString(),
            concurrency,
            duration,
            maxRows,
            o.WebSql.Enabled ? Sql(o.WebSql, maxRows, duration) : null,
            GraphQl(o.GraphQL, policy, tierPolicy, maxRows));
    }

    private static SqlQueryLimits Sql(WebSqlOptions webSql, LimitValue maxRows, LimitValue duration)
    {
        var installationLimit = SqlRowLimit.For(webSql);
        var limit = maxRows.Value is { } cap ? installationLimit.CappedAt(cap) : installationLimit;
        long defaultRows = limit.Effective(null);
        long maxWithLimit = limit.Effective(long.MaxValue);
        string RowSource(long effective, long installationValue) => effective < installationValue ? maxRows.Source : Installation;

        var timeout = webSql.ExecutionTimeoutSeconds;
        var timeoutValue = duration.Value is { } d && d < timeout
            ? new LimitValue(d, duration.Source)
            : new LimitValue(timeout, Installation);

        return new SqlQueryLimits(
            new LimitValue(defaultRows, RowSource(defaultRows, installationLimit.Effective(null))),
            new LimitValue(maxWithLimit, RowSource(maxWithLimit, installationLimit.Effective(long.MaxValue))),
            new LimitValue(webSql.Paging.MaxOffset, Installation),
            new LimitValue(webSql.MaxListParameterValues, Installation),
            new LimitValue(webSql.MaxListParameters, Installation),
            timeoutValue,
            new LimitValue(webSql.Paging.TokenLifetimeMinutes, Installation));
    }

    private static GraphQlQueryLimits GraphQl(GraphQLOptions gql, ClientQuotaPolicy policy, ClientQuotaPolicy tierPolicy, LimitValue maxRows)
    {
        var rows = maxRows.Value is { } cap && cap < gql.MaxResponseRows
            ? new LimitValue(cap, maxRows.Source)
            : new LimitValue(gql.MaxResponseRows, Installation);
        var builtInCost = ClientQuotaPolicy.ForTier(policy.Tier).MaxCostPerQuery;
        var costSource = policy.MaxCostPerQuery != tierPolicy.MaxCostPerQuery
            ? ScopeSource
            : tierPolicy.MaxCostPerQuery == builtInCost ? Installation : TierSource;
        var cost = new LimitValue(policy.MaxCostPerQuery, costSource);
        return new GraphQlQueryLimits(
            rows,
            new LimitValue(gql.MaxResponseBytes, Installation),
            new LimitValue(gql.MaxAllowedOffset, Installation),
            cost);
    }
}
