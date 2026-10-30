namespace Autheris.Tests.Unit.Dbt;

using Autheris.Tests.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Shared builders of the #112 semantic catalog tests.</summary>
internal static class SemanticCatalogTestKit
{
    internal static IOptions<GatewayOptions> Mode(TenancyShape? mode) =>
        Options.Create(new GatewayOptions
        {
            Tenancy = (mode ?? TenancyShape.Multi).Options()
        });

    internal static DbtSemanticManifest Manifest(string version, params string[] metricNames)
    {
        var model = new DbtSemanticModel(
            "orders", "Orders", "analytics.public.orders",
            [new DbtEntity("order_id", "primary")],
            [new DbtMeasure("order_total", "sum", "amount", null)],
            [new DbtDimension("status", "categorical", null)]);
        var metrics = new List<DbtMetric>();
        foreach (var name in metricNames)
        {
            metrics.Add(new DbtMetric(name, "simple", "d", name, null, null, ["status"], ["order_total"], new Dictionary<string, string>()));
        }

        return new DbtSemanticManifest([model], metrics, version);
    }
}

/// <summary>Counting in-memory <see cref="ISemanticManifestStore"/>.</summary>
internal sealed class FakeSemanticManifestStore : ISemanticManifestStore
{
    private readonly ConcurrentDictionary<string, StoredSemanticManifest> _rows = new();
    private int _getCount;

    public int GetCount => _getCount;

    public TaskCompletionSource? Gate { get; set; }

    public async Task<StoredSemanticManifest?> GetAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _getCount);
        if (Gate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        return _rows.TryGetValue(tenantId.Value, out var row) ? row : null;
    }

    public Task SaveAsync(TenantId tenantId, StoredSemanticManifest manifest, CancellationToken cancellationToken = default)
    {
        _rows[tenantId.Value] = manifest;
        return Task.CompletedTask;
    }

    public void Put(StoredSemanticManifest row) => _rows[row.TenantId.Value] = row;

    public StoredSemanticManifest? Peek(TenantId tenantId) => _rows.TryGetValue(tenantId.Value, out var row) ? row : null;
}

/// <summary>Records the log levels of a logger.</summary>
internal sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<Microsoft.Extensions.Logging.LogLevel> Levels { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Levels.Add(logLevel);
}
