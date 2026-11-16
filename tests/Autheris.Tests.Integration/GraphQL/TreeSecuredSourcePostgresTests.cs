namespace Autheris.Tests.Integration.GraphQL;

using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

/// <summary>
/// PostgreSQL execution verification for <see cref="TreeSecuredSourceBuilder"/> parameter-marker lookalike defense.
/// </summary>
public sealed class TreeSecuredSourcePostgresTests
{
    [Trait("Category", "Security")]
    [RequiresContainerRuntimeFact]
    public async Task SQLA06F_MarkerLookalikeInClientValue_StaysBoundAndInert()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        await PostgresTreeHarness.SeedAsync(db.ConnectionString);

        var harness = new PostgresTreeHarness(db.ConnectionString);
        var access = await harness.TreeAccessAsync(PostgresTreeHarness.OrdersId, PostgresTreeHarness.ExistsPolicy(), "t1");
        var map = new Dictionary<TableIdentifier, TreeTableAccess> { [PostgresTreeHarness.OrdersId] = access };

        foreach (var value in new[] { "@p0", "$1", "@gq1", "@p10", "'; DROP TABLE orders; --" })
        {
            var root = new TreeQueryNode(PostgresTreeHarness.OrdersId, ["id"])
            {
                Limit = 10,
                Where = new TreeComparison("email", TreeFilterOperator.Eq, value)
            };

            var compiled = TreeSqlCompiler.Compile(root, map, DatabaseDialect.PostgreSql);

            if (value != "@gq1")
            {
                compiled.Sql.ShouldNotContain(value);
            }

            compiled.Parameters.Values.ShouldContain(value);
            using var doc = JsonDocument.Parse(await harness.RunTreeAsync(root, map));
            doc.RootElement.GetArrayLength().ShouldBe(0);
        }
    }
}
