using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Autheris.Application.Plugins;

public interface IPluginManager
{
    // HTTP Data Source Plugins
    IReadOnlyCollection<IHttpDataSourcePlugin> GetAllPlugins();
    IHttpDataSourcePlugin? GetPlugin(string name);
    void RegisterPlugin(IHttpDataSourcePlugin plugin);

    // Event Listener Plugins
    IReadOnlyCollection<IPluginEventListener> GetAllListeners();
    void RegisterListener(IPluginEventListener listener);

    // Action Handler Plugins
    IReadOnlyCollection<IPluginActionHandler> GetAllActionHandlers();
    IPluginActionHandler? GetActionHandler(string actionName);
    void RegisterActionHandler(IPluginActionHandler handler);
    Task<PluginActionResult> ExecuteActionAsync(string actionName, PluginActionContext context, CancellationToken ct = default);

    // Loading & Discovery
    int LoadPluginsFromDirectory(string directoryPath);
}
