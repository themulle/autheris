namespace Autheris.Tests.Unit.Sql.Federation;

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Autheris.Application.Sql.Federation.Conjuncts;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class SqlConjunctSplitterChainTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void FlattenAnd_And_CollectColumnReferences_1000Terms_SmallStack_NoOverflow()
    {
        var sb = new StringBuilder("SELECT id FROM dbo.orders WHERE ");
        for (int i = 0; i < SqlAstBuilder.MaxOperatorChainTerms; i++)
        {
            if (i > 0) sb.Append(" AND ");
            sb.Append('c').Append(i).Append(" IS NOT NULL");
        }

        var engine = new FastSqlEngine();
        var (tree, _) = engine.Parse(sb.ToString().AsMemory(), SqlTokenSecurityOptions.Strict);
        var select = (SelectStatement)new SqlAstBuilder(new AstBuilderOptions
        {
            AllowedFunctions = TrinoSqlEngine.SqlFunctionAllowlists.WithDateTranslation(TrinoSqlEngine.SqlFunctionAllowlists.DuckDb)
        }).BuildStatement(tree);
        var where = ((QuerySpecification)select.Body).Where!;

        Exception? failure = null;
        var flattened = new List<Expression>();
        var columns = new List<ColumnReference>();

        var thread = new Thread(() =>
        {
            try
            {
                SqlConjunctSplitter.FlattenAnd(where, flattened);
                SingleTableConjunctClassifier.CollectColumnReferences(where, columns);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, maxStackSize: 512 * 1024);

        thread.Start();
        thread.Join();

        failure.ShouldBeNull();
        flattened.Count.ShouldBe(SqlAstBuilder.MaxOperatorChainTerms);
        columns.Count.ShouldBe(SqlAstBuilder.MaxOperatorChainTerms);
    }
}
