namespace Autheris.Application.Catalog;

using System;
using Microsoft.Extensions.Logging;

/// <summary>Default <see cref="ICatalogWriteScopeFactory"/>; the flusher is the catalog repository.</summary>
public sealed class CatalogWriteScopeFactory : ICatalogWriteScopeFactory
{
    private readonly ICatalogVersionFlusher _flusher;
    private readonly ICatalogDirtyFlag _dirtyFlag;
    private readonly ILogger<CatalogWriteScopeFactory>? _logger;
    private readonly TimeProvider _timeProvider;

    public CatalogWriteScopeFactory(
        ICatalogVersionFlusher flusher,
        ICatalogDirtyFlag dirtyFlag,
        ILogger<CatalogWriteScopeFactory>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _flusher = flusher ?? throw new ArgumentNullException(nameof(flusher));
        _dirtyFlag = dirtyFlag ?? throw new ArgumentNullException(nameof(dirtyFlag));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ICatalogWriteScope Begin(string writer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writer);

        var outer = CatalogWriteScopeContext.TryGetActive(_flusher);
        if (outer is not null)
        {
            return outer.Join();
        }

        var scope = new CatalogWriteScope(writer, _flusher, _dirtyFlag, _logger, _timeProvider);
        CatalogWriteScopeContext.Slot.Value = scope;
        return scope;
    }
}

/// <summary>Helpers for writers that optionally take an <see cref="ICatalogWriteScopeFactory"/> (tests and tools run without one).</summary>
public static class CatalogWriteScopeFactoryExtensions
{
    /// <summary>Begins a write scope, or returns a scope that does nothing when no factory is registered.</summary>
    public static ICatalogWriteScope BeginOrNoop(this ICatalogWriteScopeFactory? factory, string writer) =>
        factory?.Begin(writer) ?? NoopCatalogWriteScope.Instance;

    private sealed class NoopCatalogWriteScope : ICatalogWriteScope
    {
        public static readonly NoopCatalogWriteScope Instance = new();

        public Guid ScopeId => Guid.Empty;

        public string Writer => string.Empty;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
