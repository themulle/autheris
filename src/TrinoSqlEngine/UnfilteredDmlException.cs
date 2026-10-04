namespace TrinoSqlEngine;

using System;
using System.Security;

/// <summary>
/// Raised by the RLS rewriter when an UPDATE/DELETE statement has no WHERE clause or only a trivially true one
/// (see <see cref="RlsOptions.RejectUnfilteredDml"/>).
/// </summary>
public sealed class UnfilteredDmlException : SecurityException
{
    public UnfilteredDmlException()
        : base("UPDATE/DELETE statements require a restricting WHERE clause.")
    {
    }

    public UnfilteredDmlException(string message)
        : base(message)
    {
    }

    public UnfilteredDmlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
