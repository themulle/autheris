using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Shouldly;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Spec;

/// <summary>
/// PARITY-FU-002: GraphQL mutation sweep. Every mutation root field is called with its required arguments,
/// then once per offered argument with a generated valid minimal value against an isolated copy of the seed.
/// Each call must succeed or answer a stable 4xx code; a 5xx or internal fault is a hard failure (SEC-PAR-05/07).
/// </summary>
[Trait("Category", "Parity")]
public sealed class SpecGraphQlMutationTests : ParityTestBase
{
    [ParityFact]
    public async Task Spec_GraphQlMutationSweep_AllMutationsExecutedAgainstIsolatedHost()
    {
        await using var isolatedHost = await IsolatedParityHost.CreateAsync();
        using var client = isolatedHost.CreateClient(roles: "ClusterAdmin");

        var model = await LoadModelAsync(client);
        var mutationRoot = model.MutationRoot;
        mutationRoot.ShouldNotBeNull("Mutation root type is missing from GraphQL schema");

        var fields = model.RootFields(mutationRoot).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        fields.Count.ShouldBe(9, "Expected exactly 9 mutation root fields");

        var failures = new List<string>();
        var notes = new List<string>();
        var calls = 0;
        var succeeded = 0;
        var rejected = 0;
        var unbuildable = 0;

        foreach (var field in fields)
        {
            var cells = new List<(string Key, GqlInput? Arg)> { (field.Name, null) };
            cells.AddRange(field.Args.Select(a => ($"{field.Name}.{a.Name}", (GqlInput?)a)));

            foreach (var (key, arg) in cells)
            {
                var query = model.Build(field, arg, "mutation");
                if (query is null)
                {
                    unbuildable++;
                    notes.Add($"inexpressible mutation {key}: no valid minimal value can be generated");
                    continue;
                }

                calls++;
                var gap = SpecKnownGaps.ForField(key);
                var info = new ParityCellInfo("spec", "GraphQlMutation", "field", key, GapId: gap);
                try
                {
                    await ParityCell.RunAsync(info, async () =>
                    {
                        var result = await PostAsync(client, query);
                        ParityAssert.NoServerError(result, query);
                        ParityAssert.StableCode(result);

                        if (ParityAssert.IsError(result))
                        {
                            // A stable rejection (e.g. DECISION_NOT_PERMITTED, NOT_FOUND, ACCESS_REQUEST_NOT_PENDING) is a valid spec contract result
                            rejected++;
                            return;
                        }

                        succeeded++;
                    });
                }
                catch (KnownGapObservedException ex)
                {
                    notes.Add($"gap {ex.Message}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"{key}: {Snip(ex.Message)}");
                }
            }
        }

        Console.WriteLine($"spec-graphql-mutations: fields={fields.Count} calls={calls} ok={succeeded} rejected={rejected} inexpressible={unbuildable}");
        foreach (var n in notes)
        {
            Console.WriteLine($"spec-graphql-mutations: {n}");
        }

        foreach (var f in failures)
        {
            Console.WriteLine($"spec-graphql-mutations: FAIL {f}");
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures.Take(40)));
    }

    private static async Task<GraphQlSpecModel> LoadModelAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query = GraphQlSpecModel.IntrospectionQuery });
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"introspection failed HTTP {(int)response.StatusCode}: {Snip(body)}");
        return GraphQlSpecModel.Parse(body);
    }

    private static async Task<ChannelResult> PostAsync(HttpClient client, string query)
    {
        using var response = await client.PostAsync("/graphql", new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        return new GraphQlBoundaryChannel().Parse((int)response.StatusCode, body);
    }

    private static string Snip(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
