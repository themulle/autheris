using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Notifications;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;

namespace Autheris.Tests.Unit.Governance.Requests;

internal sealed class RecordingNotificationDispatcher : IGovernanceNotificationDispatcher
{
    public List<GovernanceNotificationMessage> Messages { get; } = [];
    public Exception? Failure { get; set; }

    public Task DispatchAsync(GovernanceNotificationMessage message, CancellationToken ct = default)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Messages.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class StubGrantApplier(ConsentRequestKind kind, bool fail = false) : IGrantEffectApplier
{
    public ConsentRequestKind RequestKind { get; } = kind;
    public int Applied { get; private set; }
    public ApprovedPermission? Last { get; private set; }

    public Task<IReadOnlyList<GrantRef>> ApplyAsync(IGovernanceTransaction transaction, ApprovedPermission approved, CancellationToken ct = default)
    {
        if (fail)
        {
            throw new InvalidOperationException("Simulated grant write failure.");
        }

        Applied++;
        Last = approved;
        return Task.FromResult<IReadOnlyList<GrantRef>>([new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data)]);
    }
}

/// <summary>Wires the real engine, resolver, eligibility, SoD and grant policy around in-memory fakes for self-grant and renewal tests.</summary>
internal sealed class SelfGrantHarness
{
    public static readonly TenantId Tenant = new("tenant-a");
    public static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");

    public InMemoryAuditWriter Audit { get; }
    public RecordingNotificationDispatcher Notifications { get; } = new();
    public StubGrantApplier DataApplier { get; }
    public StubGrantApplier UnmaskApplier { get; }
    public List<ConsentRequest> Created { get; } = [];
    public List<Guid> Deleted { get; } = [];
    public Consent? Existing { get; set; }
    public List<Consent> CreatedConsents { get; } = [];
    public List<Guid> RevokedConsents { get; } = [];
    public Func<Guid, bool>? RevokeReturnsFalse { get; set; }
    public List<TenantId> RevokedForTenants { get; } = [];
    public Func<Guid, bool>? FailRevokeConsent { get; set; }
    public List<SystemRevocationKind> RevocationKinds { get; } = [];

    /// <summary>When set, a system revocation that neither fails nor returns false runs against this repository.</summary>
    public IConsentRepository? RevokeThrough { get; set; }
    public Func<Guid, Consent?>? ConsentLookup { get; set; }
    public RecordingConsentEventOutbox Events { get; } = new();
    public System.Data.Common.DbTransaction Transaction { get; } = new StubDbTransaction();
    public PermissionRequestEngine Engine { get; }
    public SelfGrantService Service { get; }

    public SelfGrantHarness(
        ProtectionClass tableClass = ProtectionClass.Internal,
        ProtectionClass? columnClass = null,
        Sid? ownerGroup = null,
        Exception? auditFailure = null,
        bool grantWriteFails = false,
        TenantAccessPolicy? tenantPolicy = null)
    {
        Audit = new InMemoryAuditWriter(auditFailure);
        DataApplier = new StubGrantApplier(ConsentRequestKind.Data, grantWriteFails);
        UnmaskApplier = new StubGrantApplier(ConsentRequestKind.Unmask, grantWriteFails);

        var columns = new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase);
        if (columnClass is { } cc)
        {
            columns["ssn"] = new ColumnProtection(cc, null);
        }

        var protection = new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            ownerGroup,
            tableClass,
            tableClass,
            null,
            columns,
            default);
        var provider = NullFake.Create<IProtectionSnapshotProvider>((m, _) => new ValueTask<EffectiveProtection>(protection));

        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
        {
            switch (m.Name)
            {
                case nameof(IConsentApprovalRepository.CreateConsentRequestAsync):
                    Created.Add((ConsentRequest)args![0]!);
                    return Task.FromResult((ConsentRequest)args[0]!);
                case nameof(IConsentApprovalRepository.DeleteConsentRequestAsync):
                    Deleted.Add((Guid)args![0]!);
                    return Task.CompletedTask;
                case nameof(IConsentApprovalRepository.TryDecideAsync):
                    var id = (Guid)args![0]!;
                    var decide = (Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>>)args[2]!;
                    var apply = (Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>>)args[3]!;
                    return Task.Run(async () =>
                    {
                        var req = Created.Find(r => r.Id == id)!;
                        var tx = NullFake.Create<IGovernanceTransaction>((tm, _) =>
                            tm.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? Transaction : NullFake.UseDefault);
                        var record = await decide(tx, req);
                        var updated = Decided(req, record);
                        var grants = await apply(tx, record, updated);
                        return new DecisionCasResult(DecisionCasOutcome.Applied, updated, grants);
                    });
                default:
                    return NullFake.UseDefault;
            }
        });

        IAccessPolicySettingsProvider? policyProvider = tenantPolicy is null
            ? null
            : NullFake.Create<IAccessPolicySettingsProvider>((m, _) => Task.FromResult(tenantPolicy));
        var capabilities = new GovernanceCapabilities();
        var consentRepo = NullFake.Create<IConsentRepository>((m, args) =>
        {
            switch (m.Name)
            {
                case nameof(IConsentRepository.GetConsentByIdAsync):
                    return Task.FromResult(Existing is not null && Existing.Id == (Guid)args![0]! ? Existing : null);
                case nameof(IConsentRepository.CreateConsentAsync):
                    CreatedConsents.Add((Consent)args![0]!);
                    return Task.FromResult((Consent)args[0]!);
                case nameof(IConsentRepository.RevokeSystemConsentByIdAsync):
                    if (FailRevokeConsent is not null && FailRevokeConsent((Guid)args![1]!))
                    {
                        throw new InvalidOperationException("revoke failed");
                    }

                    if (RevokeReturnsFalse is not null && RevokeReturnsFalse((Guid)args![1]!))
                    {
                        return Task.FromResult(false);
                    }

                    RevokedConsents.Add((Guid)args![1]!);
                    RevokedForTenants.Add((TenantId)args[0]!);
                    RevocationKinds.Add((SystemRevocationKind)args[4]!);
                    return RevokeThrough is null
                        ? Task.FromResult(true)
                        : RevokeThrough.RevokeSystemConsentByIdAsync(
                            (TenantId)args[0]!, (Guid)args[1]!, (Sid)args[2]!, (string)args[3]!, (SystemRevocationKind)args[4]!, (CancellationToken)args[5]!);
                default:
                    return NullFake.UseDefault;
            }
        });

        Engine = new PermissionRequestEngine(
            approvals,
            Audit,
            new ApproverResolver(protectionSnapshotProvider: provider),
            new ApproverEligibility(capabilities),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [DataApplier, UnmaskApplier],
            protectionProvider: provider,
            accessPolicyProvider: policyProvider,
            notificationDispatcher: Notifications,
            capabilities: capabilities,
            timeProvider: new FakeTimeProvider(Now),
            consentRepository: consentRepo,
            eventOutbox: Events);
        Service = new SelfGrantService(Engine, Audit, new FakeTimeProvider(Now));
    }

    /// <summary>The request row as the decision CAS leaves it: the decided status and the version after the change.</summary>
    private static ConsentRequest Decided(ConsentRequest r, PermissionDecisionRecord record) => new()
    {
        Id = r.Id,
        TableId = r.TableId,
        TableIdentifier = r.TableIdentifier,
        RequesterSid = r.RequesterSid,
        RequesterIdentifiers = r.RequesterIdentifiers,
        RequestedGranteeType = r.RequestedGranteeType,
        RequestedGranteeRef = r.RequestedGranteeRef,
        BusinessJustification = r.BusinessJustification,
        Status = record.NewStatus,
        RequestedAt = r.RequestedAt,
        RequestedValidFrom = r.RequestedValidFrom,
        RequestedValidTo = record.ApprovedValidTo,
        UpdatedAt = record.DecidedAt,
        TenantId = r.TenantId,
        Version = r.Version + 1,
        WorkflowMode = r.WorkflowMode,
        ExternalSystemId = r.ExternalSystemId,
        RequestKind = r.RequestKind,
        GrantKind = r.GrantKind,
        TargetColumns = r.TargetColumns,
        ContainerKind = r.ContainerKind,
        ContainerId = r.ContainerId,
        ApproverCapacity = record.Capacity,
        SelfGrant = record.SelfGrant,
        DecidedBy = record.DecidedBy,
        DecidedAt = record.DecidedAt
    };

    public static SecurityPrincipalContext Actor(string sid, string role = "TenantAdmin", IEnumerable<string>? groups = null, bool servicePrincipal = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tenant_id", Tenant.Value),
            new(ClaimTypes.Role, role)
        };
        if (servicePrincipal)
        {
            claims.Add(new Claim("idtyp", "app"));
        }

        var groupSids = new HashSet<Sid>();
        foreach (var g in groups ?? [])
        {
            groupSids.Add(new Sid(g));
        }

        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            TenantId = Tenant,
            GroupSids = groupSids,
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>([role], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"))
        };
    }

    public static SelfGrantCommand Command(
        SecurityPrincipalContext actor,
        PermissionKind kind = PermissionKind.Data,
        int days = 30,
        string justification = "Investigating a production incident on orders.",
        GranteeType granteeType = GranteeType.User,
        string? grantee = null) =>
        new(actor, kind, PermissionTarget.ForTable(Orders), granteeType, grantee ?? actor.UserSid.Value, Now.AddDays(days), justification);
}
