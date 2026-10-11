using System.Security;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Tests.Compiler;

namespace TrinoSqlEngine.Tests.Properties;

/// <summary>G3 oracle: text -> AST -> printed text -> AST must be structurally identical.</summary>
internal static class RoundTrip
{
    public static (List<Violation> Violations, bool Built, bool Rendered) Check(StmtCase c, Func<SqlStatement, string> render)
    {
        var violations = new List<Violation>();
        if (c.Dml is not null)
        {
            return (violations, false, false);
        }

        SqlStatement first;
        try
        {
            first = CompilerTestHelpers.Build(c.Sql);
        }
        catch (Exception e) when (e is ParseCanceledException or AstBuildException or InvalidCastException or SecurityException)
        {
            return (violations, false, false);
        }

        string text;
        try
        {
            text = render(first);
        }
        catch (RendererGapException)
        {
            return (violations, true, false);
        }

        try
        {
            var second = CompilerTestHelpers.Build(text);
            if (AstStructure.FirstDifference(first, second) is { } difference)
            {
                violations.Add(new Violation(Oracles.Identity, $"printed Trino text parses to a different tree at {difference}; printed: {text}"));
            }
        }
        catch (Exception e)
        {
            violations.Add(new Violation(Oracles.Identity, $"printed Trino text no longer parses ({e.GetType().Name}); printed: {text}"));
        }

        return (violations, true, true);
    }
}
