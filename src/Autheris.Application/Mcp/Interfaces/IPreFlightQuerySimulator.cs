namespace Autheris.Application.Mcp.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Pre-flight query simulation engine analyzing AST complexity, token volume, and hard safety limits for AI agents (F-AI-04).
/// </summary>
public interface IPreFlightQuerySimulator
{
    /// <summary>
    /// Simulates query execution against the schema and safety guardrails.
    /// </summary>
    Task<PreFlightQuerySimulationResult> SimulateQueryAsync(
        string query,
        TableIdentifier? targetTable = null,
        CancellationToken ct = default);
}
