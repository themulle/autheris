namespace Autheris.Extensions.Lineage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Lineage;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// OpenLineage 1.x/2.x and Apache Atlas compatible lineage client.
/// Pushes Autheris dataset dependencies, column lineage, and downstream consumer graphs
/// to OpenLineage backends (e.g. Marquez, Apache Atlas).
/// </summary>
public sealed class OpenLineageClient : IOpenLineageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _httpClient;
    private readonly ILineageGraphStore _graphStore;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<OpenLineageClient> _logger;
    private readonly bool _isDevelopment;
    private readonly bool _hasConfiguredEndpoint;
    private readonly Uri? _endpoint;

    public OpenLineageClient(
        HttpClient httpClient,
        ILineageGraphStore graphStore,
        IOptions<GatewayOptions> options,
        ILogger<OpenLineageClient> logger,
        IHostEnvironment? environment = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _isDevelopment = environment?.IsDevelopment() == true;

        var rawEndpoint = _options.Value.Catalog.OpenLineageEndpoint;
        _hasConfiguredEndpoint = !string.IsNullOrWhiteSpace(rawEndpoint);
        _endpoint = ValidateAndResolveEndpoint(rawEndpoint, _options.Value.Catalog.OpenLineageApiKey, _isDevelopment, _logger);
    }

    public async Task<bool> PushLineageGraphAsync(TenantId tenant, CancellationToken ct = default)
    {
        if (_endpoint is null)
        {
            if (!_hasConfiguredEndpoint)
            {
                _logger.LogWarning("OpenLineage endpoint is not configured; skipping graph push.");
            }

            return false;
        }

        var inputs = new List<OpenLineageDataset>();
        var outputs = new List<OpenLineageDataset>();
        var rootNamespace = tenant.Value;

        // SEC SR-L-19: only this tenant's nodes are pushed. Nodes without a tenant are legacy (single-tenant) data and are
        // pushed only for the default tenant (fail-closed; none without a default tenant).
        var defaultTenant = Autheris.Application.Tenancy.TenantResolver.EffectiveDefaultOf(_options.Value.Tenancy);
        var tenantNodes = _graphStore.GetAllNodes().Where(node => BelongsToTenant(node, tenant, defaultTenant)).ToList();
        var tenantNodeIds = new HashSet<string>(tenantNodes.Select(n => n.Id), StringComparer.Ordinal);

        foreach (var node in tenantNodes)
        {
            var dataset = new OpenLineageDataset(rootNamespace, node.Id, new Dictionary<string, object>
            {
                ["type"] = node.Type.ToString(),
                ["name"] = node.Name
            });

            // Downstream references to nodes of other tenants are dropped.
            if (node.DownstreamNodeIds.Any(tenantNodeIds.Contains))
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

    // DT-A-7: a node without tenant belongs to the default tenant (when one is configured), in every deployment shape.
    private static bool BelongsToTenant(LineageNode node, TenantId tenant, TenantId? defaultTenant)
        => string.IsNullOrWhiteSpace(node.TenantId)
            ? Autheris.Application.Catalog.Services.CallerScopeResolver.IsOwner(tenant, defaultTenant)
            : string.Equals(node.TenantId, tenant.Value, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> PushLineageEventAsync(OpenLineageRunEvent runEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        if (_endpoint is null)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(runEvent, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var apiKey = _options.Value.Catalog.OpenLineageApiKey;
        var canSendBearer = string.Equals(_endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (_isDevelopment && IsLoopbackHost(_endpoint));
        if (!string.IsNullOrWhiteSpace(apiKey) && canSendBearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenLineage server at {Host} responded with HTTP {StatusCode}.", _endpoint.IdnHost, (int)response.StatusCode);
                return false;
            }

            _logger.LogInformation("Successfully pushed OpenLineage event ({EventType}) to {Host} with HTTP {StatusCode}.", runEvent.EventType, _endpoint.IdnHost, (int)response.StatusCode);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to push lineage event to OpenLineage backend {Host}: {ExceptionType}.", _endpoint.IdnHost, ex.GetType().Name);
            return false;
        }
    }

    private static Uri? ValidateAndResolveEndpoint(
        string? rawEndpoint,
        string? apiKey,
        bool isDevelopment,
        ILogger<OpenLineageClient> logger)
    {
        if (string.IsNullOrWhiteSpace(rawEndpoint))
        {
            return null;
        }

        var trimmed = rawEndpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            logger.LogError("OpenLineage endpoint configuration (Gateway:Catalog:OpenLineageEndpoint) is not a valid absolute URI; OpenLineage export is disabled.");
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogError("OpenLineage endpoint configuration (Gateway:Catalog:OpenLineageEndpoint) has unsupported scheme '{Scheme}'; OpenLineage export is disabled.", uri.Scheme);
            return null;
        }

        if (HasEmbeddedUserInfo(trimmed, uri))
        {
            logger.LogError("OpenLineage endpoint configuration (Gateway:Catalog:OpenLineageEndpoint) must not contain embedded userinfo credentials; OpenLineage export is disabled.");
            return null;
        }

        if (!isDevelopment)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("OpenLineage endpoint for host {Host} must use HTTPS outside Development; OpenLineage export is disabled.", uri.IdnHost);
                return null;
            }

            try
            {
                EgressUrlPolicy.ValidateStatic(uri, isDev: false, enforceHttps: true);
            }
            catch (SecurityException)
            {
                logger.LogError("OpenLineage endpoint for host {Host} failed outbound egress validation; OpenLineage export is disabled.", uri.IdnHost);
                return null;
            }
        }
        else
        {
            if (!IsLoopbackHost(uri))
            {
                try
                {
                    EgressUrlPolicy.ValidateStatic(uri, isDev: true, enforceHttps: false);
                }
                catch (SecurityException)
                {
                    logger.LogError("OpenLineage endpoint for host {Host} failed outbound egress validation; OpenLineage export is disabled.", uri.IdnHost);
                    return null;
                }
            }

            if (!string.IsNullOrWhiteSpace(apiKey) &&
                string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !IsLoopbackHost(uri))
            {
                logger.LogWarning("OpenLineage API key is configured for non-loopback HTTP host {Host} and will not be transmitted over plaintext HTTP.", uri.IdnHost);
            }
        }

        return uri;
    }

    private static bool HasEmbeddedUserInfo(string trimmedEndpoint, Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return true;
        }

        int schemeSep = trimmedEndpoint.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep < 0)
        {
            return false;
        }

        var afterScheme = trimmedEndpoint.AsSpan(schemeSep + 3);
        int endOfAuthority = afterScheme.IndexOfAny('/', '?', '#');
        var authority = endOfAuthority >= 0 ? afterScheme[..endOfAuthority] : afterScheme;
        return authority.Contains('@');
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var literal = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(literal, out var ip) && IPAddress.IsLoopback(ip);
    }
}
