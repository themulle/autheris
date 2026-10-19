namespace Autheris.Domain.Exceptions;

using System;

/// <summary>
/// Thrown when catalog metadata validation fails (SEC-GOV163-01, fail closed).
/// </summary>
public class CatalogValidationException : Exception
{
    public CatalogValidationException(string message) : base(message) { }
    public CatalogValidationException(string message, Exception? innerException) : base(message, innerException) { }
}
