using System.Security;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Visitors;

namespace Autheris.Tests.Fuzz.Target;

/// <summary>
/// The documented rejections of <c>FastSqlEngine.Parse</c> and <c>FastSqlEngine.Compile</c>. Everything else (NullReference,
/// IndexOutOfRange, InvalidCast, InsufficientExecutionStack, OutOfMemory, ...) is a crash.
/// </summary>
public static class AcceptedRejections
{
    /// <summary>
    /// Types accepted including subclasses. <see cref="SecurityException"/> is accepted only when declared in or thrown by the
    /// TrinoSqlEngine assembly (see <see cref="IsAccepted"/>).
    /// </summary>
    public static IReadOnlyList<Type> Types { get; } =
    [
        typeof(ParseCanceledException),
        typeof(ArgumentOutOfRangeException),
        typeof(GovernedSqlException),
        typeof(SecurityException),
        typeof(AstBuildException),
        typeof(SetOperationDegreeMismatchException),
        typeof(LiteralTokenRejectedException),
        typeof(SqlTimeZoneException),
        typeof(ReservedIdentifierException),
        typeof(AggregateTypeInvalidException),
        typeof(ClientListException),
    ];

    private static readonly System.Reflection.Assembly EngineAssembly = typeof(FastSqlEngine).Assembly;

    public static bool IsAccepted(Exception exception) => exception switch
    {
        // Only the MaxQueryLength check throws this, with parameter name "sql".
        ArgumentOutOfRangeException a => a.ParamName == "sql",
        ParseCanceledException => true,
        GovernedSqlException => true,
        // Governance refusals: a subclass declared in TrinoSqlEngine, or a plain SecurityException thrown by TrinoSqlEngine code.
        SecurityException => exception.GetType().Assembly == EngineAssembly || exception.TargetSite?.DeclaringType?.Assembly == EngineAssembly,
        AstBuildException => true,
        SetOperationDegreeMismatchException => true,
        LiteralTokenRejectedException => true,
        SqlTimeZoneException => true,
        ReservedIdentifierException => true,
        AggregateTypeInvalidException => true,
        ClientListException => true,
        _ => false,
    };
}
