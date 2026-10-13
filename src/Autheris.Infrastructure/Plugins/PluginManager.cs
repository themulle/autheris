using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Application.Plugins;
using Autheris.Domain.Events;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Plugins;

public sealed class PluginManager : IPluginManager, IDisposable
{
    private readonly ILogger<PluginManager> _logger;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ConcurrentDictionary<string, PluginEntry> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PluginActionEntry> _actionHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginListenerEntry> _listeners = [];
    private readonly List<IDisposable> _busSubscriptions = [];
    private readonly object _lock = new();

    private sealed record PluginEntry(IHttpDataSourcePlugin Plugin, PluginAssemblyLoadContext? Context);
    private sealed record PluginListenerEntry(IPluginEventListener Listener, PluginAssemblyLoadContext? Context);
    private sealed record PluginActionEntry(IPluginActionHandler Handler, PluginAssemblyLoadContext? Context);

    public PluginManager(
        ILogger<PluginManager> logger,
        IServiceProvider? serviceProvider = null,
        IOptions<GatewayOptions>? options = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _options = options;
    }

    public IReadOnlyCollection<IHttpDataSourcePlugin> GetAllPlugins() =>
        _plugins.Values.Select(e => e.Plugin).ToList();

    public IHttpDataSourcePlugin? GetPlugin(string name)
    {
        return _plugins.TryGetValue(name, out var entry) ? entry.Plugin : null;
    }

    public void RegisterPlugin(IHttpDataSourcePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _plugins[plugin.Name] = new PluginEntry(plugin, null);
        _logger.LogInformation("Directly registered HTTP Data Source Plugin: {PluginName}", plugin.Name);
    }

    public IReadOnlyCollection<IPluginEventListener> GetAllListeners()
    {
        lock (_lock)
        {
            return _listeners.Select(e => e.Listener).ToList();
        }
    }

    public void RegisterListener(IPluginEventListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_lock)
        {
            _listeners.Add(new PluginListenerEntry(listener, null));
        }
        BindListenerToEventBus(listener);
        _logger.LogInformation("Directly registered plugin event listener: {ListenerType}", listener.GetType().FullName);
    }

    public IReadOnlyCollection<IPluginActionHandler> GetAllActionHandlers() =>
        _actionHandlers.Values.Select(e => e.Handler).ToList();

    public IPluginActionHandler? GetActionHandler(string actionName)
    {
        return _actionHandlers.TryGetValue(actionName, out var entry) ? entry.Handler : null;
    }

    public void RegisterActionHandler(IPluginActionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _actionHandlers[handler.ActionName] = new PluginActionEntry(handler, null);
        _logger.LogInformation("Directly registered plugin action handler: {ActionName} ({HandlerType})", handler.ActionName, handler.GetType().FullName);
    }

    public async Task<PluginActionResult> ExecuteActionAsync(string actionName, PluginActionContext context, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(context);

        if (!_actionHandlers.TryGetValue(actionName, out var entry))
        {
            _logger.LogWarning("Plugin action handler not found for action '{ActionName}'", actionName);
            return PluginActionResult.Failed($"Action handler '{actionName}' not found.");
        }

        try
        {
            return await entry.Handler.ExecuteActionAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception executing plugin action '{ActionName}'", actionName);
            return PluginActionResult.Failed($"Action execution failed: {ex.Message}");
        }
    }

    public int LoadPluginsFromDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return 0;
        }

        var fullDirectoryPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDirectoryPath))
        {
            _logger.LogInformation("Plugin directory '{Directory}' does not exist or is empty. Skipping plugin discovery.", fullDirectoryPath);
            return 0;
        }

        var dllFiles = Directory.GetFiles(fullDirectoryPath, "*.dll", SearchOption.AllDirectories);
        if (dllFiles.Length == 0)
        {
            return 0;
        }

        // SEC M-27: the trust anchor are the SHA-256 hashes in configuration (Plugins:TrustedPluginHashes,
        // ideally sourced from Key Vault / a read-only config map). manifest.json next to the DLLs is writable by
        // the same party that can drop DLLs and is therefore only accepted as an additional, consistent statement.
        var env = _serviceProvider?.GetService(typeof(Microsoft.Extensions.Hosting.IHostEnvironment)) as Microsoft.Extensions.Hosting.IHostEnvironment;
        bool isDev = string.Equals(env?.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
        var configuredHashes = _options?.Value.Plugins.TrustedPluginHashes;

        var manifestPath = Path.Combine(fullDirectoryPath, "manifest.json");
        Dictionary<string, string>? manifest = File.Exists(manifestPath) ? LoadManifest(manifestPath) : null;

        PluginTrustList? trustList = null;
        if (configuredHashes != null && configuredHashes.Count > 0)
        {
            trustList = new PluginTrustList(fullDirectoryPath, configuredHashes);
            if (manifest != null)
            {
                trustList.EnsureManifestConsistent(manifest);
            }
        }
        else if (_options?.Value.Plugins.RequireIntegrityManifest == true || !isDev)
        {
            throw new SecurityException(
                $"Security error: No trusted hashes (Plugins:TrustedPluginHashes) are configured for plugin directory '{fullDirectoryPath}'. manifest.json is not accepted as a trust anchor.");
        }
        else if (manifest != null)
        {
            // Development convenience only: verify against the local manifest when no configuration exists.
            _logger.LogWarning("Plugins:TrustedPluginHashes is empty; verifying plugins against the local manifest.json (Development only).");
            trustList = new PluginTrustList(fullDirectoryPath, manifest);
        }
        else
        {
            _logger.LogWarning("Loading plugins from '{Directory}' WITHOUT integrity verification (Development only).", fullDirectoryPath);
        }

        int loadedCount = 0;

        foreach (var dllFile in dllFiles)
        {
            var fullDllPath = Path.GetFullPath(dllFile);
            var relativePath = Path.GetRelativePath(fullDirectoryPath, fullDllPath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            {
                _logger.LogWarning("Skipping plugin DLL outside configured directory: '{DllPath}'", dllFile);
                continue;
            }

            // SEC M-27: hash and load the SAME bytes (no TOCTOU window between verification and load).
            var verifiedBytes = trustList != null
                ? trustList.ReadVerifiedBytes(fullDllPath)
                : File.ReadAllBytes(fullDllPath);

            try
            {
                var alc = new PluginAssemblyLoadContext(fullDllPath, trustList);
                Assembly assembly;
                using (var assemblyStream = new MemoryStream(verifiedBytes, writable: false))
                {
                    assembly = alc.LoadFromStream(assemblyStream);
                }

                var candidateTypes = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface &&
                                (typeof(IHttpDataSourcePlugin).IsAssignableFrom(t) ||
                                 typeof(IPluginEventListener).IsAssignableFrom(t) ||
                                 typeof(IPluginActionHandler).IsAssignableFrom(t)));

                foreach (var type in candidateTypes)
                {
                    try
                    {
                        object? instance = null;
                        if (_serviceProvider != null)
                        {
                            try
                            {
                                instance = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance(_serviceProvider, type);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "ActivatorUtilities could not instantiate plugin type '{Type}', falling back to default constructor.", type.FullName);
                            }
                        }

                        instance ??= Activator.CreateInstance(type);

                        if (instance is IHttpDataSourcePlugin pluginInstance)
                        {
                            _plugins[pluginInstance.Name] = new PluginEntry(pluginInstance, alc);
                            _logger.LogInformation("Successfully loaded HTTP data source plugin '{PluginName}' from {DllPath} ({Type})",
                                pluginInstance.Name, dllFile, type.FullName);
                            loadedCount++;
                        }

                        if (instance is IPluginEventListener listenerInstance)
                        {
                            lock (_lock)
                            {
                                _listeners.Add(new PluginListenerEntry(listenerInstance, alc));
                            }
                            BindListenerToEventBus(listenerInstance);
                            _logger.LogInformation("Successfully loaded event listener plugin from {DllPath} ({Type})",
                                dllFile, type.FullName);
                            loadedCount++;
                        }

                        if (instance is IPluginActionHandler actionInstance)
                        {
                            _actionHandlers[actionInstance.ActionName] = new PluginActionEntry(actionInstance, alc);
                            _logger.LogInformation("Successfully loaded action handler plugin '{ActionName}' from {DllPath} ({Type})",
                                actionInstance.ActionName, dllFile, type.FullName);
                            loadedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to instantiate plugin type '{Type}' from {DllPath}", type.FullName, dllFile);
                    }
                }
            }
            catch (SecurityException)
            {
                throw;
            }
            catch (BadImageFormatException)
            {
                // Not a .NET assembly, skip
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to scan or load potential plugin assembly from {DllPath}", dllFile);
            }
        }

        _logger.LogInformation("Plugin discovery finished. Total plugin artifacts loaded: {Count}", loadedCount);
        return loadedCount;
    }

    private void BindListenerToEventBus(IPluginEventListener listener)
    {
        var eventBus = _serviceProvider?.GetService(typeof(IGatewayEventBus)) as IGatewayEventBus;
        if (eventBus == null)
        {
            return;
        }

        var listenerType = listener.GetType();
        foreach (var iface in listenerType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IPluginEventListener<>))
            {
                var eventType = iface.GetGenericArguments()[0];
                var handleMethod = iface.GetMethod(nameof(IPluginEventListener<IGatewayEvent>.HandleEventAsync));
                if (handleMethod != null)
                {
                    var sub = eventBus.Subscribe(eventType, async (@event, ct) =>
                    {
                        try
                        {
                            var task = (Task)handleMethod.Invoke(listener, [@event, ct])!;
                            await task.ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Plugin listener '{ListenerType}' failed while handling event {EventType}", listenerType.FullName, @event.EventType);
                        }
                    });

                    lock (_lock)
                    {
                        _busSubscriptions.Add(sub);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var sub in _busSubscriptions)
            {
                sub.Dispose();
            }
            _busSubscriptions.Clear();
        }

        foreach (var entry in _plugins.Values)
        {
            entry.Context?.Unload();
        }
        _plugins.Clear();

        lock (_lock)
        {
            foreach (var entry in _listeners)
            {
                entry.Context?.Unload();
            }
            _listeners.Clear();
        }

        foreach (var entry in _actionHandlers.Values)
        {
            entry.Context?.Unload();
        }
        _actionHandlers.Clear();
    }

    private static Dictionary<string, string> LoadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (doc.RootElement.TryGetProperty("plugins", out var pluginsElem))
            {
                if (pluginsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in pluginsElem.EnumerateArray())
                    {
                        if (item.TryGetProperty("file", out var fileProp) && item.TryGetProperty("sha256", out var hashProp))
                        {
                            result[fileProp.GetString()!] = hashProp.GetString()!;
                        }
                    }
                }
                else if (pluginsElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in pluginsElem.EnumerateObject())
                    {
                        result[prop.Name] = prop.Value.GetString()!;
                    }
                }
            }
            else
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    result[prop.Name] = prop.Value.GetString()!;
                }
            }
        }

        return result;
    }
}
