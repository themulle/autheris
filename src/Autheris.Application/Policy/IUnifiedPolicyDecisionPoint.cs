namespace Autheris.Application.Policy;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Architecture Phase 2 (WP 2.2): Unified Policy Decision Point orchestrating ReBAC, Casbin ABAC,
/// and Consent Engine evaluation into a single authoritative TableAccessDecision.
/// </summary>
public interface IUnifiedPolicyDecisionPoint
{
    Task<TableAccessDecision> EvaluateAccessAsync(
        TableIdentifier table,
        TableMetadata metadata,
        SecurityPrincipalContext securityContext,
        IReadOnlyList<string>? requestedColumns,
        CancellationToken ct);
}
