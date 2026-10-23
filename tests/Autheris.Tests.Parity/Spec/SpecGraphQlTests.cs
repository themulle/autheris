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
/// Block F, GraphQL introspection sweep: every query root field is called once with its required arguments, then once per offered
/// argument with a generated valid minimal value. Each call must succeed or answer a stable 4xx code; a 5xx or leak is a hard failure
/// (SEC-PAR-05/07). Mutation and subscription roots are never called against the shared seed (follow-up PARITY-FU-002).
/// </summary>
[Trait("Category", "Parity")]
[Collection(ParityHostCollection.Name)]
public sealed class SpecGraphQlTests(ParityHostFixture host) : ParityTestBase
{
    private const string SampleSchema = """
        {"data":{"__schema":{"queryType":{"name":"Query"},"mutationType":{"name":"Mutation"},"subscriptionType":null,"types":[
        {"kind":"OBJECT","name":"Query","fields":[{"name":"things","args":[
          {"name":"first","defaultValue":null,"type":{"kind":"SCALAR","name":"Int","ofType":null}},
          {"name":"orderBy","defaultValue":null,"type":{"kind":"LIST","name":null,"ofType":{"kind":"NON_NULL","name":null,"ofType":{"kind":"INPUT_OBJECT","name":"Ord","ofType":null}}}},
          {"name":"mode","defaultValue":null,"type":{"kind":"NON_NULL","name":null,"ofType":{"kind":"ENUM","name":"Mode","ofType":null}}}],
          "type":{"kind":"OBJECT","name":"Thing","ofType":null}}],"inputFields":null,"enumValues":null},
        {"kind":"OBJECT","name":"Mutation","fields":[{"name":"drop","args":[],"type":{"kind":"SCALAR","name":"Boolean","ofType":null}}],"inputFields":null,"enumValues":null},
        {"kind":"INPUT_OBJECT","name":"Ord","fields":null,"inputFields":[{"name":"column","defaultValue":null,"type":{"kind":"NON_NULL","name":null,"ofType":{"kind":"SCALAR","name":"String","ofType":null}}},{"name":"dir","defaultValue":null,"type":{"kind":"SCALAR","name":"String","ofType":null}}],"enumValues":null},
        {"kind":"ENUM","name":"Mode","fields":null,"inputFields":null,"enumValues":[{"name":"FAST"},{"name":"SLOW"}]},
        {"kind":"OBJECT","name":"Thing","fields":[],"inputFields":null,"enumValues":null}]}}}
        """;

    [ParityFact]
    public void Spec_Sample_BuildsMinimalValidCalls()
    {
        var model = GraphQlSpecModel.Parse(SampleSchema);
        model.QueryRoot.ShouldBe("Query");
        var field = model.RootFields(model.QueryRoot).Single();
        model.Build(field, null).ShouldBe("{ things(first: 1, mode: FAST) { __typename } }");
        model.Build(field, field.Args.Single(a => a.Name == "first")).ShouldBe("{ things(mode: FAST, first: 1) { __typename } }");
        model.Build(field, field.Args.Single(a => a.Name == "orderBy")).ShouldBe("{ things(first: 1, mode: FAST, orderBy: [{column: \"x\"}]) { __typename } }");
        model.RootFields(model.MutationRoot).Select(f => f.Name).ShouldBe(["drop"]);
        model.RootFields(model.SubscriptionRoot).ShouldBeEmpty();
    }

    [ParityFact]
    public async Task Spec_GraphQlIntrospectionSweep()
    {
        var model = await LoadModelAsync();
        var fields = model.RootFields(model.QueryRoot).Where(f => f.Name.Contains("parity_", StringComparison.Ordinal)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        fields.Count.ShouldBeGreaterThan(0, "no query root fields");
        fields.ShouldContain(f => f.Name.Contains(SalesTable, StringComparison.Ordinal), "parity_sales is missing from the schema");

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
                var query = model.Build(field, arg);
                if (query is null)
                {
                    unbuildable++;
                    notes.Add($"inexpressible {key}: no valid minimal value can be generated");
                    continue;
                }

                calls++;
                var gap = SpecKnownGaps.ForField(key);
                var info = new ParityCellInfo("spec", "GraphQl", "field", key, GapId: gap);
                try
                {
                    await ParityCell.RunAsync(info, async () =>
                    {
                        var result = await PostAsync(query);
                        ParityAssert.NoServerError(result, query);
                        ParityAssert.StableCode(result);
                        if (ParityAssert.IsError(result) && result.Code?.StartsWith("REQUIRED_FILTER", StringComparison.Ordinal) == true && field.Name.Contains("scoped", StringComparison.Ordinal))
                        {
                            rejected++; // by design: the scoped table demands its filter column; a stable 4xx-class answer is the spec result
                            return;
                        }

                        if (ParityAssert.IsError(result))
                        {
                            rejected++;
                            throw new XunitException($"{key}: valid minimal call rejected, HTTP {result.Status} code '{result.Code}': {Snip(result.RawBody)} query: {query}");
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

        var mutations = model.RootFields(model.MutationRoot).Count;
        var subscriptions = model.RootFields(model.SubscriptionRoot).Count;
        Console.WriteLine($"spec-graphql: fields={fields.Count} calls={calls} ok={succeeded} rejected={rejected} inexpressible={unbuildable} mutations(skipped)={mutations} subscriptions(skipped)={subscriptions}");
        foreach (var n in notes)
        {
            Console.WriteLine($"spec-graphql: {n}");
        }

        foreach (var f in failures)
        {
            Console.WriteLine($"spec-graphql: FAIL {f}");
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures.Take(40)));
    }

    private const string SalesTable = "parity_sales";

    private async Task<GraphQlSpecModel> LoadModelAsync()
    {
        using var client = host.CreateClient();
        using var response = await client.PostAsJsonAsync("/graphql", new { query = GraphQlSpecModel.IntrospectionQuery });
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"introspection failed HTTP {(int)response.StatusCode}: {Snip(body)}");
        return GraphQlSpecModel.Parse(body);
    }

    private async Task<ChannelResult> PostAsync(string query)
    {
        using var client = host.CreateClient();
        using var response = await client.PostAsync("/graphql", new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        return new GraphQlBoundaryChannel().Parse((int)response.StatusCode, body);
    }

    private static string Snip(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
