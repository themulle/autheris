using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Plugins;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Plugins;

public sealed class PluginHttpDataSourceExecutor : IDataSourceExecutor
{
    private readonly IPluginManager _pluginManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PluginHttpDataSourceExecutor> _logger;

    public DataSourceType SupportedType => DataSourceType.HttpPlugin;

    public PluginHttpDataSourceExecutor(
        IPluginManager pluginManager,
        IHttpClientFactory httpClientFactory,
        ILogger<PluginHttpDataSourceExecutor> logger)
    {
        _pluginManager = pluginManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        var pluginName = context.Metadata.PluginName;
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            pluginName = context.Metadata.Table.SourceName;
        }

        if (string.IsNullOrWhiteSpace(pluginName))
        {
            throw new InvalidOperationException(
                $"Table '{context.Metadata.Identifier}' is configured as HttpPlugin but specifies neither PluginName nor SourceName.");
        }

        var plugin = _pluginManager.GetPlugin(pluginName);
        if (plugin == null)
        {
            throw new InvalidOperationException(
                $"The requested HTTP plugin '{pluginName}' for table '{context.Metadata.Identifier}' was not found or is not loaded.");
        }

        _logger.LogInformation("Routing query for table '{Table}' to HTTP DataSource Plugin '{PluginName}'",
            context.Metadata.Identifier, pluginName);

        var pluginContext = new PluginExecutionContext(
            OperationName: context.Metadata.Identifier.ToString(),
            Metadata: context.Metadata,
            Principal: context.Principal,
            Arguments: context.Arguments,
            HttpClientFactory: new SsrfProtectedHttpClientFactory(_httpClientFactory),
            RequestHeaders: context.RequestHeaders
        );

        return await plugin.ExecuteAsync(pluginContext, ct);
    }

    private sealed class SsrfProtectedHttpClientFactory : IHttpClientFactory
    {
        private readonly IHttpClientFactory _inner;

        public SsrfProtectedHttpClientFactory(IHttpClientFactory inner)
        {
            _inner = inner;
        }

        public HttpClient CreateClient(string name)
        {
            // Route all outbound plugin HTTP requests through the SSRF-hardened client with SocketsHttpHandler ConnectCallback
            return _inner.CreateClient(Autheris.Application.Services.DeclarativeHttpDataSourceExecutor.HttpClientName);
        }
    }
}
