using System.Collections.Frozen;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Tests.Compiler;

internal static class CompilerTestHelpers
{
    private static readonly FastSqlEngine Engine = new();

    public static SqlStatement Build(string sql)
    {
        var (tree, _) = Engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        return new SqlAstBuilder(new AstBuilderOptions()).BuildStatement(tree);
    }

    public static CompiledSql GenerateSqlServer(string sql, IDictionary<string, PolicyValue>? policyValues = null) =>
        Generate(TargetSqlDialect.SqlServer, Build(sql), policyValues);

    public static CompiledSql GenerateSqlServer(SqlStatement statement, IDictionary<string, PolicyValue>? policyValues = null) =>
        Generate(TargetSqlDialect.SqlServer, statement, policyValues);

    public static CompiledSql Generate(TargetSqlDialect dialect, string sql, IDictionary<string, PolicyValue>? policyValues = null) =>
        Generate(dialect, Build(sql), policyValues);

    public static CompiledSql Generate(TargetSqlDialect dialect, SqlStatement statement, IDictionary<string, PolicyValue>? policyValues = null)
    {
        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        var values = (policyValues ?? new Dictionary<string, PolicyValue>()).ToFrozenDictionary();
        return generator.Generate(statement, new ParameterSource(values, new Dictionary<string, object?>()), CancellationToken.None);
    }
}
