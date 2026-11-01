namespace Autheris.Tests.Parity.Channels;

using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Robin;

/// <summary>Calls the MCP query and aggregation tools against the parity host (PARITY-FU-001).</summary>
public sealed class McpChannel(IParityHost? host = null) : IParityChannel
{
    public string Name => "Mcp";

    public async Task<ChannelResult> ExecuteAsync(ParityQuery query, CancellationToken ct)
    {
        if (host is null)
        {
            return ChannelResult.CreatePending();
        }

        var robin = new RobinClient(host);
        if (query.RawSql is not null)
        {
            return await robin.McpAsync("query_sql", new { query = query.RawSql });
        }

        var graphql = GraphQlChannel.Render(query);
        if (graphql is null)
        {
            return ChannelResult.CreateNotExpressible();
        }

        var field = GraphQlChannel.FieldName(query.Table);
        return await robin.McpAsync("query_graphql", new { query = graphql }, field);
    }
}
