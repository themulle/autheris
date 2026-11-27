using System.Security.Claims;
using Autheris.Application.Services;
using Autheris.Domain.Common;

namespace Autheris.Application.Sql.Tree;

public sealed partial class GovernedTreeQueryService
{
    public IReadOnlyList<DatasetAccessFacts> GetDatasetFacts(ClaimsPrincipal? principal, TreeQueryNode root, string operationId)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return [];
        }

        var tenant = principal.GetTenantId().Value;
        var userSid = principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userSid))
        {
            userSid = "anonymous";
        }

        if (!_memos.TryGetValue($"{operationId}|{tenant}|{userSid}", out var memo))
        {
            return [];
        }

        var facts = new List<DatasetAccessFacts>();
        return TryCollectFacts(root, [], memo, tenant, userSid, facts) ? facts : [];
    }

    private static bool TryCollectFacts(
        TreeQueryNode node,
        IReadOnlyList<string> path,
        OperationMemo memo,
        string tenant,
        string userSid,
        List<DatasetAccessFacts> facts)
    {
        if (!memo.AccessByTable.TryGetValue((node.Table, tenant, userSid), out var access) || !access.Decision.IsAllowed)
        {
            return false;
        }

        facts.Add(DatasetAccessFacts.From(node.Table, path, access.Decision));
        foreach (var relation in node.Relations)
        {
            if (!TryCollectFacts(relation.Child, [.. path, relation.ResponseKey], memo, tenant, userSid, facts))
            {
                return false;
            }
        }

        return true;
    }
}
