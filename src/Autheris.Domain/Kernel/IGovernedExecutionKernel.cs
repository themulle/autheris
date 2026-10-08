namespace Autheris.Domain.Kernel;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Security;

/// <summary>
/// Architecture Phase 2: Central Governed Data Pipeline Kernel.
/// Enforces canonical normalization, unified PDP evaluation, resource guardrails, and post-execution masking
/// identically across all protocols (Relational SQL, DuckDB OLAP, Arrow export).
/// </summary>
public interface IGovernedExecutionKernel
{
    Task<Autheris.Domain.Model.GovernedVectorResult> ExecuteVectorQueryAsync(
        Autheris.Domain.Model.VectorSearchRequest request,
        SecurityPrincipalContext securityContext,
        CancellationToken ct = default);
}
