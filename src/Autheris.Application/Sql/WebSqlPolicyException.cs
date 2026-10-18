namespace Autheris.Application.Sql;

using System;
using System.Security;

/// <summary>
/// SEC M-10: Security rejection raised by the governed WebSQL pipeline whose message is curated and safe
/// to return to the client (no database, schema, policy or server internals).
/// All other exceptions are mapped to generic client messages by the WebSQL endpoint.
/// </summary>
public class WebSqlPolicyException : SecurityException
{
    public string? Code { get; init; }

    public WebSqlPolicyException()
        : base("The SQL statement was rejected by the WebSQL security policy.")
    {
    }

    public WebSqlPolicyException(string message)
        : base(message)
    {
    }

    public WebSqlPolicyException(string message, string code)
        : base(message)
    {
        Code = code;
    }

    public WebSqlPolicyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WebSqlPolicyException(string message, string code, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}

/// <summary>
/// SEC-X1-05: a table is not usable for the caller (not registered, inactive, no columns, denied, case-spelling conflict). Every such
/// cause maps to the one stable code <c>SQL_POLICY_DENIED</c>, so a response never tells whether the table exists. Statement-level
/// rejections that do not depend on the table stay plain <see cref="WebSqlPolicyException"/> (<c>SQL_POLICY_VIOLATION</c>).
/// </summary>
public sealed class WebSqlTableDeniedException : WebSqlPolicyException
{
    public WebSqlTableDeniedException(string message)
        : base(message)
    {
    }
}
