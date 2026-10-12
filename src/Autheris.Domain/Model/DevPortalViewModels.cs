namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

public sealed record DevPortal2FaEnrollmentViewModel(
    string UserSid,
    string Email,
    string Issuer,
    string SecretBase32,
    string FormattedSecret,
    string OtpAuthUri,
    string SvgQrCodeContent,
    string Nonce,
    string? ErrorMessage = null,
    string? SuccessMessage = null);

public sealed record DevPortalApprovalItem(
    string ApprovalId,
    string TenantId,
    string RequestedBy,
    string TargetResource,
    string Operation,
    string Reason,
    string CreatedAtIso,
    string ExpiresAtIso);

public sealed record DevPortalApprovalsViewModel(
    string TenantId,
    string ApproverUserSid,
    IReadOnlyList<DevPortalApprovalItem> PendingTickets,
    string Nonce,
    string? ErrorMessage = null,
    string? SuccessMessage = null);

public sealed record DevPortalStepUpApprovalRequest(
    string ApprovalId,
    string TotpCode,
    string? AntiforgeryToken = null);
