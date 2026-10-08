namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

/// <summary>Connects the official MCP client to the gateway under test and calls tools with plain argument objects.</summary>
internal static class McpTestClient
{
    public static Task<McpClient> ConnectAsync(HttpClient http, string path = "/mcp") =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, path), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: false));

    /// <summary>Calls a tool; returns whether it reported an error and the text of its result.</summary>
    public static async Task<(bool IsError, string Text)> CallAsync(McpClient client, string tool, object arguments)
    {
        var json = JsonSerializer.SerializeToElement(arguments);
        var args = json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var result = await client.CallToolAsync(tool, args);
        return (result.IsError == true, string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    /// <summary>Like <see cref="CallAsync"/> but parses the result text as JSON (throws with the text when it is not JSON).</summary>
    public static async Task<(bool IsError, JsonElement Payload)> CallJsonAsync(McpClient client, string tool, object arguments)
    {
        var (isError, text) = await CallAsync(client, tool, arguments);
        if (!text.TrimStart().StartsWith('{'))
        {
            throw new InvalidOperationException($"Tool '{tool}' returned: {text}");
        }

        using var doc = JsonDocument.Parse(text);
        return (isError, doc.RootElement.Clone());
    }
}
