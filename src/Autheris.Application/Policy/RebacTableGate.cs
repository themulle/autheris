namespace Autheris.Application.Policy;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// POL-6: the ReBAC <c>can_query</c> check on a catalog table, shared by every read path. The unified PDP (MCP-RAG,
/// DuckDB OLAP) always applies it while ReBAC is enabled; the query paths (OData, GraphQL, WebSQL, procedures) apply it
/// only with <see cref="RebacOptions.EnforceOnQueryPaths"/>, because the evaluator denies tables without tuples.
/// </summary>
public static class RebacTableGate
{
    public const string Relation = "can_query";

    public static string ObjectId(TableIdentifier table) => $"table:{table.Domain}.{table.TableName}";

    /// <summary>True when the query paths must check ReBAC for this configuration.</summary>
    public static bool IsEnforcedOnQueryPaths(GatewayOptions? options) =>
        options?.Rebac is { Enabled: true, EnforceOnQueryPaths: true };

    /// <summary>
    /// Evaluates <c>can_query</c> for the subject on the table. A missing or disabled evaluator denies (fail-closed):
    /// callers only invoke this when enforcement is configured.
    /// </summary>
    public static async ValueTask<bool> IsAllowedAsync(
        IRebacEvaluator? evaluator,
        TenantId tenant,
        Sid subject,
        TableIdentifier table,
        CancellationToken ct)
    {
        if (evaluator is not { IsEnabled: true })
        {
            return false;
        }

        var result = await evaluator.CheckAsync(new RebacCheckRequest(tenant.Value, subject.Value, Relation, ObjectId(table)), ct).ConfigureAwait(false);
        return result.Allowed;
    }
}
