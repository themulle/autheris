namespace Autheris.Domain.Exceptions;

using System;

/// <summary>
/// Wird ausgelöst, wenn eine Suche angefragt wird, bevor der Index betriebsbereit ist.
/// </summary>
public sealed class CatalogSearchIndexNotReadyException() 
    : InvalidOperationException("Der Katalog-Suchindex wird aktuell noch initialisiert und ist noch nicht abfragebereit.");

/// <summary>
/// Wird ausgelöst, wenn ein Vektor eine unerwartete Dimensionierung aufweist.
/// </summary>
public sealed class InvalidEmbeddingDimensionsException(int expected, int actual) 
    : InvalidOperationException($"Die Vektordimension {actual} entspricht nicht der erwarteten Modell-Dimension {expected}.");
