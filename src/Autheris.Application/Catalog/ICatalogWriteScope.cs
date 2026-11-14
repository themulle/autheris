namespace Autheris.Application.Catalog;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Classification of a catalog write for version publication. Only additive or permissive writes may be coalesced by a
/// write scope; everything else (restrictive or unknown) bumps the catalog version and publishes immediately.
/// </summary>
public enum CatalogWriteKind
{
    /// <summary>New table, new column, description or tag change, relation add: may be deferred to the end of a scope.</summary>
    Additive = 0,

    /// <summary>Anything that can reduce access or whose effect is unknown: never deferred.</summary>
    Restrictive = 1,
}

/// <summary>
/// Unit of work for a multi-table catalog write. Per-table data and per-table policy epochs are still committed per table;
/// only the catalog version bump and the <c>catalog:changed</c> event of additive writes are coalesced and flushed once
/// when the outermost scope is disposed (and every <see cref="CatalogWriteScopeSettings.ChunkSize"/> changed tables).
/// </summary>
public interface ICatalogWriteScope : IAsyncDisposable
{
    /// <summary>Unique id of the scope (nested scopes report the id of the outer scope they joined).</summary>
    Guid ScopeId { get; }

    /// <summary>Name of the writer that opened the scope (diagnostics only).</summary>
    string Writer { get; }
}

/// <summary>Opens <see cref="ICatalogWriteScope"/> instances. The scope is ambient (like a transaction scope).</summary>
public interface ICatalogWriteScopeFactory
{
    /// <summary>
    /// Begins a write scope. Must be called synchronously by the writer (not from an awaited helper) so that the ambient
    /// scope flows into the writer's async calls. A scope opened while another active scope exists joins the outer one.
    /// </summary>
    ICatalogWriteScope Begin(string writer);
}

/// <summary>Commits a catalog version bump and publishes the change event in its own short transaction.</summary>
public interface ICatalogVersionFlusher
{
    /// <summary>
    /// Bumps the catalog version by the given deltas (each at most 1 is expected from a scope) and publishes
    /// <c>catalog:changed</c> once. Implementations never roll back or hide a committed bump because publishing failed.
    /// </summary>
    Task FlushCatalogVersionAsync(long structureDelta, long contentDelta, CancellationToken ct = default) =>
        throw new NotSupportedException($"{GetType().Name} does not support catalog version flushes.");
}

/// <summary>
/// Process-wide marker that a deferred catalog version bump could not be flushed. The catalog snapshot poll repairs it by
/// forcing a version bump; health reports degraded while it is set.
/// </summary>
public interface ICatalogDirtyFlag
{
    bool IsDirty { get; }

    void MarkDirty();

    void Clear();
}

/// <summary>Default <see cref="ICatalogDirtyFlag"/>.</summary>
public sealed class CatalogDirtyFlag : ICatalogDirtyFlag
{
    private int _dirty;

    public bool IsDirty => Volatile.Read(ref _dirty) != 0;

    public void MarkDirty() => Volatile.Write(ref _dirty, 1);

    public void Clear() => Volatile.Write(ref _dirty, 0);
}

/// <summary>Constants of the catalog write scope.</summary>
public static class CatalogWriteScopeSettings
{
    /// <summary>Number of additive changed tables after which a scope flushes one version bump.</summary>
    public const int ChunkSize = 500;

    /// <summary>Upper bound of one flush (all retries included).</summary>
    public static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Flush attempts before the dirty flag is set.</summary>
    public const int MaxFlushAttempts = 3;
}
