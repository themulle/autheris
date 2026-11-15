namespace Autheris.Api.Middleware;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
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
        ICallerContextProvider? callerProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsSkippedPath(context.Request.Path))
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

    private static bool IsSkippedPath(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
}
