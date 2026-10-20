namespace Autheris.Infrastructure.Plugins.Auth;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Plugins.Auth;
using Microsoft.Extensions.Logging;

public sealed class GuardedPluginInvoker
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GuardedPluginInvoker> _logger;

    public GuardedPluginInvoker(
        TimeProvider? timeProvider = null,
        ILogger<GuardedPluginInvoker>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GuardedPluginInvoker>.Instance;
    }

    public async ValueTask<TResult> InvokeAsync<TResult>(
        string pluginName,
        string extensionPoint,
        TimeSpan timeout,
        Func<CancellationToken, ValueTask<TResult>> invocation,
        Func<string, string, TResult> failureFactory,
        CancellationToken callerCt,
        string? tenant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionPoint);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(failureFactory);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerCt);
        linkedCts.CancelAfter(timeout);

        using var activity = AuthPluginDiagnostics.ActivitySource.StartActivity($"auth.plugin.{extensionPoint}");
        activity?.SetTag("plugin.name", pluginName);
        activity?.SetTag("plugin.extension_point", extensionPoint);
        if (!string.IsNullOrEmpty(tenant))
        {
            activity?.SetTag("tenant", tenant);
        }

        long startTimestamp = _timeProvider.GetTimestamp();
        string outcome = "success";

        try
        {
            // SEC-BL08-03: Asynchronous timeout protection with guaranteed pipeline unblocking
            var task = invocation(linkedCts.Token).AsTask();
            var result = await task.WaitAsync(timeout, _timeProvider, callerCt).ConfigureAwait(false);
            return result;
        }
        catch (TimeoutException)
        {
            outcome = "timeout";
            _logger.LogError("Auth plugin '{PluginName}' ({ExtensionPoint}) exceeded execution timeout of {Timeout}ms (Fail-Closed).",
                pluginName, extensionPoint, timeout.TotalMilliseconds);
            return failureFactory("AUTH_PLUGIN_TIMEOUT", $"Plugin execution exceeded timeout of {timeout.TotalMilliseconds}ms.");
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            outcome = "timeout";
            _logger.LogError("Auth plugin '{PluginName}' ({ExtensionPoint}) exceeded execution timeout of {Timeout}ms (Fail-Closed).",
                pluginName, extensionPoint, timeout.TotalMilliseconds);
            return failureFactory("AUTH_PLUGIN_TIMEOUT", $"Plugin execution exceeded timeout of {timeout.TotalMilliseconds}ms.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = "error";
            _logger.LogError(ex, "Auth plugin '{PluginName}' ({ExtensionPoint}) threw unhandled exception (Fail-Closed).",
                pluginName, extensionPoint);
            return failureFactory("AUTH_PLUGIN_FAILED", "Plugin execution encountered an unhandled fault.");
        }
        finally
        {
            var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
            AuthPluginDiagnostics.RecordInvocation(pluginName, extensionPoint, outcome, elapsed.TotalSeconds, tenant);
            activity?.SetTag("auth.outcome", outcome);
        }
    }
}
