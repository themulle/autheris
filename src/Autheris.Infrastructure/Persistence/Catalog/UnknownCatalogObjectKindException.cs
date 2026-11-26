namespace Autheris.Infrastructure.Persistence.Catalog;

using System;

/// <summary>Raised when a persisted catalog row carries an object kind that is not on the allow-list.</summary>
internal sealed class UnknownCatalogObjectKindException(string objectKind)
    : FormatException("The stored catalog object kind is unknown.")
{
    /// <summary>The sanitized, length-bounded stored value.</summary>
    public string ObjectKind { get; } = objectKind;
}
