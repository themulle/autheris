namespace Autheris.Api.Endpoints;

using System.Collections.Generic;
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
    internal static Task<IResult> GetGraphQlSchemaStatusAsync(
        ICatalogSchemaFreshness? freshness,
        HttpContext httpContext,
        SchemaStatusAuditSampler? sampler,
        CancellationToken ct) =>
        GetGraphQlSchemaStatusCoreAsync(freshness, httpContext, sampler, ct);

    /// <summary>
    /// A client polls this route until the schema is current, so one audit row per poll would flood the audit chain.
    /// Polls are aggregated per caller (tenant and subject): at most one row per caller and
    /// <see cref="SchemaStatusAuditSampler.Interval"/>, carrying the number of polls it stands for. The first poll of every
    /// caller is always audited; the other polls are marked handled and write nothing. Denials never reach this handler.
    /// </summary>
    private static async Task<IResult> GetGraphQlSchemaStatusCoreAsync(
        ICatalogSchemaFreshness? freshness,
        HttpContext httpContext,
        SchemaStatusAuditSampler? sampler,
        CancellationToken ct)
    {
        if (sampler is not null && httpContext.Features.Get<AuditContext>() is { } audit)
        {
            Autheris.Api.Middleware.AccessAuditMiddleware.ResolveSecurityContext(httpContext, audit);
            var suppressed = sampler.TryAcquire(audit.TenantId.Value, audit.ActorSid.Value);
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

/// <summary>
/// Admits one audit row per caller and interval; the admitted poll learns how many polls were folded into it. Bounded: at most
/// <c>maxEntries</c> callers are tracked (entries expire after <see cref="Interval"/>); when the map is full every request is
/// audited instead of suppressed.
/// </summary>
internal sealed class SchemaStatusAuditSampler(TimeProvider? timeProvider = null, int maxEntries = 10_000)
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PruneSpacing = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Tenant, string Sid), (long Admitted, int Suppressed)> _callers = [];
    private long _lastPrune = long.MinValue;

    /// <summary>-1: not audited. Otherwise the number of polls suppressed for this caller since its previous audited one.</summary>
    internal int TryAcquire(string? tenant, string? sid)
    {
        if (string.IsNullOrEmpty(sid) || sid == "S-1-0-0")
        {
            return 0; // unresolved identity: never aggregate across unknown callers
        }

        var key = (tenant ?? string.Empty, sid);
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_callers.TryGetValue(key, out var entry))
            {
                if (_time.GetElapsedTime(entry.Admitted, now) < Interval)
                {
                    _callers[key] = (entry.Admitted, entry.Suppressed + 1);
                    return -1;
                }

                _callers[key] = (now, 0);
                return entry.Suppressed;
            }

            if (_callers.Count >= maxEntries)
            {
                Prune(now);
                if (_callers.Count >= maxEntries)
                {
                    return 0; // overflow: audit every request
                }
            }

            _callers[key] = (now, 0);
            return 0;
        }
    }

    private void Prune(long now)
    {
        if (_lastPrune != long.MinValue && _time.GetElapsedTime(_lastPrune, now) < PruneSpacing)
        {
            return;
        }

        _lastPrune = now;
        List<(string, string)>? expired = null;
        foreach (var (key, entry) in _callers)
        {
            if (_time.GetElapsedTime(entry.Admitted, now) >= Interval)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _callers.Remove(key);
            }
        }
    }
}
