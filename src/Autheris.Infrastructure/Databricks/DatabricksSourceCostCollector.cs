namespace Autheris.Infrastructure.Databricks;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Collects delayed query compute and cost metrics from the Databricks SQL Query History API 2.0.
/// Enforces tenant-scoped credential acquisition (failing closed when a tenant principal is absent),
/// host policy validation, no redirect following, bounded JSON reads, and numeric bounds checks.
/// </summary>
public sealed partial class DatabricksSourceCostCollector(
    HttpClient http,
    IOptions<GatewayOptions> options,
    IDatabricksCredentialProvider credentialProvider,
    ILogger<DatabricksSourceCostCollector>? logger = null) : ISourceCostCollector
{
    public const int MaxBatchSize = 100;
    public const long MaxComputeMs = 1_000_000_000_000L;
    public const decimal MaxSourceCost = 1_000_000_000_000m;
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IOptions<GatewayOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IDatabricksCredentialProvider _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex QueryIdRegex();

    public string SourceType => nameof(DatabaseDialect.Databricks);

    public string CostUnit => "DBU";

    public async Task<IReadOnlyList<SourceCostSample>> CollectAsync(IReadOnlyList<PendingSourceQuery> pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.Count == 0)
        {
            return Array.Empty<SourceCostSample>();
        }

        var groups = new Dictionary<(TenantId Tenant, string DataSource), List<string>>();
        for (int i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            if (string.IsNullOrWhiteSpace(item.SourceQueryId) || !QueryIdRegex().IsMatch(item.SourceQueryId))
            {
                UsageDiagnostics.SourceCostMalformedCounter.Add(1);
                continue;
            }

            var key = (item.Tenant, item.DataSource);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<string>();
                groups[key] = list;
            }

            list.Add(item.SourceQueryId);
        }

        if (groups.Count == 0)
        {
            return Array.Empty<SourceCostSample>();
        }

        var results = new List<SourceCostSample>();
        foreach (var entry in groups)
        {
            var (tenant, dataSource) = entry.Key;
            var db = ResolveDatabricksOptions(dataSource);
            EnsureTenantPrincipalConfigured(db, tenant);

            var delegation = new DelegationContext(TenantId: tenant);
            var token = await _credentialProvider.AcquireTokenAsync(db, delegation, ct).ConfigureAwait(false);

            var uniqueIds = new List<string>(entry.Value.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entry.Value.Count; i++)
            {
                if (seen.Add(entry.Value[i]))
                {
                    uniqueIds.Add(entry.Value[i]);
                }
            }

            for (int offset = 0; offset < uniqueIds.Count; offset += MaxBatchSize)
            {
                int count = Math.Min(MaxBatchSize, uniqueIds.Count - offset);
                var chunk = uniqueIds.GetRange(offset, count);
                var path = BuildHistoryPath(chunk);

                using var doc = await SendGetAsync(db, token, path, delegation, retryAllowed: true, ct).ConfigureAwait(false);
                ParseHistoryResponse(doc.RootElement, chunk, results);
            }
        }

        return results;
    }

    private DatabricksDataSourceOptions ResolveDatabricksOptions(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource) ||
            !_options.Value.DataSources.Connections.TryGetValue(dataSource, out var match) ||
            !DataSourceProvider.Is(match.Provider, DatabaseDialect.Databricks))
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.Configuration,
                "The requested data source is not a configured Databricks data source.");
        }

        var db = match.Databricks ?? throw new DatabricksProviderException(
            DatabricksErrorCodes.Configuration,
            "The Databricks section of the data source is missing.");

        if (!DatabricksHostPolicy.IsValidHost(db.Host))
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.Configuration,
                "The Databricks host must be one exact DNS name without scheme, port, path or wildcard.");
        }

        return db;
    }

    private static void EnsureTenantPrincipalConfigured(DatabricksDataSourceOptions db, TenantId tenant)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value))
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.Configuration,
                "A tenant identifier is required for Databricks cost collection.");
        }

        if (db.Auth.Tenants.Count > 0 && !db.Auth.Tenants.ContainsKey(tenant.Value))
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.Configuration,
                "No Databricks principal configured for the tenant.");
        }
    }

    private static string BuildHistoryPath(List<string> statementIds)
    {
        var sb = new StringBuilder("/api/2.0/sql/history/queries?include_metrics=true&max_results=100");
        for (int i = 0; i < statementIds.Count; i++)
        {
            sb.Append("&filter_by.statement_ids=");
            sb.Append(Uri.EscapeDataString(statementIds[i]));
        }

        return sb.ToString();
    }

    private async Task<JsonDocument> SendGetAsync(
        DatabricksDataSourceOptions db,
        string token,
        string path,
        DelegationContext delegation,
        bool retryAllowed,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{db.Host}{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.Http,
                "Databricks answered with a redirect, which is never followed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _credentialProvider.EvictToken(db, delegation);
                if (retryAllowed)
                {
                    _credentialProvider.InvalidateSecret(db, delegation.TenantId?.Value);
                    var refreshedToken = await _credentialProvider.AcquireTokenAsync(db, delegation, ct).ConfigureAwait(false);
                    return await SendGetAsync(db, refreshedToken, path, delegation, retryAllowed: false, ct).ConfigureAwait(false);
                }

                throw new DatabricksProviderException(
                    DatabricksErrorCodes.AuthFailed,
                    $"Databricks rejected the credentials (HTTP {status}).");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                _credentialProvider.EvictToken(db, delegation);
                throw new DatabricksProviderException(
                    DatabricksErrorCodes.AuthFailed,
                    $"Databricks rejected the credentials (HTTP {status}).");
            }

            logger?.LogWarning("Databricks query history call failed with HTTP {StatusCode}.", status);
            throw new DatabricksProviderException(
                DatabricksErrorCodes.Http,
                $"Databricks answered with HTTP {status}.");
        }

        var bytes = await ReadBoundedAsync(response, MaxResponseBytes, ct).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new DatabricksProviderException(
                DatabricksErrorCodes.ProtocolViolation,
                "The Databricks query history response is not valid JSON.");
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } length && length > limit)
        {
            throw new DatabricksResultTooLargeException();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new ArrayBufferWriter<byte>(8192);
        while (true)
        {
            var memory = buffer.GetMemory(8192);
            var read = await stream.ReadAsync(memory, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.WrittenMemory;
            }

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
            {
                throw new DatabricksResultTooLargeException();
            }
        }
    }

    private void ParseHistoryResponse(JsonElement root, List<string> requestedIds, List<SourceCostSample> results)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("res", out var resEl) ||
            resEl.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var expected = new HashSet<string>(requestedIds, StringComparer.Ordinal);
        foreach (var item in resEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = TryReadString(item, "statement_id") ?? TryReadString(item, "query_id");
            if (string.IsNullOrWhiteSpace(id) || !expected.Contains(id))
            {
                continue;
            }

            long? computeMs = null;
            decimal? cost = null;
            bool malformed = false;

            JsonElement? metrics = item.TryGetProperty("metrics", out var metricsEl) && metricsEl.ValueKind == JsonValueKind.Object
                ? metricsEl
                : null;

            if (metrics is { } m && m.TryGetProperty("total_time_ms", out var totalTimeEl) && totalTimeEl.ValueKind != JsonValueKind.Null)
            {
                if (!TryReadBoundedDecimal(totalTimeEl, MaxSourceCost, out var rawMs))
                {
                    malformed = true;
                }
                else
                {
                    computeMs = (long)Math.Round(rawMs, MidpointRounding.AwayFromZero);
                }
            }

            if (!malformed && TryReadOptionalCost(item, metrics, out cost) == false)
            {
                malformed = true;
            }

            if (malformed)
            {
                UsageDiagnostics.SourceCostMalformedCounter.Add(1);
                continue;
            }

            if (computeMs.HasValue || cost.HasValue)
            {
                results.Add(new SourceCostSample(id, computeMs, cost, cost.HasValue ? CostUnit : null));
            }
        }
    }

    private static bool TryReadOptionalCost(JsonElement item, JsonElement? metrics, out decimal? cost)
    {
        cost = null;
        string[] propertyNames = ["cost", "dbu", "source_cost"];
        for (int i = 0; i < propertyNames.Length; i++)
        {
            if (metrics is { } m &&
                m.TryGetProperty(propertyNames[i], out var mCostEl) &&
                mCostEl.ValueKind != JsonValueKind.Null)
            {
                if (!TryReadBoundedDecimal(mCostEl, MaxSourceCost, out var parsed))
                {
                    return false;
                }

                cost = parsed;
                return true;
            }

            if (item.TryGetProperty(propertyNames[i], out var itemCostEl) &&
                itemCostEl.ValueKind != JsonValueKind.Null)
            {
                if (!TryReadBoundedDecimal(itemCostEl, MaxSourceCost, out var parsed))
                {
                    return false;
                }

                cost = parsed;
                return true;
            }
        }

        return true;
    }

    private static bool TryReadBoundedDecimal(JsonElement element, decimal maxInclusive, out decimal value)
    {
        value = 0m;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (element.TryGetDecimal(out var dec))
        {
            if (dec < 0m || dec > maxInclusive)
            {
                return false;
            }

            value = dec;
            return true;
        }

        if (element.TryGetDouble(out var dbl) && double.IsFinite(dbl) && dbl >= 0d && dbl <= (double)maxInclusive)
        {
            value = (decimal)dbl;
            return true;
        }

        return false;
    }

    private static string? TryReadString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
