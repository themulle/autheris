using Microsoft.Extensions.Options;

namespace Autheris.Tests.Common;

/// <summary>An <see cref="IOptionsMonitor{TOptions}"/> that always returns one fixed value.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
