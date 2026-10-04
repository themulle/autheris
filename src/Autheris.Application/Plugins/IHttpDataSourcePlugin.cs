using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Autheris.Application.Plugins;

public interface IHttpDataSourcePlugin
{
    /// <summary>Eindeutiger Bezeichner des Plugins (wird in Table.PluginName oder Table.SourceName referenziert)</summary>
    string Name { get; }

    /// <summary>Registriert pluginspezifische Abhängigkeiten und typisierte HttpClients</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Führt den Aufruf aus und liefert die Rohdaten als Dictionaries zurück.
    /// Spaltenmaskierung, RLS-Post-Filtering und Audit-Logs werden anschließend zentral vom Gateway erzwungen!
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        PluginExecutionContext context,
        CancellationToken ct = default);
}

public sealed record PluginExecutionContext(
    string OperationName,
    TableMetadata Metadata,
    ClaimsPrincipal Principal,
    IReadOnlyDictionary<string, object?> Arguments,
    IHttpClientFactory HttpClientFactory,
    IReadOnlyDictionary<string, string[]>? RequestHeaders = null
);
