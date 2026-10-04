using System.Collections.Generic;

namespace Autheris.Application.Plugins;

public interface IPluginManager
{
    IReadOnlyCollection<IHttpDataSourcePlugin> GetAllPlugins();
    IHttpDataSourcePlugin? GetPlugin(string name);
    void RegisterPlugin(IHttpDataSourcePlugin plugin);
    int LoadPluginsFromDirectory(string directoryPath);
}
