namespace Autheris.Extensions.Lineage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Interfaces;

/// <summary>
/// OpenLineage 1.x/2.x and Apache Atlas compatible lineage client.
/// Pushes Autheris dataset dependencies, column lineage, and downstream consumer graphs
/// to OpenLineage backends (e.g. Marquez, Apache Atlas, Collibra Lineage).
/// </summary>
public sealed class OpenLineageClient : IOpenLineageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _httpClient;
    private readonly ILineageGraphStore _graphStore;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<OpenLineageClient> _logger;


    public OpenLineageClient(
        HttpClient httpClient,
        ILineageGraphStore graphStore,
        IOptions<GatewayOptions> options,
        ILogger<OpenLineageClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PushLineageGraphAsync(TenantId tenant, CancellationToken ct = default)
    {
        var openLineageEndpoint = _options.Value.Catalog.OpenLineageEndpoint;
        if (string.IsNullOrWhiteSpace(openLineageEndpoint))
        {
            _logger.LogWarning("OpenLineage endpoint is not configured; skipping graph push.");
            return false;
        }

        var inputs = new List<OpenLineageDataset>();
        var outputs = new List<OpenLineageDataset>();
        var rootNamespace = tenant.Value;

        // Build dataset list from active LineageGraphStore
        foreach (var node in _graphStore.GetAllNodes())
        {
            var dataset = new OpenLineageDataset(rootNamespace, node.Id, new Dictionary<string, object>
            {
                ["type"] = node.Type.ToString(),
                ["name"] = node.Name
            });

            if (node.DownstreamNodeIds.Count > 0)
            {
                inputs.Add(dataset);
            }
            else
            {
                outputs.Add(dataset);
            }
        }

        var runEvent = new OpenLineageRunEvent(
            EventType: "COMPLETE",
            EventTime: DateTimeOffset.UtcNow,
            Producer: "https://github.com/themulle/gql/autheris",
            SchemaUrl: "https://openlineage.io/spec/1-0-2/OpenLineage.json",
            Job: new
            {
                @namespace = rootNamespace,
                name = "autheris.governance_sync",
                facets = new
                {
                    jobType = new { jobType = "GATEWAY_ROUTING", integration = "Autheris" }
                }
            },
            Inputs: inputs,
            Outputs: outputs
        );

        return await PushLineageEventAsync(runEvent, ct).ConfigureAwait(false);
    }

    public async Task<bool> PushLineageEventAsync(OpenLineageRunEvent runEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        var endpoint = _options.Value.Catalog.OpenLineageEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = "http://localhost:5000/api/v1/lineage";
        }

        var json = JsonSerializer.Serialize(runEvent, JsonOptions);


        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var apiKey = _options.Value.Catalog.OpenLineageApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await BoundedHttpContent.ReadBoundedStringAsync(response, "OpenLineage", ct: ct).ConfigureAwait(false);
                _logger.LogWarning("OpenLineage server responded with HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
                return false;
            }

            _logger.LogInformation("Successfully pushed OpenLineage event ({EventType}) to {Endpoint}.", runEvent.EventType, endpoint);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to push lineage event to OpenLineage backend: {Message}", ex.Message);
            return false;
        }
    }
}
