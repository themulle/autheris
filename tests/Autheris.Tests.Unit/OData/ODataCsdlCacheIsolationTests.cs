namespace Autheris.Tests.Unit.OData;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Extensions.OData;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public class ODataCsdlCacheIsolationTests
{
    private static readonly TableIdentifier OrdersTableId = new("corp", "sales", "orders");

    private static TableMetadata CreateOrdersTable() => new()
    {
        Identifier = OrdersTableId,
        Table = new Table { SchemaName = "sales", TableName = "orders", SourceName = "corp" },
        Columns =
        [
            new TableColumn { ColumnName = "order_id", DataType = "integer" },
            new TableColumn { ColumnName = "customer_name", DataType = "varchar" },
            new TableColumn { ColumnName = "salary", DataType = "decimal" }
        ],
        PrimaryKeyColumns = ["order_id"]
    };

    private static (ODataHandler Handler, IMemoryCache Cache) CreateHandler(TableMetadata table, Dictionary<string, HashSet<string>> userDeniedColumns)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));

        var exec = Substitute.For<IGatewayExecutionService>();
        exec.CheckTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableMetadata>(), false, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var principal = ci.ArgAt<ClaimsPrincipal?>(0);
                var userName = principal?.Identity?.Name ?? "anon";
                var denied = userDeniedColumns.TryGetValue(userName, out var cols) ? cols : new HashSet<string>();

                var colDecisions = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in denied)
                {
                    colDecisions[col] = ColumnAccessLevel.Deny;
                }

                return Task.FromResult(TableAccessDecision.Allowed(OrdersTableId, colDecisions, hasUnconstrainedColumnAllow: true));
            });

        var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new ODataHandler(repo, exec, NullLogger<ODataHandler>.Instance, memoryCache: cache);
        return (handler, cache);
    }

    private static ClaimsPrincipal CreateUser(string name, string tenant = "corp") =>
        new(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.NameIdentifier, name),
            new Claim("tenant_id", tenant)
        ], "TestAuth"));

    [Fact]
    public async Task Csdl_UserWithDeniedColumn_DoesNotReceiveCachedFullSchema()
    {
        var table = CreateOrdersTable();
        var userDenied = new Dictionary<string, HashSet<string>>
        {
            ["Alice"] = [], // Full access
            ["Bob"] = ["salary"] // Denied salary
        };

        var (handler, _) = CreateHandler(table, userDenied);

        var alice = CreateUser("Alice");
        var bob = CreateUser("Bob");

        // Alice loads CSDL first -> gets salary in CSDL, populates cache
        var aliceCsdl = await handler.GetMetadataCsdlAsync(alice);
        aliceCsdl.ShouldContain("Name=\"salary\"");

        // Bob loads CSDL second -> must NOT receive cached CSDL with salary!
        var bobCsdl = await handler.GetMetadataCsdlAsync(bob);
        bobCsdl.ShouldNotContain("Name=\"salary\"");
        bobCsdl.ShouldContain("Name=\"order_id\"");
        bobCsdl.ShouldContain("Name=\"customer_name\"");
    }

    [Fact]
    public async Task Csdl_FullAccessUser_DoesNotReceiveCachedReducedSchema()
    {
        var table = CreateOrdersTable();
        var userDenied = new Dictionary<string, HashSet<string>>
        {
            ["Alice"] = [],
            ["Bob"] = ["salary"]
        };

        var (handler, _) = CreateHandler(table, userDenied);

        var alice = CreateUser("Alice");
        var bob = CreateUser("Bob");

        // Bob loads CSDL first -> populates cache with reduced schema
        var bobCsdl = await handler.GetMetadataCsdlAsync(bob);
        bobCsdl.ShouldNotContain("Name=\"salary\"");

        // Alice loads CSDL second -> must receive full schema with salary
        var aliceCsdl = await handler.GetMetadataCsdlAsync(alice);
        aliceCsdl.ShouldContain("Name=\"salary\"");
    }

    [Fact]
    public async Task Csdl_SameVisibility_SharesCacheEntry()
    {
        var table = CreateOrdersTable();
        var userDenied = new Dictionary<string, HashSet<string>>
        {
            ["Alice"] = ["salary"],
            ["Charlie"] = ["salary"]
        };

        var (handler, _) = CreateHandler(table, userDenied);

        var alice = CreateUser("Alice");
        var charlie = CreateUser("Charlie");

        var aliceCsdl = await handler.GetMetadataCsdlAsync(alice);
        var charlieCsdl = await handler.GetMetadataCsdlAsync(charlie);

        // Identical output
        charlieCsdl.ShouldBe(aliceCsdl);
        charlieCsdl.ShouldNotContain("Name=\"salary\"");
    }
}
