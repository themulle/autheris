using Microsoft.Extensions.Options;

namespace Autheris.Tests.Common;

/// <summary>An <see cref="IOptionsMonitor{TOptions}"/> that returns a fixed or mutable value for tests.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; private set; } = value;

    public void SetCurrent(T val) => CurrentValue = val;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
