namespace Autheris.Domain.Exceptions;

using System;

/// <summary>
/// Wird ausgelöst, wenn eine Suche angefragt wird, bevor der Index betriebsbereit ist.
/// </summary>
public sealed class CatalogSearchIndexNotReadyException() 
    : InvalidOperationException("Der Katalog-Suchindex wird aktuell noch initialisiert und ist noch nicht abfragebereit.");
