namespace TrinoSqlEngine;

using System;
using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// The user statement uses an identifier with the reserved gateway prefix <see cref="Prefix"/> (alias, column, table or CTE name).
/// The gateway builds aliases with this prefix itself (the INSERT check alias <c>autheris_ins</c>, the policy alias
/// <c>autheris_target</c>), so a user identifier of the same shape could be confused with them. This is an error of the statement
/// (HTTP 400, code <see cref="GovernedSqlErrorCodes.ReservedIdentifier"/>), not a gateway defect: it raises no defect metric. The message
/// is fixed and never echoes the identifier.
/// </summary>
public sealed class ReservedIdentifierException : Exception
{
    /// <summary>The reserved prefix, compared case-insensitively and without quotes.</summary>
    public const string Prefix = "autheris_";

    public ReservedIdentifierException()
        : base(GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.ReservedIdentifier))
    {
    }
}
