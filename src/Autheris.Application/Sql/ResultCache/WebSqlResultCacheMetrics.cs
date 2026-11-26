namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using Autheris.Domain.Diagnostics;

/// <summary>
/// Instruments of the WebSQL result cache. Labels are limited to the configured logical source and fixed outcome and reason codes;
/// SQL, user ids, tenants and key hashes never appear.
/// </summary>
public static class WebSqlResultCacheMetrics
{
    private static readonly ConcurrentDictionary<object, Func<(long Bytes, long Entries)>> Stores = new();

    private static readonly Counter<long> Requests = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_websql_result_cache_requests_total",
        description: "WebSQL requests by result cache outcome");

    private static readonly Counter<long> Invalidations = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_websql_result_cache_invalidations_total",
        description: "WebSQL result cache entries removed before they were served again");

    private static readonly ObservableGauge<long> BytesGauge = GatewayDiagnostics.Meter.CreateObservableGauge(
        "autheris_websql_result_cache_bytes", ObserveBytes, unit: "By", description: "Estimated bytes held by the WebSQL result cache");

    private static readonly ObservableGauge<long> EntriesGauge = GatewayDiagnostics.Meter.CreateObservableGauge(
        "autheris_websql_result_cache_entries", ObserveEntries, description: "Entries held by the WebSQL result cache");

    /// <summary>Counts one request. <paramref name="source"/> is the configured logical data source name.</summary>
    public static void RecordRequest(string source, WebSqlResultCacheStatus status, string? reason = null)
    {
        Requests.Add(
            1,
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("outcome", OutcomeName(status)),
            new KeyValuePair<string, object?>("reason", reason ?? string.Empty));
    }

    public static void RecordInvalidation(string reason) =>
        Invalidations.Add(1, new KeyValuePair<string, object?>("reason", reason));

    internal static IDisposable Register(Func<(long Bytes, long Entries)> sample)
    {
        var token = new object();
        Stores[token] = sample;
        return new Registration(token);
    }

    internal static (long Bytes, long Entries) Observed()
    {
        long bytes = 0;
        long entries = 0;
        foreach (var sample in Stores.Values)
        {
            var (b, e) = sample();
            bytes += b;
            entries += e;
        }

        return (bytes, entries);
    }

    private static long ObserveBytes() => Observed().Bytes;

    private static long ObserveEntries() => Observed().Entries;

    private static string OutcomeName(WebSqlResultCacheStatus status) => status switch
    {
        WebSqlResultCacheStatus.Hit => "hit",
        WebSqlResultCacheStatus.Miss => "miss",
        WebSqlResultCacheStatus.Store => "store",
        WebSqlResultCacheStatus.Bypass => "bypass",
        _ => "ineligible",
    };

    private sealed class Registration(object token) : IDisposable
    {
        public void Dispose() => Stores.TryRemove(token, out _);
    }
}
