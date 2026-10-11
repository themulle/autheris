namespace TrinoSqlEngine.Ast.Generators;

using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using System.Threading;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Target SQL dialect code emitter interface.
/// </summary>
public interface ISqlDialectGenerator
{
    TargetSqlDialect TargetDialect { get; }
    int MaxParameterBudget { get; }
    void GenerateSql(SqlStatement statement, ref ValueStringBuilder builder, SqlEmitterContext context);
    string GenerateSql(SqlStatement statement);

    /// <summary>Capabilities of the dialect; a dialect without a capability entry throws.</summary>
    DialectCapabilities Capabilities { get; }

    /// <summary>Parameterized output: every value is bound, the text is verified before it is returned.</summary>
    CompiledSql Generate(SqlStatement statement, ParameterSource values, CancellationToken cancellationToken = default);
}
