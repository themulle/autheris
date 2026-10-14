using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class GraphQLTests
{
    [Theory]
    [InlineData(10_001)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public async Task SR_M_17_TableRootField_RejectsAfterBeyondMaxAllowedOffset(int after)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions
        {
            GraphQL = new Autheris.Domain.Options.GraphQLOptions { MaxAllowedOffset = 10_000 }
        });

        var ex = await Should.ThrowAsync<HotChocolate.GraphQLException>(() => new Query().GetTableAsync(
            "sales", "invoices", "dbo", 50, after, null!, null!, options));

        ex.Errors[0].Code.ShouldBe("BAD_REQUEST");
    }






    [Fact]
    public async Task SchemaSnapshot_BuildsValidGraphQLSchema()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>();

        var sp = services.BuildServiceProvider();
        var executor = await sp.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        var schema = executor.Schema;

        var sdl = schema.ToString();
        sdl.ShouldNotBeNull();
        sdl.ShouldContain("type Query");
        sdl.ShouldContain("type Mutation");
        sdl.ShouldContain("table(");
        sdl.ShouldContain("TableRecordPayload");
        sdl.ShouldContain("requestTableAccess");
        sdl.ShouldContain("approveConsentRequest");
        sdl.ShouldContain("revokeConsent");
    }





    [Fact]
    public async Task DynamicTableType_WhenColumnIsBigint_MapsToLongType()
    {
        var table = new Table
        {
            SchemaName = "dbo",
            TableName = "transactions",
            DisplayName = "Transactions"
        };
        var columns = new[]
        {
            new TableColumn { ColumnName = "tx_id", DataType = "bigint" },
            new TableColumn { ColumnName = "quantity", DataType = "int" }
        };
        var meta = new TableMetadata
        {
            Table = table,
            Identifier = new TableIdentifier("finance", "dbo", "transactions"),
            Columns = columns
        };

        var masking = NSubstitute.Substitute.For<IColumnMaskingProvider>();

        var schema = await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType(d => d.Name("Query").Field("test").Resolve(_ => "ok"))
            .AddType(new Autheris.GraphQL.DynamicTypes.DynamicTableType(meta, masking))
            .BuildSchemaAsync();

        var dynamicType = schema.Types.GetType<IObjectTypeDefinition>("finance_transactions");
        dynamicType.ShouldNotBeNull();
        ((INameProvider)dynamicType.Fields["tx_id"].Type).Name.ShouldBe("Long");
        ((INameProvider)dynamicType.Fields["quantity"].Type).Name.ShouldBe("Int");
    }



}


