namespace Autheris.Infrastructure.Cdn;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class FastlyCdnPurgeService : ICdnCachePurgeService
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<FastlyCdnPurgeService> _logger;

    public FastlyCdnPurgeService(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<FastlyCdnPurgeService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var fastlyOptions = options.Value.Caching.Cdn.Fastly;
        if (!string.IsNullOrWhiteSpace(fastlyOptions.BaseUrl) && _httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(fastlyOptions.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<PurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        if (tags == null || tags.Count == 0)
        {
            return new PurgeResult(true, 0);
        }

        var fastlyOptions = _options.Value.Caching.Cdn.Fastly;
        if (string.IsNullOrWhiteSpace(fastlyOptions.ApiKey) || string.IsNullOrWhiteSpace(fastlyOptions.ServiceId))
        {
            _logger.LogInformation("Fastly CDN purge unconfigured (ApiKey or ServiceId missing). Mocking successful purge for {Count} tags.", tags.Count);
            return new PurgeResult(true, tags.Count);
        }

        int successCount = 0;
        foreach (var tag in tags)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"service/{fastlyOptions.ServiceId}/purge/{Uri.EscapeDataString(tag)}");
                request.Headers.Add("Fastly-Key", fastlyOptions.ApiKey);

                var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    successCount++;
                }
                else
                {
                    _logger.LogWarning("Fastly surrogate key purge failed for tag {Tag} with status {StatusCode}", tag, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while purging surrogate key {Tag} on Fastly.", tag);
            }
        }

        return new PurgeResult(successCount > 0, successCount);
    }
}
