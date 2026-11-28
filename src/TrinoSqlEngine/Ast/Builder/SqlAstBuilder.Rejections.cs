namespace TrinoSqlEngine.Ast.Builder;

using Antlr4.Runtime;

public sealed partial class SqlAstBuilder
{
    /// <summary>
    /// Like <see cref="Unsupported(string)"/>, and names a catalogued statement-structure construct with its position, but only while the
    /// builder parses client statement text. A policy-text builder keeps the unnamed rejection.
    /// </summary>
    private AstBuildException Unsupported(string construct, string publicConstruct, IToken? at) =>
        _options.ExposeRejectionDetails && SqlConstructCatalog.IsPublic(publicConstruct)
            ? new($"SQL construct {construct} is not supported by the AST compiler.", publicConstruct, at?.Line, at is null ? null : at.Column + 1)
            : Unsupported(construct);
}
