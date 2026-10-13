using System.Text;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate golden test (M-12) of <see cref="AstSecurityVisitor.BuildDialectMaskExpression"/>: the exact SQL text of every masking
/// rule per dialect (quote escaping, clamping of the jitter decimals, prefix/suffix arithmetic, typed NULL casts, replacement escaping).
/// The golden file lives in <c>Compiler/Golden/mask-expressions.txt</c>; a deliberate change of the generator updates it in the same commit.
/// </summary>
[Trait("Category", "SqlGate")]
public class MaskExpressionGoldenTests
{
    private static readonly TargetSqlDialect[] Dialects =
        { TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb, TargetSqlDialect.Sqlite, TargetSqlDialect.Oracle };

    private static readonly string[] TypeNames =
    {
        "int", "INTEGER", "bigint", "smallint", "tinyint", "decimal(10,2)", "numeric", "float", "real", "double", "double precision", "bit", "bool",
        "boolean", "date", "datetime", "datetime2", "timestamp", "timestamptz", "uuid", "uniqueidentifier", "money", "smallmoney", "smalldatetime",
        "time", "varchar(10)", "text", "money (5)", "numeric(12,3)", "DECIMAL", "NUMERIC (5)"
    };

    private static string Render()
    {
        var sb = new StringBuilder();
        foreach (var d in Dialects)
        {
            sb.Append("== ").Append(d).Append('\n');
            sb.Append("GEO: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c\"]x", "geo_jitter", d, decimals: 9)).Append('\n');
            sb.Append("GEO3: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c", "GEO_JITTER", d, decimals: -4)).Append('\n');
            sb.Append("GEO2: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c", "GEO_JITTER", d, decimals: 3)).Append('\n');
            foreach (var (p, s, fl, mc) in new[] { (2, 0, false, '#'), (1, 3, false, '*'), (1, 3, true, 'x'), (0, 0, true, '*'), (-1, -2, false, '*') })
            {
                sb.Append($"PM p={p} s={s} fixed={fl} mc={mc}: ")
                    .Append(AstSecurityVisitor.BuildDialectMaskExpression("c", " partial_mask ", d, keepPrefix: p, keepSuffix: s, fixedLength: fl, maskChar: mc)).Append('\n');
            }

            sb.Append("RED: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c", "REDACT", d)).Append('\n');
            sb.Append("RED2: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c", "redact", d, replacement: "it's")).Append('\n');
            sb.Append("RED nullrule: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c", null!, d)).Append('\n');
            sb.Append("NULLIFY: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c", "nullify", d)).Append('\n');
            sb.Append("UNK: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c", "weird", d, dataType: "  ")).Append('\n');
            sb.Append("UNK2: ").Append(AstSecurityVisitor.BuildDialectMaskExpression("c", "weird", d, replacement: "x'y")).Append('\n');
            foreach (var t in TypeNames)
            {
                sb.Append($"T[{t}] redact={AstSecurityVisitor.BuildDialectMaskExpression("c", "REDACT", d, dataType: t)}")
                    .Append($" | default={AstSecurityVisitor.BuildDialectMaskExpression("c", "other", d, dataType: t)}").Append('\n');
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void MaskExpressions_MatchTheGoldenFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Compiler", "Golden", "mask-expressions.txt");
        if (Environment.GetEnvironmentVariable("SQLGATE_UPDATE_GOLDEN") == "1")
        {
            // regenerates the copy in the output folder; copy it over Compiler/Golden/mask-expressions.txt after reviewing the diff
            File.WriteAllText(path + ".new", Render());
        }

        string golden = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(golden.TrimEnd('\n'), Render().TrimEnd('\n'));
    }
}
