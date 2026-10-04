namespace Autheris.Application.Sql;

using System;
using System.Security;

/// <summary>
/// SEC M-10: Security rejection raised by the governed WebSQL pipeline whose message is curated and safe
/// to return to the client (no database, schema, policy or server internals).
/// All other exceptions are mapped to generic client messages by the WebSQL endpoint.
/// </summary>
public sealed class WebSqlPolicyException : SecurityException
{
    public WebSqlPolicyException()
        : base("The SQL statement was rejected by the WebSQL security policy.")
    {
    }

    public WebSqlPolicyException(string message)
        : base(message)
    {
    }

    public WebSqlPolicyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
