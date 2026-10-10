namespace TrinoSqlEngine.Ast.Capabilities;

using System.Globalization;
using System.Security;

/// <summary>Kind of a resource or dialect limit (INV-7).</summary>
public enum SqlLimitKind
{
    BindParameters,
    InListItems,
    IdentifierLength,
    QueryLength,
    NestingDepth,
    AstDepth,
    SecuredTableReferences,
    EmittedSqlLength,
    PolicyExpansionFactor,
    CompileTime
}

/// <summary>
/// Typed, auditable limit rejection (INV-7). The message carries only the kind, dialect and counts, never values (INV-16).
/// </summary>
public sealed class SqlLimitExceededException : SecurityException
{
    public SqlLimitKind Kind { get; }
    public TargetSqlDialect Dialect { get; }
    public long Requested { get; }
    public long Maximum { get; }

    public SqlLimitExceededException(SqlLimitKind kind, TargetSqlDialect dialect, long requested, long maximum)
        : base(string.Create(CultureInfo.InvariantCulture, $"SQL limit exceeded: {kind} for dialect {dialect}, requested {requested}, maximum {maximum}."))
    {
        Kind = kind;
        Dialect = dialect;
        Requested = requested;
        Maximum = maximum;
    }
}
