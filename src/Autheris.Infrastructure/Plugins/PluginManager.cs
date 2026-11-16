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
using Autheris.Infrastructure.Modules.Trust;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Plugins;

public sealed class PluginManager : IPluginManager, IDisposable
{
    private static readonly MethodInfo CreateInvokerMethod =
        typeof(PluginManager).GetMethod(nameof(CreateInvoker), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly ILogger<PluginManager> _logger;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ConcurrentDictionary<string, PluginEntry> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PluginActionEntry> _actionHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginListenerEntry> _listeners = [];
    private readonly List<IDisposable> _busSubscriptions = [];
    private readonly object _lock = new();
    private int _inFlightActions;
    private volatile bool _disposed;

    /// <summary>Upper bound for a single plugin action execution.</summary>
    internal static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time <see cref="Dispose"/> waits for in-flight plugin actions.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private sealed record PluginEntry(IHttpDataSourcePlugin Plugin, PluginAssemblyLoadContext? Context);
    private sealed record PluginListenerEntry(IPluginEventListener Listener, PluginAssemblyLoadContext? Context);
    private sealed record PluginActionEntry(IPluginActionHandler Handler, PluginAssemblyLoadContext? Context);
    private sealed record CollectedHttpPlugin(string Name, IHttpDataSourcePlugin Instance);
    private sealed record CollectedActionHandler(string ActionName, IPluginActionHandler Instance);

    private sealed class CollectedPluginAssembly(string dllPath, PluginAssemblyLoadContext context)
    {
        public string DllPath { get; } = dllPath;
        public PluginAssemblyLoadContext Context { get; } = context;
        public List<CollectedHttpPlugin> HttpPlugins { get; } = [];
        public List<IPluginEventListener> EventListeners { get; } = [];
        public List<CollectedActionHandler> ActionHandlers { get; } = [];
        public bool HasAny => HttpPlugins.Count > 0 || EventListeners.Count > 0 || ActionHandlers.Count > 0;

        public void ClearAndUnload()
        {
            HttpPlugins.Clear();
            EventListeners.Clear();
            ActionHandlers.Clear();
            Context.Unload();
        }
    }

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

        Interlocked.Increment(ref _inFlightActions);
        try
        {
            if (_disposed)
            {
                return PluginActionResult.Failed("Plugin manager is shutting down.");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ActionTimeout);
            return await entry.Handler.ExecuteActionAsync(context, cts.Token).WaitAsync(ActionTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("Plugin action '{ActionName}' exceeded the timeout of {Timeout}", actionName, ActionTimeout);
            return PluginActionResult.Failed($"Action '{actionName}' timed out.");
        }
        catch (TimeoutException)
        {
            _logger.LogError("Plugin action '{ActionName}' exceeded the timeout of {Timeout}", actionName, ActionTimeout);
            return PluginActionResult.Failed($"Action '{actionName}' timed out.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unhandled exception executing plugin action '{ActionName}'", actionName);
            return PluginActionResult.Failed($"Action execution failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightActions);
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

        var rawDllFiles = Directory.GetFiles(fullDirectoryPath, "*.dll", SearchOption.AllDirectories);
        var dllFiles = new List<string>(rawDllFiles.Length);
        foreach (var candidateDll in rawDllFiles)
        {
            if (IntegrityHashHelper.ContainsModuleManifestInHierarchy(fullDirectoryPath, candidateDll))
            {
                _logger.LogWarning("Skipping signed module assembly '{DllPath}' during customer plugin discovery.", candidateDll);
                continue;
            }

            dllFiles.Add(candidateDll);
        }

        if (dllFiles.Count == 0)
        {
            return 0;
        }

        dllFiles.Sort(StringComparer.Ordinal);

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

        var collectedAssemblies = new List<CollectedPluginAssembly>(dllFiles.Count);
        try
        {
            foreach (var dllFile in dllFiles)
            {
                var collected = TryCollectAssembly(fullDirectoryPath, dllFile, trustList);
                if (collected != null)
                {
                    collectedAssemblies.Add(collected);
                }
            }
        }
        catch
        {
            UnloadCollectedAssemblies(collectedAssemblies);
            throw;
        }

        var acceptedAssemblies = FilterCollidingAssemblies(collectedAssemblies);
        int loadedCount = RegisterAcceptedAssemblies(acceptedAssemblies);

        _logger.LogInformation("Plugin discovery finished. Total plugin artifacts loaded: {Count}", loadedCount);
        return loadedCount;
    }

    private CollectedPluginAssembly? TryCollectAssembly(string fullDirectoryPath, string dllFile, PluginTrustList? trustList)
    {
        var fullDllPath = Path.GetFullPath(dllFile);
        var relativePath = Path.GetRelativePath(fullDirectoryPath, fullDllPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            _logger.LogWarning("Skipping plugin DLL outside configured directory: '{DllPath}'", dllFile);
            return null;
        }

        PluginAssemblyLoadContext? alc = null;
        try
        {
            // Hash and load the same bytes to prevent TOCTOU between verification and load.
            var verifiedBytes = trustList != null
                ? trustList.ReadVerifiedBytes(fullDllPath)
                : File.ReadAllBytes(fullDllPath);

            alc = new PluginAssemblyLoadContext(fullDllPath, trustList);
            Assembly assembly;
            using (var assemblyStream = new MemoryStream(verifiedBytes, writable: false))
            {
                assembly = alc.LoadFromStream(assemblyStream);
            }

            var candidateTypes = assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface &&
                            (typeof(IHttpDataSourcePlugin).IsAssignableFrom(t) ||
                             typeof(IPluginEventListener).IsAssignableFrom(t) ||
                             typeof(IPluginActionHandler).IsAssignableFrom(t)))
                .ToArray();

            var collected = new CollectedPluginAssembly(dllFile, alc);
            foreach (var type in candidateTypes)
            {
                try
                {
                    var instance = InstantiatePluginType(type);
                    if (instance is IHttpDataSourcePlugin pluginInstance)
                    {
                        var pluginName = pluginInstance.Name;
                        if (string.IsNullOrWhiteSpace(pluginName))
                        {
                            _logger.LogError("Skipping HTTP data source plugin with empty name in {FileName}.", Path.GetFileName(dllFile));
                        }
                        else
                        {
                            collected.HttpPlugins.Add(new CollectedHttpPlugin(pluginName, pluginInstance));
                        }
                    }

                    if (instance is IPluginEventListener listenerInstance)
                    {
                        collected.EventListeners.Add(listenerInstance);
                    }

                    if (instance is IPluginActionHandler actionInstance)
                    {
                        var actionName = actionInstance.ActionName;
                        if (string.IsNullOrWhiteSpace(actionName))
                        {
                            _logger.LogError("Skipping action handler plugin with empty action name in {FileName}.", Path.GetFileName(dllFile));
                        }
                        else
                        {
                            collected.ActionHandlers.Add(new CollectedActionHandler(actionName, actionInstance));
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex is SecurityException || ex.InnerException is SecurityException)
                    {
                        throw ex as SecurityException ?? ex.InnerException!;
                    }

                    _logger.LogError(ex, "Failed to instantiate plugin type '{TypeName}' from {FileName}", type.Name, Path.GetFileName(dllFile));
                }
            }

            if (!collected.HasAny)
            {
                alc.Unload();
                return null;
            }

            return collected;
        }
        catch (SecurityException)
        {
            alc?.Unload();
            throw;
        }
        catch (BadImageFormatException)
        {
            alc?.Unload();
            return null;
        }
        catch (ReflectionTypeLoadException ex)
        {
            alc?.Unload();
            _logger.LogWarning(ex, "Failed to load types from plugin assembly {FileName} ({LoaderErrorCount} loader errors)",
                Path.GetFileName(dllFile), ex.LoaderExceptions.Length);
            return null;
        }
        catch (IOException ex)
        {
            alc?.Unload();
            _logger.LogWarning(ex, "I/O error loading plugin assembly {FileName}", Path.GetFileName(dllFile));
            return null;
        }
        catch (Exception ex)
        {
            alc?.Unload();
            _logger.LogWarning(ex, "Failed to scan or load potential plugin assembly {FileName}", Path.GetFileName(dllFile));
            return null;
        }
    }

    private object? InstantiatePluginType(Type type)
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
                if (ex is SecurityException || ex.InnerException is SecurityException)
                {
                    throw ex as SecurityException ?? ex.InnerException!;
                }

                _logger.LogDebug(ex, "ActivatorUtilities could not instantiate plugin type '{Type}', falling back to default constructor.", type.FullName);
            }
        }

        return instance ?? Activator.CreateInstance(type);
    }

    private List<CollectedPluginAssembly> FilterCollidingAssemblies(List<CollectedPluginAssembly> collectedAssemblies)
    {
        var httpNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var actionNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var collected in collectedAssemblies)
        {
            foreach (var plugin in collected.HttpPlugins)
            {
                httpNameCounts[plugin.Name] = httpNameCounts.GetValueOrDefault(plugin.Name) + 1;
            }

            foreach (var action in collected.ActionHandlers)
            {
                actionNameCounts[action.ActionName] = actionNameCounts.GetValueOrDefault(action.ActionName) + 1;
            }
        }

        var accepted = new List<CollectedPluginAssembly>(collectedAssemblies.Count);
        foreach (var collected in collectedAssemblies)
        {
            var collidingHttpNames = collected.HttpPlugins
                .Where(p => _plugins.ContainsKey(p.Name) || httpNameCounts[p.Name] > 1)
                .Select(p => p.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var collidingActionNames = collected.ActionHandlers
                .Where(a => _actionHandlers.ContainsKey(a.ActionName) || actionNameCounts[a.ActionName] > 1)
                .Select(a => a.ActionName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (collidingHttpNames.Count > 0 || collidingActionNames.Count > 0)
            {
                if (collidingHttpNames.Count > 0)
                {
                    _logger.LogError(
                        "Rejecting plugin assembly due to HTTP plugin name collision ({CollisionCount} colliding names: {PluginNames}; {PluginCount} plugins, {ListenerCount} listeners, {ActionCount} actions rejected).",
                        collidingHttpNames.Count,
                        string.Join(", ", collidingHttpNames),
                        collected.HttpPlugins.Count,
                        collected.EventListeners.Count,
                        collected.ActionHandlers.Count);
                }

                if (collidingActionNames.Count > 0)
                {
                    _logger.LogError(
                        "Rejecting plugin assembly due to action handler name collision ({CollisionCount} colliding names: {ActionNames}; {PluginCount} plugins, {ListenerCount} listeners, {ActionCount} actions rejected).",
                        collidingActionNames.Count,
                        string.Join(", ", collidingActionNames),
                        collected.HttpPlugins.Count,
                        collected.EventListeners.Count,
                        collected.ActionHandlers.Count);
                }

                collected.ClearAndUnload();
                continue;
            }

            accepted.Add(collected);
        }

        return accepted;
    }

    private int RegisterAcceptedAssemblies(List<CollectedPluginAssembly> acceptedAssemblies)
    {
        int loadedCount = 0;

        foreach (var collected in acceptedAssemblies)
        {
            foreach (var plugin in collected.HttpPlugins)
            {
                if (_plugins.TryAdd(plugin.Name, new PluginEntry(plugin.Instance, collected.Context)))
                {
                    _logger.LogInformation("Successfully loaded HTTP data source plugin '{PluginName}' from {DllPath} ({Type})",
                        plugin.Name, collected.DllPath, plugin.Instance.GetType().FullName);
                    loadedCount++;
                }
                else
                {
                    _logger.LogError("Skipping HTTP data source plugin '{PluginName}' due to concurrent registration collision.", plugin.Name);
                }
            }

            foreach (var listener in collected.EventListeners)
            {
                lock (_lock)
                {
                    _listeners.Add(new PluginListenerEntry(listener, collected.Context));
                }

                BindListenerToEventBus(listener);
                _logger.LogInformation("Successfully loaded event listener plugin from {DllPath} ({Type})",
                    collected.DllPath, listener.GetType().FullName);
                loadedCount++;
            }

            foreach (var action in collected.ActionHandlers)
            {
                if (_actionHandlers.TryAdd(action.ActionName, new PluginActionEntry(action.Instance, collected.Context)))
                {
                    _logger.LogInformation("Successfully loaded action handler plugin '{ActionName}' from {DllPath} ({Type})",
                        action.ActionName, collected.DllPath, action.Instance.GetType().FullName);
                    loadedCount++;
                }
                else
                {
                    _logger.LogError("Skipping action handler plugin '{ActionName}' due to concurrent registration collision.", action.ActionName);
                }
            }
        }

        return loadedCount;
    }

    private static void UnloadCollectedAssemblies(IEnumerable<CollectedPluginAssembly> assemblies)
    {
        foreach (var collected in assemblies)
        {
            collected.ClearAndUnload();
        }
    }

    /// <summary>
    /// Builds the typed dispatch delegate once per listener interface. The per-event call is a plain delegate invocation:
    /// no <c>MethodInfo.Invoke</c>, no boxing of arguments and no <see cref="TargetInvocationException"/> wrapping.
    /// </summary>
    private static Func<IGatewayEvent, CancellationToken, Task> CreateInvoker<TEvent>(IPluginEventListener listener)
        where TEvent : IGatewayEvent
    {
        var typed = (IPluginEventListener<TEvent>)listener;
        return (e, ct) => typed.HandleEventAsync((TEvent)e, ct);
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
                var invoker = (Func<IGatewayEvent, CancellationToken, Task>)CreateInvokerMethod
                    .MakeGenericMethod(eventType)
                    .Invoke(null, [listener])!;

                var sub = eventBus.Subscribe(eventType, async (@event, ct) =>
                {
                    Interlocked.Increment(ref _inFlightActions);
                    try
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        await invoker(@event, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Plugin listener '{ListenerType}' failed while handling event {EventType}", listenerType.FullName, @event.EventType);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlightActions);
                    }
                });

                lock (_lock)
                {
                    _busSubscriptions.Add(sub);
                }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;

        // Drain in-flight action handlers (bounded) before unloading their assembly contexts.
        var deadline = DateTime.UtcNow + DrainTimeout;
        while (Volatile.Read(ref _inFlightActions) > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

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

    /// <summary>Shared by <c>AuthPluginHost</c> (EXT-18): flat object, <c>{"plugins": {..}}</c> and <c>{"plugins":[{file,sha256}]}</c>. Parse errors throw (fail closed).</summary>
    internal static Dictionary<string, string> LoadManifest(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return IntegrityHashHelper.ParsePluginManifestJson(json);
    }
}
