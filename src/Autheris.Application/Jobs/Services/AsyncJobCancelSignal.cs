namespace Autheris.Application.Jobs.Services;

/// <summary>
/// Cluster broadcast asking the node that runs a job to cancel it. The owner never trusts it blindly: tenant and
/// requester are verified against its local job entry before anything is cancelled.
/// </summary>
public sealed record AsyncJobCancelSignal(string TenantId, string JobId, string RequesterSid)
{
    public const string Channel = "asyncjob:cancel";
}
