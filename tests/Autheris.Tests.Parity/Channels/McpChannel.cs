namespace Autheris.Tests.Parity.Channels;

/// <summary>MCP cells are registered as Pending in this phase (owner decision).</summary>
public sealed class McpChannel : IParityChannel
{
    public string Name => "Mcp";

    public Task<ChannelResult> ExecuteAsync(ParityQuery query, CancellationToken ct) => Task.FromResult(ChannelResult.CreatePending());
}
