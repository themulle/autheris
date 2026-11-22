namespace Autheris.Api.Middleware;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Usage;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

/// <summary>
/// Opens a <see cref="UsageRequestScope"/> for authenticated, tenanted requests and measures uncompressed
/// response bytes and wall-clock duration without adding awaits on the metering path.
/// </summary>
public sealed class UsageMeteringMiddleware
{
    private readonly RequestDelegate _next;

    public UsageMeteringMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public Task InvokeAsync(
        HttpContext context,
        IUsageAggregator? aggregator = null,
        IIngressTransportContext? ingress = null,
        ICallerContextProvider? callerProvider = null,
        IUsageQuotaService? quotaService = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsSkippedRequest(context))
        {
            return _next(context);
        }

        if (!context.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var item) ||
            item is not SecurityPrincipalContext secContext ||
            !secContext.IsAuthenticated ||
            secContext.ResolvedTenant is not { } tenant)
        {
            UsageDiagnostics.UnattributedCounter.Add(1);
            return _next(context);
        }

        var transport = ingress?.Current ?? DataSourceTransportFlags.None;
        if (transport == DataSourceTransportFlags.None)
        {
            transport = DataSourceTransportFlags.Rest;
        }

        var caller = secContext.Caller ?? callerProvider?.ResolveCallerContext();
        var identity = new UsageIdentity(tenant, secContext.UserSid, secContext.GroupSids);

        if (quotaService is not null && !IsQuotaExemptPath(context.Request.Path))
        {
            var decision = quotaService.Evaluate(identity);
            if (decision.Kind is UsageQuotaDecisionKind.Exceeded or UsageQuotaDecisionKind.Unavailable)
            {
                return WriteQuotaRejectionAsync(context, identity, transport, caller, aggregator, decision);
            }

            if (decision.Kind == UsageQuotaDecisionKind.AllowWithWarning && decision.Warnings is { Count: > 0 } warnings)
            {
                context.Response.Headers["X-Usage-Quota-Warning"] = FormatWarnings(warnings);
            }
        }

        return ExecuteMeteredAsync(context, identity, transport, caller, aggregator);
    }

    private async Task ExecuteMeteredAsync(
        HttpContext context,
        UsageIdentity identity,
        DataSourceTransportFlags transport,
        CallerContext? caller,
        IUsageAggregator? aggregator)
    {
        using var scope = UsageRequestScope.Begin(identity, transport, caller, aggregator);
        var originalBody = context.Features.Get<IHttpResponseBodyFeature>();
        CountingResponseBodyFeature? countingFeature = null;
        if (originalBody is not null)
        {
            countingFeature = new CountingResponseBodyFeature(originalBody);
            context.Features.Set<IHttpResponseBodyFeature>(countingFeature);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            if (originalBody is not null)
            {
                context.Features.Set(originalBody);
            }

            var durationMs = Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var bytes = countingFeature?.BytesWritten ?? 0;
            countingFeature?.Dispose();
            scope.Complete(durationMs, bytes, context.Response.StatusCode);
        }
    }

    private static async Task WriteQuotaRejectionAsync(
        HttpContext context,
        UsageIdentity identity,
        DataSourceTransportFlags transport,
        CallerContext? caller,
        IUsageAggregator? aggregator,
        UsageQuotaDecision decision)
    {
        using var scope = UsageRequestScope.Begin(identity, transport, caller, aggregator);
        int statusCode;
        string errorCode;
        string detail;
        Dictionary<string, object?> ext;

        if (decision.RetryAfterSeconds > 0)
        {
            context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        if (decision.Kind == UsageQuotaDecisionKind.Exceeded)
        {
            statusCode = StatusCodes.Status429TooManyRequests;
            errorCode = ApiErrorCodes.UsageQuotaExceeded;
            detail = "Usage quota exceeded.";
            ext = new Dictionary<string, object?>
            {
                ["scope"] = decision.ViolatedScope?.ToString(),
                ["metric"] = decision.ViolatedMetric?.ToString(),
                ["period"] = decision.ViolatedPeriod?.ToString(),
                ["limit"] = decision.Limit,
                ["retryAfterSeconds"] = decision.RetryAfterSeconds
            };
        }
        else
        {
            statusCode = StatusCodes.Status503ServiceUnavailable;
            errorCode = ApiErrorCodes.UsageQuotaUnavailable;
            detail = "Usage quota totals are temporarily unavailable.";
            ext = new Dictionary<string, object?>
            {
                ["retryAfterSeconds"] = decision.RetryAfterSeconds
            };
        }

        await ApiProblems.WriteAsync(context, statusCode, errorCode, detail, context.RequestAborted, ext).ConfigureAwait(false);
        scope.Complete(0, 0, statusCode, errorCode);
    }

    private static string FormatWarnings(IReadOnlyList<UsageQuotaWarning> warnings)
    {
        if (warnings.Count == 1)
        {
            return $"{warnings[0].Scope};{warnings[0].Metric}";
        }

        var parts = new string[warnings.Count];
        for (int i = 0; i < warnings.Count; i++)
        {
            parts[i] = $"{warnings[i].Scope};{warnings[i].Metric}";
        }

        return string.Join(", ", parts);
    }

    private static bool IsQuotaExemptPath(PathString path) =>
        path.StartsWithSegments("/api/v1/usage/me", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkippedRequest(HttpContext context) =>
        GatewayEndpointMetadata.IsHealthProbe(context) ||
        GatewayEndpointMetadata.IsMetricsEndpoint(context) ||
        context.Request.Path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase);
}
