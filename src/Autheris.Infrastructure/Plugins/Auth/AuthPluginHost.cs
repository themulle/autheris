namespace Autheris.Infrastructure.Plugins.Auth;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Plugins.Auth;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Plugins.Auth;
using Autheris.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class AuthPluginHost : IAuthPluginHost, IDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<AuthPluginHost> _logger;
    private readonly IOptions<GatewayOptions> _options;
    private readonly IServiceProvider? _serviceProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IAuditWriter? _auditWriter;
    private readonly GuardedPluginInvoker _invoker;
    private readonly AuthPluginRoleEscalationGuard _guard;

    private readonly List<IPluginRoleProvider> _roleProviders = [];
    private readonly ConcurrentDictionary<string, IPluginCallerAttributeProvider> _attributeProviders = new(StringComparer.Ordinal);
    private readonly List<IPluginAuthorizationHook> _hooks = [];
    private readonly List<PluginAssemblyLoadContext> _contexts = [];

    private readonly object _lock = new();
    private int _inFlightInvocations;
    private volatile bool _disposed;

    public AuthPluginHost(
        IOptions<GatewayOptions> options,
        ILogger<AuthPluginHost>? logger = null,
        IServiceProvider? serviceProvider = null,
        TimeProvider? timeProvider = null,
        IAuditWriter? auditWriter = null,
        GuardedPluginInvoker? invoker = null,
        AuthPluginRoleEscalationGuard? guard = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthPluginHost>.Instance;
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditWriter = auditWriter;
        _invoker = invoker ?? new GuardedPluginInvoker(_timeProvider, serviceProvider?.GetService<ILogger<GuardedPluginInvoker>>());
        _guard = guard ?? new AuthPluginRoleEscalationGuard(
            _options.Value.Plugins.Auth,
            serviceProvider?.GetService<ILogger<AuthPluginRoleEscalationGuard>>(),
            _auditWriter);
    }

    public bool HasRoleProviders
    {
        get
        {
            lock (_lock)
            {
                return _roleProviders.Count > 0;
            }
        }
    }

    public bool HasCallerAttributeProviders => !_attributeProviders.IsEmpty;

    public bool HasAuthorizationHooks
    {
        get
        {
            lock (_lock)
            {
                return _hooks.Count > 0;
            }
        }
    }

    public void RegisterRoleProvider(IPluginRoleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_lock)
        {
            _roleProviders.Add(provider);
        }
        _logger.LogInformation("Directly registered auth role provider: {Name}", provider.Name);
    }

    public void RegisterCallerAttributeProvider(IPluginCallerAttributeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _attributeProviders[provider.Kind] = provider;
        _logger.LogInformation("Directly registered caller attribute provider: {Kind}", provider.Kind);
    }

    public void RegisterAuthorizationHook(IPluginAuthorizationHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        lock (_lock)
        {
            _hooks.Add(hook);
            _hooks.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        _logger.LogInformation("Directly registered auth hook: {HookName} (Order: {Order})", hook.HookName, hook.Order);
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
            _logger.LogInformation("Auth plugin directory '{Directory}' does not exist. Skipping plugin discovery.", fullDirectoryPath);
            return 0;
        }

        var dllFiles = Directory.GetFiles(fullDirectoryPath, "*.dll", SearchOption.AllDirectories);
        if (dllFiles.Length == 0)
        {
            return 0;
        }

        var env = _serviceProvider?.GetService(typeof(Microsoft.Extensions.Hosting.IHostEnvironment)) as Microsoft.Extensions.Hosting.IHostEnvironment;
        bool isDev = string.Equals(env?.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
        var configuredHashes = _options.Value.Plugins.TrustedPluginHashes;

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
        else if (_options.Value.Plugins.RequireIntegrityManifest || !isDev)
        {
            throw new SecurityException(
                $"Security error: No trusted hashes (Plugins:TrustedPluginHashes) are configured for plugin directory '{fullDirectoryPath}'. manifest.json is not accepted as a trust anchor.");
        }
        else if (manifest != null)
        {
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

            // SEC M-27 / SEC-BL08-01: Hash and load the SAME bytes (zero TOCTOU window)
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
                                (typeof(IPluginRoleProvider).IsAssignableFrom(t) ||
                                 typeof(IPluginCallerAttributeProvider).IsAssignableFrom(t) ||
                                 typeof(IPluginAuthorizationHook).IsAssignableFrom(t)));

                bool contextUsed = false;
                foreach (var type in candidateTypes)
                {
                    try
                    {
                        object? instance = null;
                        if (_serviceProvider != null)
                        {
                            try
                            {
                                instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "ActivatorUtilities could not instantiate auth plugin type '{Type}', falling back to default constructor.", type.FullName);
                            }
                        }

                        instance ??= Activator.CreateInstance(type);

                        if (instance is IPluginRoleProvider roleProvider)
                        {
                            RegisterRoleProvider(roleProvider);
                            contextUsed = true;
                            loadedCount++;
                        }

                        if (instance is IPluginCallerAttributeProvider attrProvider)
                        {
                            RegisterCallerAttributeProvider(attrProvider);
                            contextUsed = true;
                            loadedCount++;
                        }

                        if (instance is IPluginAuthorizationHook authHook)
                        {
                            RegisterAuthorizationHook(authHook);
                            contextUsed = true;
                            loadedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to instantiate auth plugin type '{Type}' from '{DllPath}'.", type.FullName, dllFile);
                        throw;
                    }
                }

                if (contextUsed)
                {
                    lock (_lock)
                    {
                        _contexts.Add(alc);
                    }
                }
            }
            catch (Exception ex) when (ex is not SecurityException)
            {
                _logger.LogError(ex, "Failed to load auth plugin assembly from '{DllPath}'.", dllFile);
                throw;
            }
        }

        return loadedCount;
    }

    public async ValueTask<PluginRoleEnrichmentExecutionResult> EnrichRolesAsync(
        PluginSecurityContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var authOptions = _options.Value.Plugins.Auth;
        if (!authOptions.Enabled || !authOptions.AllowRoleEnrichment)
        {
            return PluginRoleEnrichmentExecutionResult.Success(PluginRoleEnrichmentResult.Empty);
        }

        List<IPluginRoleProvider> providers;
        lock (_lock)
        {
            if (_roleProviders.Count == 0)
            {
                return PluginRoleEnrichmentExecutionResult.Success(PluginRoleEnrichmentResult.Empty);
            }
            providers = [.. _roleProviders];
        }

        var timeout = TimeSpan.FromMilliseconds(authOptions.ExecutionTimeoutMilliseconds);
        var allAddedRoles = new List<string>();
        var allAddedSids = new List<Sid>();

        Interlocked.Increment(ref _inFlightInvocations);
        try
        {
            foreach (var provider in providers)
            {
                var execResult = await _invoker.InvokeAsync<(bool Success, PluginRoleEnrichmentResult? Result, string? ErrorCode, string? ErrorMessage)>(
                    provider.Name,
                    "role_enrichment",
                    timeout,
                    async token =>
                    {
                        var res = await provider.EnrichRolesAsync(context, token).ConfigureAwait(false);
                        return (true, res, null, null);
                    },
                    (code, msg) => (false, null, code, msg),
                    ct,
                    context.TenantId.Value).ConfigureAwait(false);

                if (!execResult.Success)
                {
                    // SEC-BL08-04: Multi-provider fail-closed
                    return PluginRoleEnrichmentExecutionResult.Failed(execResult.ErrorCode ?? "AUTH_PLUGIN_FAILED", execResult.ErrorMessage);
                }

                var sanitized = _guard.Sanitize(provider.Name, execResult.Result ?? PluginRoleEnrichmentResult.Empty, context);
                allAddedRoles.AddRange(sanitized.AddedRoles);
                allAddedSids.AddRange(sanitized.AddedGroupSids);
            }

            var combined = new PluginRoleEnrichmentResult(allAddedRoles, allAddedSids);
            var finalSanitized = _guard.Sanitize("CombinedRoleEnrichment", combined, context);
            return PluginRoleEnrichmentExecutionResult.Success(finalSanitized);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightInvocations);
        }
    }

    public bool TryGetCallerAttributeProvider(string kind, out IPluginCallerAttributeProvider? provider)
    {
        var authOptions = _options.Value.Plugins.Auth;
        if (!authOptions.Enabled)
        {
            provider = null;
            return false;
        }

        return _attributeProviders.TryGetValue(kind, out provider);
    }

    public async ValueTask<PluginAuthorizationResult> EvaluateAuthorizationHooksAsync(
        PluginAuthorizationContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var authOptions = _options.Value.Plugins.Auth;
        if (!authOptions.Enabled || !authOptions.AllowAuthorizationHooks)
        {
            return PluginAuthorizationResult.Allow();
        }

        List<IPluginAuthorizationHook> hooks;
        lock (_lock)
        {
            if (_hooks.Count == 0)
            {
                return PluginAuthorizationResult.Allow();
            }
            hooks = [.. _hooks];
        }

        var timeout = TimeSpan.FromMilliseconds(authOptions.ExecutionTimeoutMilliseconds);
        var deniedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? combinedRowFilterSql = null;

        Interlocked.Increment(ref _inFlightInvocations);
        try
        {
            foreach (var hook in hooks.OrderBy(h => h.Order))
            {
                var hookResult = await _invoker.InvokeAsync<PluginAuthorizationResult>(
                    hook.HookName,
                    "authorization_hook",
                    timeout,
                    async token => await hook.EvaluateAccessAsync(context, token).ConfigureAwait(false),
                    (code, msg) => PluginAuthorizationResult.Deny(code, msg, hook.HookName),
                    ct,
                    context.TenantId.Value).ConfigureAwait(false);

                if (hookResult.Outcome == PluginAuthorizationOutcome.Deny)
                {
                    // SEC-BL08-04: Short circuit on first Deny
                    RecordHookDecisionAudit(context, hook.HookName, "DENY", hookResult.ReasonCode, hookResult.ClientMessage);
                    return hookResult;
                }

                if (hookResult.Outcome == PluginAuthorizationOutcome.Restrict)
                {
                    RecordHookDecisionAudit(context, hook.HookName, "RESTRICT", hookResult.ReasonCode, hookResult.ClientMessage);
                    if (hookResult.DeniedColumns != null)
                    {
                        foreach (var col in hookResult.DeniedColumns)
                        {
                            deniedCols.Add(col);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(hookResult.AdditionalRowFilterSql))
                    {
                        combinedRowFilterSql = string.IsNullOrWhiteSpace(combinedRowFilterSql)
                            ? hookResult.AdditionalRowFilterSql
                            : $"({combinedRowFilterSql}) AND ({hookResult.AdditionalRowFilterSql})";
                    }
                }
            }

            if (deniedCols.Count > 0 || !string.IsNullOrWhiteSpace(combinedRowFilterSql))
            {
                return PluginAuthorizationResult.Restrict(deniedCols, combinedRowFilterSql);
            }

            return PluginAuthorizationResult.Allow();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightInvocations);
        }
    }

    private void RecordHookDecisionAudit(PluginAuthorizationContext context, string hookName, string decision, string? reasonCode, string? clientMessage)
    {
        if (_auditWriter == null) return;
        try
        {
            var entry = new AuditLogEntry
            {
                EventType = AuthPluginAuditEventTypes.AuthPluginDecision,
                ActorSid = context.UserSid,
                TenantId = context.TenantId,
                TargetTable = context.TableIdentifier.ToString(),
                Decision = decision,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    hook = hookName,
                    outcome = decision,
                    reasonCode = reasonCode,
                    message = clientMessage
                })
            };
            _ = _auditWriter.RecordBestEffortAsync(entry);
        }
        catch
        {
        }
    }

    private static Dictionary<string, string>? LoadManifest(string manifestPath)
    {
        try
        {
            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var start = _timeProvider.GetTimestamp();
        while (_inFlightInvocations > 0 && _timeProvider.GetElapsedTime(start) < DrainTimeout)
        {
            Thread.Sleep(20);
        }

        lock (_lock)
        {
            foreach (var alc in _contexts)
            {
                try
                {
                    alc.Unload();
                }
                catch
                {
                }
            }
            _contexts.Clear();
            _roleProviders.Clear();
            _attributeProviders.Clear();
            _hooks.Clear();
        }
    }
}
