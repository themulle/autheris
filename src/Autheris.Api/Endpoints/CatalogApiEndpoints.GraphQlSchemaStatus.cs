namespace Autheris.Api.Endpoints;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.GraphQL.Catalog;
using Microsoft.AspNetCore.Http;

public static partial class CatalogApiEndpoints
{
    /// <summary>
    /// Issue #802: <c>GET /api/v1/catalog/graphql-schema-status</c> answers 200 <c>{"status":"Current"}</c> or 503
    /// <c>{"status":"Updating"}</c> with <c>Retry-After: 1</c>, and 404 when the GraphQL catalog module is not registered. A client
    /// polls it after a catalog write before it queries GraphQL. The body never carries catalog names, counts or versions.
    /// </summary>
    internal static Task<IResult> GetGraphQlSchemaStatusAsync(ICatalogSchemaFreshness? freshness, HttpContext httpContext, CancellationToken ct) =>
        GetGraphQlSchemaStatusAsync(freshness, httpContext, SchemaStatusAuditSampler.Shared, ct);

    /// <summary>
    /// A client polls this route until the schema is current, so one audit row per poll would flood the audit chain.
    /// Polls are aggregated: at most one row per <see cref="SchemaStatusAuditSampler.Interval"/>, carrying the number of
    /// polls it stands for. The other polls are marked handled and write nothing; denials never reach this handler.
    /// </summary>
    internal static async Task<IResult> GetGraphQlSchemaStatusAsync(
        ICatalogSchemaFreshness? freshness,
        HttpContext httpContext,
        SchemaStatusAuditSampler sampler,
        CancellationToken ct)
    {
        if (httpContext.Features.Get<AuditContext>() is { } audit)
        {
            var suppressed = sampler.TryAcquire();
            if (suppressed < 0)
            {
                audit.MarkHandled();
            }
            else if (suppressed > 0)
            {
                audit.AdditionalDetails["aggregatedPolls"] = suppressed + 1;
            }
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        if (freshness is null)
        {
            return Results.NotFound();
        }

        if (await freshness.IsSchemaCurrentAsync(ct).ConfigureAwait(false))
        {
            return Results.Ok(new { status = "Current" });
        }

        httpContext.Response.Headers.RetryAfter = "1";
        return Results.Json(new { status = "Updating" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}

/// <summary>Admits one audit row per interval; the admitted poll learns how many polls were folded into it.</summary>
internal sealed class SchemaStatusAuditSampler(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    internal static SchemaStatusAuditSampler Shared { get; } = new();

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private long _lastAdmitted = long.MinValue;
    private int _suppressed;

    /// <summary>-1: not audited. Otherwise the number of polls suppressed since the previous audited one.</summary>
    internal int TryAcquire()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_lastAdmitted != long.MinValue && _time.GetElapsedTime(_lastAdmitted, now) < Interval)
            {
                _suppressed++;
                return -1;
            }

            _lastAdmitted = now;
            var folded = _suppressed;
            _suppressed = 0;
            return folded;
        }
    }
}
