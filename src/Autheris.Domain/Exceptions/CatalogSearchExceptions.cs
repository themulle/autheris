namespace Autheris.Domain.Exceptions;

using System;

/// <summary>
/// Raised when a search is requested before the index is ready to serve queries.
/// </summary>
public sealed class CatalogSearchIndexNotReadyException() 
    : InvalidOperationException("The catalog search index is still initializing and is not yet ready for queries.");

/// <summary>
/// Raised when a consumer needs the catalog snapshot before its first successful load. The caller must report the
/// condition as unavailable (retryable) and never treat the empty placeholder snapshot as an empty catalog.
/// </summary>
public sealed class CatalogSnapshotNotReadyException()
    : InvalidOperationException("The catalog snapshot has not been loaded yet.");
