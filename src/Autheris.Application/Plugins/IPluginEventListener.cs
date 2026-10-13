namespace Autheris.Application.Plugins;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Events;

/// <summary>
/// Non-generic marker interface for plugin event listeners.
/// </summary>
public interface IPluginEventListener
{
}

/// <summary>
/// Strongly-typed event listener interface implemented by C# DLL plugins.
/// Allows plugins to react to domain and operational events emitted by the gateway.
/// </summary>
/// <typeparam name="TEvent">The type of domain event.</typeparam>
public interface IPluginEventListener<in TEvent> : IPluginEventListener
    where TEvent : IGatewayEvent
{
    /// <summary>
    /// Handles the specified event asynchronously.
    /// </summary>
    Task HandleEventAsync(TEvent @event, CancellationToken ct = default);
}
