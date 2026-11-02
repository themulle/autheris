using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Invariant;
using Shouldly;

namespace Autheris.Tests.Parity.Spec;

/// <summary>
/// PARITY-FU-002: GraphQL subscription sweep. Verifies schema introspection of subscription roots,
/// minimal valid subscription generation, and transport behavior over WebSocket and HTTP.
/// Each call must succeed or answer a stable 4xx code; a 5xx or internal fault is a hard failure (SEC-PAR-05/07).
/// </summary>
[Trait("Category", "Parity")]
public sealed class SpecGraphQlSubscriptionTests : ParityTestBase
{
    [ParityFact]
    public async Task Spec_GraphQlSubscriptionSweep_IntrospectionAndTransportExecution()
    {
        await using var isolatedHost = await IsolatedParityHost.CreateAsync(enableSubscriptions: true);
        using var client = isolatedHost.CreateClient(roles: "ClusterAdmin");

        var model = await LoadModelAsync(client);
        var subRoot = model.SubscriptionRoot;
        subRoot.ShouldNotBeNull("Subscription root type is missing from GraphQL schema when subscriptions are enabled");

        var fields = model.RootFields(subRoot).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        fields.Count.ShouldBe(1, "Expected exactly 1 subscription root field (onTableChanged)");
        fields[0].Name.ShouldBe("onTableChanged");

        // 1. Build minimal subscription query
        var subQuery = model.Build(fields[0], null, "subscription");
        subQuery.ShouldNotBeNull("Minimal subscription query could not be built");
        subQuery.ShouldStartWith("subscription { onTableChanged(");

        // 2. HTTP transport verification: standard HTTP POST of a subscription operation
        // Must fail closed with a stable code (e.g. 4xx / rejection), never a 5xx or server leak.
        using var response = await client.PostAsync("/graphql", new StringContent(JsonSerializer.Serialize(new { query = subQuery }), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        var result = new GraphQlBoundaryChannel().Parse((int)response.StatusCode, body);
        ParityAssert.NoServerError(result, subQuery);

        // 3. WebSocket transport verification: connection_init over graphql-transport-ws
        var wsClient = isolatedHost.Factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
        {
            req.Headers["X-Test-User-Sid"] = "S-1-5-PARITY";
            req.Headers["X-Test-Roles"] = "ClusterAdmin";
            req.Headers["X-Test-Tenant"] = "tenant_parity";
            req.Headers["Sec-WebSocket-Protocol"] = "graphql-transport-ws";
        };

        var wsUri = new Uri(isolatedHost.Factory.Server.BaseAddress, "/graphql");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var ws = await wsClient.ConnectAsync(wsUri, cts.Token);

        ws.State.ShouldBe(WebSocketState.Open, "WebSocket should connect to /graphql");

        // Send connection_init
        var initJson = """{"type":"connection_init","payload":{}}""";
        var initBytes = Encoding.UTF8.GetBytes(initJson);
        await ws.SendAsync(new ArraySegment<byte>(initBytes), WebSocketMessageType.Text, true, cts.Token);

        // Read connection_ack or error
        var buffer = new byte[4096];
        var receiveResult = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
        var receivedText = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);

        ParityAssert.NoLeak(receivedText);
        receivedText.ShouldContain("connection_ack");

        // Graceful close
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test done", cts.Token);
    }

    private static async Task<GraphQlSpecModel> LoadModelAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query = GraphQlSpecModel.IntrospectionQuery });
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"introspection failed HTTP {(int)response.StatusCode}: {Snip(body)}");
        return GraphQlSpecModel.Parse(body);
    }

    private static string Snip(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
