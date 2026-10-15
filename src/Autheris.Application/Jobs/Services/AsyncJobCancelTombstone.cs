namespace Autheris.Application.Jobs.Services;

using System;

/// <summary>
/// Authoritative marker that a job was cancelled from another node. It lives under its own key so that later writes of
/// the owner node to the job record can never mask it; the owner checks it before every state transition.
/// </summary>
public sealed record AsyncJobCancelTombstone(string TenantId, string JobId, string RequesterSid, DateTimeOffset CancelledAt);
