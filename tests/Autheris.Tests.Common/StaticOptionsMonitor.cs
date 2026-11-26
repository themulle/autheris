using Microsoft.Extensions.Options;

namespace Autheris.Tests.Common;

/// <summary>An <see cref="IOptionsMonitor{TOptions}"/> that returns a fixed or mutable value for tests and notifies change listeners.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public T CurrentValue { get; private set; } = value;

    public void SetCurrent(T val)
    {
        CurrentValue = val;
        Action<T, string?>[] listeners;
        lock (_listeners)
        {
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener(val, null);
        }
    }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        lock (_listeners)
        {
            _listeners.Add(listener);
        }

        return new Subscription(this, listener);
    }

    private sealed class Subscription(StaticOptionsMonitor<T> owner, Action<T, string?> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._listeners)
            {
                owner._listeners.Remove(listener);
            }
        }
    }
}
