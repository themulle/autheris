namespace Autheris.Application.Federation.Interfaces;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-OPS-03: Dynamic feature flagging and traffic splitting router for federated subgraphs.
/// </summary>
public interface ISubgraphCanaryRouter
{
    ValueTask<SubgraphRoutingDecision> ResolveTargetAsync(
        string subgraphName,
        Uri defaultUri,
        ClaimsPrincipal? principal,
        string? tenantId,
        string? variantHeader,
        CancellationToken ct = default);

    void RegisterRule(SubgraphCanaryRule rule);
    void RemoveRule(string ruleId);
}
