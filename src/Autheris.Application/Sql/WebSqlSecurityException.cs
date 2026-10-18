namespace Autheris.Domain.Common;

using System;
using System.Security;

/// <summary>
/// SEC-DATA131-01 / SEC-FED-37: Security exception raised by WebSQL parameter validation,
/// anti-oracle checks, or forbidden control characters.
/// </summary>
public class WebSqlSecurityException : SecurityException
{
    public WebSqlSecurityException()
        : base("WebSQL parameter security violation.")
    {
    }

    public WebSqlSecurityException(string message)
        : base(message)
    {
    }

    public WebSqlSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
