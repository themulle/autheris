namespace Autheris.Tests.Unit.TestDoubles;

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Domain.Options.DataSources;

/// <summary>
/// Issue #447: hand-built data-path services in unit tests have no DI-provided transport gate. Their pre-existing tests exercise the
/// governed pipeline, not the transport check, so they run with an allow-all gate. The seam is internal to Application and installed
/// only here; production code always falls back to <see cref="DenyAllTransportGate"/> (SEC-ATR-01). Tests of the gate itself
/// construct the real <see cref="DataSourceTransportGate"/> or pass an explicit gate.
/// </summary>
internal sealed class AllowAllTransportGate : IDataSourceTransportGate
{
    public static AllowAllTransportGate Instance { get; } = new();

    public DataSourceTransportFlags Ingress => DataSourceTransportFlags.WebSql;

    public IReadOnlyList<string> CheckedSources => [];

    public Task EnsureAsync(string? dataSourceName, CancellationToken ct = default) => Task.CompletedTask;

    public Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default) => Task.CompletedTask;

    public Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default) => Task.CompletedTask;

    public Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default) => Task.CompletedTask;

    public bool IsAllowed(string? dataSourceName) => true;
}

internal static class AllowAllTransportGateSeam
{
    [ModuleInitializer]
    internal static void Install() => TransportGateFallback.Current = AllowAllTransportGate.Instance;
}
