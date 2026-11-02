using Autheris.GraphQL.Types;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class GraphQlSchemaGoldenTests
{
    private const string SnapshotRelativePath = "GraphQL/Snapshots/graphql-schema-golden.graphql";

    public static string NormalizeSdl(string sdl)
    {
        var doc = Utf8GraphQLParser.Parse(sdl);
        var sortedDefinitions = doc.Definitions
            .OrderBy(d => d switch
            {
                SchemaDefinitionNode => "00_schema",
                ObjectTypeDefinitionNode o => $"01_{o.Name.Value}",
                InterfaceTypeDefinitionNode i => $"02_{i.Name.Value}",
                UnionTypeDefinitionNode u => $"03_{u.Name.Value}",
                InputObjectTypeDefinitionNode inp => $"04_{inp.Name.Value}",
                EnumTypeDefinitionNode e => $"05_{e.Name.Value}",
                ScalarTypeDefinitionNode s => $"06_{s.Name.Value}",
                DirectiveDefinitionNode dir => $"07_{dir.Name.Value}",
                _ => d.ToString()
            })
            .Select(d => d switch
            {
                ObjectTypeDefinitionNode o => o.WithFields(o.Fields.OrderBy(f => f.Name.Value).Select(f => f.WithArguments(f.Arguments.OrderBy(a => a.Name.Value).ToList())).ToList()),
                InputObjectTypeDefinitionNode inp => inp.WithFields(inp.Fields.OrderBy(f => f.Name.Value).ToList()),
                EnumTypeDefinitionNode e => e.WithValues(e.Values.OrderBy(v => v.Name.Value).ToList()),
                _ => d
            })
            .ToList();

        return doc.WithDefinitions(sortedDefinitions).ToString().Replace("\r\n", "\n").Trim();
    }

    [Fact]
    public async Task RuntimeSchema_MatchesGoldenSdl()
    {
        var services = new ServiceCollection();
        services.AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>();

        var sp = services.BuildServiceProvider();
        var executor = await sp.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();
        var currentSdl = NormalizeSdl(executor.Schema.ToString());

        var snapshotPath = Path.Combine(AppContext.BaseDirectory, SnapshotRelativePath);
        if (!File.Exists(snapshotPath))
        {
            var repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../GraphQL/Snapshots/graphql-schema-golden.graphql"));
            if (File.Exists(repoPath))
            {
                snapshotPath = repoPath;
            }
        }

        File.Exists(snapshotPath).ShouldBeTrue($"Golden snapshot file not found at: {snapshotPath}");
        var goldenSdl = NormalizeSdl(await File.ReadAllTextAsync(snapshotPath));

        currentSdl.ShouldBe(goldenSdl, customMessage: "HotChocolate GraphQL SDL has changed unexpectedly after refactoring!");
    }
}

