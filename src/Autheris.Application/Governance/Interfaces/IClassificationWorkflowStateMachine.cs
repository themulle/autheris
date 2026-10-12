namespace Autheris.Application.Governance.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// State machine orchestrating transitions for classification proposals through 1-stage, 2-stage (4-eyes)
/// and zero-touch approval flows.
/// </summary>
public interface IClassificationWorkflowStateMachine
{
    /// <summary>
    /// Evaluates if a freshly generated proposal qualifies for zero-touch auto-approval.
    /// </summary>
    bool CanZeroTouchAutoApprove(TableClassificationProposal proposal);

    /// <summary>
    /// Executes a transition / sign-off step on an active proposal.
    /// </summary>
    ValueTask<TableClassificationApprovalResult> TransitionAsync(
        TableClassificationProposal proposal,
        TableClassificationApprovalRequest request,
        CancellationToken ct = default);
}
