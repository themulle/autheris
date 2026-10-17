namespace TrinoSqlEngine.Ast.Emit;

using System;

/// <summary>
/// A client list parameter was refused. Carries only a stable <c>SQL_LIST_PARAM_*</c> code; the message is the fixed message of
/// the code (no value, no count, no name). It is an <see cref="ArgumentException"/> (a caller mistake, 400), not a security hit.
/// </summary>
public sealed class ClientListException : ArgumentException
{
    public ClientListException(string code)
        : base(GovernedSqlErrorCodes.FixedMessage(code))
    {
        Code = code;
    }

    public string Code { get; }
}
