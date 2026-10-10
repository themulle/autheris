namespace Autheris.Application.Governance.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Hook interface allowing custom external C# plugins (.dll assemblies) to intercept
/// and customize governance classification proposals and workflow transitions.
/// </summary>
public interface IGovernanceWorkflowHook
{
    /// <summary>
    /// Invoked before table classification occurs, allowing programmatic overrides of columns or owners.
    /// </summary>
    ValueTask OnBeforeClassifyAsync(
        TableClassificationContext context,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <summary>
    /// Invoked when a workflow transition occurs on a proposal.
    /// </summary>
    ValueTask OnWorkflowTransitionAsync(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        TableClassificationApprovalResult result,
        CancellationToken ct = default) => ValueTask.CompletedTask;
}
