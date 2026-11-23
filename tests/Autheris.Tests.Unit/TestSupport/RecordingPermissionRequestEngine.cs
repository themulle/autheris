namespace Autheris.Tests.Unit.TestSupport;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

/// <summary>Records what entry points hand to the engine; every submission becomes one PENDING request.</summary>
internal sealed class RecordingPermissionRequestEngine(int maxBundleItems = 50) : IPermissionRequestEngine
{
    public List<PermissionRequestSubmission> Submissions { get; } = [];

    public List<IReadOnlyList<PermissionRequestSubmission>> Bundles { get; } = [];

    public Task<PermissionSubmitResult> SubmitAsync(PermissionRequestSubmission submission, CancellationToken ct = default)
    {
        Submissions.Add(submission);
        return Task.FromResult(new PermissionSubmitResult(ToRequest(submission, submission.BundleId)));
    }

    public Task<PermissionSubmitResult> SubmitBundleAsync(
        ClaimsPrincipal caller,
        TenantId tenantId,
        IReadOnlyList<PermissionRequestSubmission> items,
        SecurityPrincipalContext? callerContext = null,
        CancellationToken ct = default)
    {
        if (items.Count > maxBundleItems)
        {
            throw new ConsentRequestException("BUNDLE_LIMIT_EXCEEDED", $"Bundle exceeds maximum of {maxBundleItems} items.");
        }

        Bundles.Add(items);
        var bundleId = Guid.NewGuid();
        var created = items.Select(i => ToRequest(i, bundleId)).ToList();
        Submissions.AddRange(items);
        return Task.FromResult(new PermissionSubmitResult(created[0], created, bundleId));
    }

    public Task<PermissionDecisionResult> DecideAsync(PermissionDecisionCommand command, CancellationToken ct = default) =>
        throw new NotSupportedException("Entry points under test never decide.");

    public Task<bool> WithdrawAsync(Guid requestId, ClaimsPrincipal caller, CancellationToken ct = default) => Task.FromResult(true);

    private static ConsentRequest ToRequest(PermissionRequestSubmission s, Guid? bundleId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = s.TenantId,
        TableIdentifier = s.Target.Table ?? default,
        TargetColumns = s.Target.Columns,
        RequesterSid = new Sid(s.GranteeRef),
        RequestedGranteeType = s.GranteeType,
        RequestedGranteeRef = s.GranteeRef,
        BusinessJustification = s.Justification ?? string.Empty,
        Status = "PENDING",
        RequestKind = s.RequestKind,
        BundleId = bundleId,
        Version = 1
    };
}
