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

    /// <summary>
    /// Architecture 1 / POL-11: the one ReBAC object id of a catalog table, fully qualified so equal schema and table
    /// names in two domains never share tuples. Used by every path (query paths, OLAP, unified PDP, streaming).
    /// </summary>
    public static string ObjectId(TableIdentifier table) => $"table:{table.Domain}.{table.Schema}.{table.TableName}";

    /// <summary>
    /// Canonical parent object id for a schema: schema:{Domain}.{Schema}.
    /// Domain qualification ensures equal schema names across different domains do not collide.
    /// </summary>
    public static string SchemaObjectId(string domain, string schema) => $"schema:{domain}.{schema}";

    /// <summary>
    /// Canonical parent object id for a schema belonging to a table identifier.
    /// </summary>
    public static string SchemaObjectId(TableIdentifier table) => SchemaObjectId(table.Domain, table.Schema);

    /// <summary>
    /// Canonical parent object id for a domain: domain:{Domain}.
    /// </summary>
    public static string DomainObjectId(string domain) => $"domain:{domain}";

    /// <summary>
    /// Canonical parent object id for the domain of a table identifier.
    /// </summary>
    public static string DomainObjectId(TableIdentifier table) => DomainObjectId(table.Domain);

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

        try
        {
            var result = await evaluator.CheckAsync(new RebacCheckRequest(tenant.Value, subject.Value, Relation, ObjectId(table)), ct).ConfigureAwait(false);
            return result.Allowed;
        }
        catch
        {
            return false;
        }
    }
}
