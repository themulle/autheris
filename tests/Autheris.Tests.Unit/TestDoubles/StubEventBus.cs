using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;

namespace Autheris.Tests.Unit.TestDoubles;

internal sealed class StubEventBus : IEventBus
{
    public static readonly StubEventBus Instance = new();

    public event Action? ConnectionRestored { add { } remove { } }

    public Task PublishAsync<T>(string channel, T message, CancellationToken ct = default) => Task.CompletedTask;

    public IDisposable Subscribe<T>(string channel, Func<T, Task> handler) => NoopSubscription.Instance;

    public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(1L);

    public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);

    private sealed class NoopSubscription : IDisposable
    {
        public static readonly NoopSubscription Instance = new();
        public void Dispose() { }
    }
}

internal sealed class StubTableSensitivityLookup(bool sensitive = false) : ITableSensitivityLookup
{
    public static readonly StubTableSensitivityLookup Insensitive = new(false);

    public ValueTask<bool> HasSensitiveDataAsync(TableIdentifier table, CancellationToken ct = default)
        => ValueTask.FromResult(sensitive);
}
