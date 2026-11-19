using Microsoft.Extensions.Options;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// Minimal <see cref="IOptionsMonitor{TOptions}"/> for tests. The current value is either a fixed instance or
/// re-evaluated from a delegate on every read; change notifications are not raised.
/// </summary>
public sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    where TOptions : class
{
    private readonly Func<TOptions> _current;

    public TestOptionsMonitor(TOptions value) => _current = () => value;

    public TestOptionsMonitor(Func<TOptions> current) => _current = current;

    public TOptions CurrentValue => _current();

    public TOptions Get(string? name) => _current();

    public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
}
