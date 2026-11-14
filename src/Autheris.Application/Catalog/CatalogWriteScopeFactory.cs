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
