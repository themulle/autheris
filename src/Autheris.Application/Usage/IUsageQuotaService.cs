namespace Autheris.Application.Usage;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;

/// <summary>Outcome of evaluating configured usage quotas for a request identity.</summary>
public enum UsageQuotaDecisionKind
{
    Allow,
    AllowWithWarning,
    Exceeded,
    Unavailable
}

/// <summary>Soft-limit warning metadata emitted via <c>X-Usage-Quota-Warning</c>.</summary>
public sealed record UsageQuotaWarning(
    UsageQuotaScope Scope,
    UsageQuotaMetric Metric);

/// <summary>Decision produced by synchronous hot-path quota evaluation.</summary>
public sealed record UsageQuotaDecision(
    UsageQuotaDecisionKind Kind,
    UsageQuotaScope? ViolatedScope = null,
    UsageQuotaMetric? ViolatedMetric = null,
    UsageQuotaPeriod? ViolatedPeriod = null,
    decimal? Limit = null,
    int RetryAfterSeconds = 0,
    IReadOnlyList<UsageQuotaWarning>? Warnings = null)
{
    public static readonly UsageQuotaDecision AllowDecision = new(UsageQuotaDecisionKind.Allow);
}

/// <summary>Evaluates per-user, per-group, billing-group, and tenant usage quotas against cached DB totals and unflushed counters.</summary>
public interface IUsageQuotaService
{
    /// <summary>Synchronously evaluates applicable quotas for <paramref name="identity"/> without performing blocking I/O.</summary>
    UsageQuotaDecision Evaluate(UsageIdentity identity);

    /// <summary>Refreshes cached usage snapshots from the usage repository for configured quota tenants.</summary>
    Task RefreshAsync(CancellationToken ct = default);
}
