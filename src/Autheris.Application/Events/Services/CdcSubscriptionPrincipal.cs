namespace Autheris.Application.Events.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>Why a creator snapshot could not be captured.</summary>
public enum CdcSnapshotCaptureError
{
    None = 0,
    MissingSid,
    TooLarge
}

/// <summary>Why <see cref="CdcSubscriptionPrincipal.Evaluate"/> denied.</summary>
public enum CdcSnapshotDenial
{
    None = 0,
    Missing,
    Expired
}

/// <summary>Result of capturing the creator principal of a CDC webhook subscription.</summary>
public sealed record CdcSnapshotCapture(string? Sid, IReadOnlyList<string>? GroupSids, IReadOnlyList<string>? Roles, CdcSnapshotCaptureError Error)
{
    public bool IsSuccess => Error == CdcSnapshotCaptureError.None;
}

/// <summary>
/// Captures the creating admin's principal at subscription creation and rebuilds a minimal <see cref="ClaimsPrincipal"/> from it for
/// delivery-time authorisation. The tenant claim always comes from the subscription itself, never from the snapshot.
/// </summary>
public static class CdcSubscriptionPrincipal
{
    public const string AuthenticationType = "cdc-webhook-snapshot";
    public const string TenantClaimType = "tenant_id";

    public static CdcSnapshotCapture Capture(ClaimsPrincipal? admin, CdcWebhooksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sid = admin.GetUserSid();
        if (sid is null || string.IsNullOrWhiteSpace(sid.Value.Value))
        {
            return new CdcSnapshotCapture(null, null, null, CdcSnapshotCaptureError.MissingSid);
        }

        var groups = admin.GetGroupSids().Select(g => g.Value).Distinct(StringComparer.Ordinal).ToList();
        if (groups.Count > options.MaxSnapshotGroups)
        {
            return new CdcSnapshotCapture(null, null, null, CdcSnapshotCaptureError.TooLarge);
        }

        var roles = admin.GetUserRoles().ToList();
        return new CdcSnapshotCapture(sid.Value.Value, groups, roles, CdcSnapshotCaptureError.None);
    }

    /// <summary>Returns <c>null</c> (deny) when the snapshot is missing, incomplete or older than <see cref="CdcWebhooksOptions.CreatorSnapshotMaxAgeDays"/>.</summary>
    public static ClaimsPrincipal? TryBuild(CloudEventWebhookSubscription subscription, TimeProvider time, CdcWebhooksOptions options)
    {
        Evaluate(subscription, time, options, out var principal);
        return principal;
    }

    /// <summary>Like <see cref="TryBuild"/>, but tells an expired snapshot from a missing one.</summary>
    public static CdcSnapshotDenial Evaluate(CloudEventWebhookSubscription subscription, TimeProvider time, CdcWebhooksOptions options, out ClaimsPrincipal? principal)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        principal = null;

        if (string.IsNullOrWhiteSpace(subscription.CreatedBySid)
            || string.IsNullOrWhiteSpace(subscription.TenantId)
            || subscription.CreatedByGroupSids is null
            || subscription.CreatedByRoles is null
            || subscription.CreatedAt is null)
        {
            return CdcSnapshotDenial.Missing;
        }

        if (options.CreatorSnapshotMaxAgeDays > 0
            && time.GetUtcNow() - subscription.CreatedAt.Value > TimeSpan.FromDays(options.CreatorSnapshotMaxAgeDays))
        {
            return CdcSnapshotDenial.Expired;
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, subscription.CreatedBySid),
            new(TenantClaimType, subscription.TenantId)
        };
        claims.AddRange(subscription.CreatedByGroupSids.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => new Claim(ClaimTypes.GroupSid, g)));
        claims.AddRange(subscription.CreatedByRoles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => new Claim(ClaimTypes.Role, r)));

        principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
        return CdcSnapshotDenial.None;
    }
}
