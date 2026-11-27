using System.Linq;
using Autheris.Domain.Model;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common.Fakes;
using HotChocolate;
using HotChocolate.Types;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>The root field signatures the MCP describe tool reports must equal what the built schema declares.</summary>
public sealed class CatalogRootFieldSignaturesSchemaParityTests
{
    private static async Task<(ISchemaDefinition Schema, GraphQlTableMapping Mapping, TableMetadata Table)> BuildAsync()
    {
        var host = new TypedAggregateTestHost();
        var table = host.Tables.Single();
        using var map = new CatalogGraphQlMap(new InMemoryTableMetadataRepository(table), new InMemoryTableRelationRepository());
        var mapping = (await map.GetTableAsync(table.Identifier))!;
        var schema = (await host.BuildAsync()).Schema;
        return (schema, mapping, table);
    }

    private static void AssertField(ISchemaDefinition schema, GraphQlRootFieldSignature signature)
    {
        var field = schema.QueryType.Fields[signature.Field];
        field.Type.NamedType().Name.ShouldBe(signature.ResultType);
        field.Arguments.Select(a => a.Name).ShouldBe(signature.Arguments.Select(a => a.Name), ignoreOrder: true);
        foreach (var argument in signature.Arguments)
        {
            field.Arguments[argument.Name].Type.Print().ShouldBe(argument.Type, $"{signature.Field}.{argument.Name}");
        }
    }

    [Fact]
    public async Task EverySignatureArgumentType_EqualsTheBuiltSchema()
    {
        var (schema, mapping, _) = await BuildAsync();

        AssertField(schema, new GraphQlRootFieldSignature(mapping.QueryField, mapping.TypeName, mapping.ListArguments!));
        AssertField(schema, mapping.Aggregate!);
        AssertField(schema, mapping.Connection!);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Signatures_ContainNoColumnNames()
    {
        var (_, mapping, table) = await BuildAsync();
        var strings = new[] { mapping.Aggregate!, mapping.Connection! }
            .SelectMany(s => new[] { s.Field, s.ResultType }.Concat(s.Arguments.SelectMany(a => new[] { a.Name, a.Type })))
            .Concat(mapping.ListArguments!.SelectMany(a => new[] { a.Name, a.Type }))
            .ToList();

        foreach (var column in table.Columns.Select(c => c.ColumnName))
        {
            strings.ShouldNotContain(s => s.Contains("_" + column + "_", System.StringComparison.OrdinalIgnoreCase) || s.EndsWith("_" + column, System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
