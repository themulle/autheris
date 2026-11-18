namespace Autheris.Domain.Exceptions;

using System;

/// <summary>
/// Wird ausgelöst, wenn eine Suche angefragt wird, bevor der Index betriebsbereit ist.
/// </summary>
public sealed class CatalogSearchIndexNotReadyException() 
    : InvalidOperationException("Der Katalog-Suchindex wird aktuell noch initialisiert und ist noch nicht abfragebereit.");

/// <summary>
/// Raised when a consumer needs the catalog snapshot before its first successful load. The caller must report the
/// condition as unavailable (retryable) and never treat the empty placeholder snapshot as an empty catalog.
/// </summary>
public sealed class CatalogSnapshotNotReadyException()
    : InvalidOperationException("The catalog snapshot has not been loaded yet.");
