namespace Autheris.Application.Jobs.Services;

using System.Security.Claims;
using Autheris.Domain.Common;

/// <summary>
/// Canonical resolution of the owner of an async query job. Submission, status, result download and cancellation all
/// compare owners resolved by this single method (the platform's canonical user SID resolution), so a token carrying
/// <c>PrimarySid</c>/<c>objectSid</c>/<c>oid</c> resolves to the same owner on every path.
/// </summary>
public static class AsyncJobOwner
{
    /// <summary>Returns the canonical owner identifier, or <c>null</c> when the principal carries no usable identity.</summary>
    public static string? Resolve(ClaimsPrincipal? user)
    {
        var sid = user.GetUserSid();
        return sid.HasValue && !string.IsNullOrWhiteSpace(sid.Value.Value) ? sid.Value.Value : null;
    }
}
