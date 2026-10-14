using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

namespace TrinoSqlEngine.Tests;

/// <summary>
/// Inline emission of a user statement for one dialect: parse (strict token guards of the dialect), build, simplify, validate and
/// generate text. No security injection happens here (no policy, no tenant, no catalog); it exists for the tests of the emission
/// rules of the generators. Governed behaviour (policy, masks, binding, rejections) is tested through <c>FastSqlEngine.Compile</c>
/// (see <c>ProbeEnv</c> and the <c>Compiler</c> tests), which is the only production path since the X1 cutover.
/// </summary>
internal static class AstEmit
{
    private static readonly FastSqlEngine Engine = new();

    public static SqlStatement Build(string sql, TargetSqlDialect dialect, bool translateDates = false, IReadOnlySet<string>? allowedFunctions = null, bool enforceReadOnly = true)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.ForDialect(dialect));
        var ast = new SqlAstBuilder(new AstBuilderOptions
        {
            EnforceReadOnlyQueries = enforceReadOnly,
            EnforceFunctionPolicy = true,
            AllowedFunctions = allowedFunctions,
            TranslateTrinoDateFunctions = translateDates
        }).BuildStatement(tree);
        var simplified = (SqlStatement)new AstSimplificationVisitor().Visit(ast);
        new AstValidationVisitor().Validate(simplified);
        return simplified;
    }

    public static string Inline(string sql, TargetSqlDialect dialect, bool translateDates = false, IReadOnlySet<string>? allowedFunctions = null, bool enforceReadOnly = true) =>
        SqlDialectGeneratorFactory.GetGenerator(dialect).GenerateSql(Build(sql, dialect, translateDates, allowedFunctions, enforceReadOnly));
}
