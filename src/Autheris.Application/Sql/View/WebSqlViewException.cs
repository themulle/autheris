namespace Autheris.Application.Sql.View;

using System;
using System.Linq;

/// <summary>
/// A structured <c>view</c> was refused (unknown or non-output column, unsupported member or operator, a cap exceeded, or a stored statement the
/// view cannot be applied to). The message is fixed and value-free; it may name the offending column (length-bounded, control characters removed)
/// because the caller supplied it. The request is a 400, never a policy decision.
/// </summary>
public sealed class WebSqlViewException : Exception
{
    public const string ErrorCode = "SQL_VIEW_INVALID";

    public WebSqlViewException(string message)
        : base(message)
    {
    }

    /// <summary>The fixed, value-free text a client may see. Never an engine or parser message.</summary>
    public string UserMessage => Message;

    internal static WebSqlViewException ForColumn(string prefix, string column)
    {
        var safe = new string(column.Where(static c => !char.IsControl(c)).Take(64).ToArray());
        return new WebSqlViewException($"{prefix} '{safe}'.");
    }
}
