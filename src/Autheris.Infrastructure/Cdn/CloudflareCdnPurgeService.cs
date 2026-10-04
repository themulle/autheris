namespace Autheris.Infrastructure.Cdn;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class CloudflareCdnPurgeService : ICdnCachePurgeService
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<CloudflareCdnPurgeService> _logger;

    public CloudflareCdnPurgeService(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<CloudflareCdnPurgeService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var cfOptions = options.Value.Caching.Cdn.Cloudflare;
        if (!string.IsNullOrWhiteSpace(cfOptions.BaseUrl) && _httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(cfOptions.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<PurgeResult> PurgeTagsAsync(IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        if (tags == null || tags.Count == 0)
        {
            return new PurgeResult(true, 0);
        }

        var cfOptions = _options.Value.Caching.Cdn.Cloudflare;
        if (string.IsNullOrWhiteSpace(cfOptions.ApiToken) || string.IsNullOrWhiteSpace(cfOptions.ZoneId))
        {
            _logger.LogInformation("Cloudflare CDN purge unconfigured (ApiToken or ZoneId missing). Mocking successful purge for {Count} tags.", tags.Count);
            return new PurgeResult(true, tags.Count);
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"zones/{cfOptions.ZoneId}/purge_cache")
            {
                Content = JsonContent.Create(new { tags })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfOptions.ApiToken);

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully purged {Count} cache tags via Cloudflare API.", tags.Count);
                return new PurgeResult(true, tags.Count);
            }

            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("Cloudflare cache purge failed with status {StatusCode}: {Error}", response.StatusCode, errorBody);
            return new PurgeResult(false, 0, $"Cloudflare HTTP {response.StatusCode}: {errorBody}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending cache purge to Cloudflare.");
            return new PurgeResult(false, 0, ex.Message);
        }
    }
}
