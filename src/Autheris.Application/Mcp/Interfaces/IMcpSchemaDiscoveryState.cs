namespace Autheris.Application.Mcp.Interfaces;

/// <summary>Phase of the MCP schema discovery. <see cref="Pending"/> is the initial value; anything but <see cref="Ready"/> maps to 503.</summary>
public enum McpSchemaDiscoveryPhase
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
}

/// <summary>
/// Issue #1109: live view of the MCP schema discovery for <c>/health/ready</c>. The discovery runs in the background after
/// host start, so the instance is not ready until the GraphQL executor is built and its MCP tools are registered.
/// </summary>
public interface IMcpSchemaDiscoveryState
{
    McpSchemaDiscoveryPhase Phase { get; }
}
