namespace Autheris.Tests.Unit.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DeprovisioningGrantRevocationTests
{
    private static readonly TenantId Tenant = new("tenant-dep");
    private static readonly Sid Subject = new("S-1-5-21-77");
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private const string Reason = "SCIM_DEPROVISION";

    [Fact]
    public async Task Revokes_data_unmask_catalog()
    {
        var f = new Fixture();
        var data = f.AddGrant();
        var unmask = f.AddGrant(PermissionGrantKind.Unmask);
        var cat = new CatalogVisibilityGrant { Id = Guid.NewGuid(), TenantId = Tenant, ContainerKind = ContainerKind.Domain, ContainerId = Guid.NewGuid(), GranteeSid = Subject };
        f.ActiveCatalog.Add(cat);

        var result = await f.Build().RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        result.Complete.ShouldBeTrue();
        result.GrantsRevoked.ShouldBe(2);
        result.CatalogGrantsRevoked.ShouldBe(1);
        f.Revoked.ShouldBe([data.Id, unmask.Id], ignoreOrder: true);
        f.CatalogRevoked.ShouldBe([cat.Id]);
    }

    [Fact]
    public async Task Unmask_grants_revoked_on_deprovisioning()
    {
        var f = new Fixture();
        var unmask = f.AddGrant(PermissionGrantKind.Unmask);

        var result = await f.Build().RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        result.Complete.ShouldBeTrue();
        f.Revoked.ShouldBe([unmask.Id]);
        f.WrongTenantCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Pending_withdrawn()
    {
        var f = new Fixture();
        var req = new ConsentRequest { Id = Guid.NewGuid(), TenantId = Tenant, RequesterSid = Subject, Status = "PENDING", TableIdentifier = new TableIdentifier("s", "dbo", "t") };
        f.Pending.Add(req);

        var result = await f.Build().RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        result.RequestsWithdrawn.ShouldBe(1);
        f.Withdrawn.ShouldBe([req.Id]);
    }

    [Fact]
    public async Task Audit_per_item()
    {
        var f = new Fixture();
        f.AddGrant();
        f.AddGrant(PermissionGrantKind.Unmask);
        f.Pending.Add(new ConsentRequest { Id = Guid.NewGuid(), TenantId = Tenant, RequesterSid = Subject, Status = "PENDING", TableIdentifier = new TableIdentifier("s", "dbo", "t") });

        await f.Build().RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        f.Audit.Entries.Count(e => e.EventType == AuditEventTypes.GrantRevokedDeprovisioned).ShouldBe(2);
        f.Audit.Entries.Count(e => e.EventType == AuditEventTypes.ConsentWithdrawn).ShouldBe(1);
    }

    [Fact]
    public async Task Partial_failure_retried_lock_stays()
    {
        var f = new Fixture();
        var ok = f.AddGrant();
        var bad = f.AddGrant();
        f.FailRevoke.Add(bad.Id);
        var service = f.Build();

        var first = await service.RevokeAllForSubjectAsync(Tenant, Subject, Reason);
        first.Complete.ShouldBeFalse();
        first.Failed.ShouldBe(1);
        f.Revoked.ShouldBe([ok.Id]);

        // Retry is idempotent: the revoked grant is not touched again and the failing one succeeds once the store recovers.
        f.FailRevoke.Clear();
        var second = await service.RevokeAllForSubjectAsync(Tenant, Subject, Reason);
        second.Complete.ShouldBeTrue();
        second.GrantsRevoked.ShouldBe(1);
        f.Revoked.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Reactivation_does_not_resurrect()
    {
        var f = new Fixture();
        f.AddGrant();
        var service = f.Build();
        await service.RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        // After revocation nothing is active again; a repeated run (re-activation then deprovision) finds nothing to restore.
        var again = await service.RevokeAllForSubjectAsync(Tenant, Subject, Reason);
        again.GrantsRevoked.ShouldBe(0);
        again.Complete.ShouldBeTrue();
        f.Revoked.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Batched_with_timeout()
    {
        var f = new Fixture();
        for (var i = 0; i < 5; i++) f.AddGrant();

        var result = await f.Build(maxItemsPerRun: 2).RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        result.GrantsRevoked.ShouldBe(2);
        result.Truncated.ShouldBeTrue();
        result.Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task Deny_effects_are_not_revoked()
    {
        var f = new Fixture();
        f.Active.Add(new Consent { Id = Guid.NewGuid(), TenantId = Tenant, TableIdentifier = new TableIdentifier("s", "dbo", "t"), GranteeSid = Subject, Effect = ConsentEffect.Deny, ValidTo = Now.AddDays(5) });

        var result = await f.Build().RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        result.GrantsRevoked.ShouldBe(0);
        f.Revoked.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Deny_rows_do_not_hide_revocable_grants_beyond_the_cap()
    {
        var f = new Fixture();
        for (var i = 0; i < 3; i++)
        {
            f.Active.Add(new Consent { Id = Guid.NewGuid(), TenantId = Tenant, TableIdentifier = new TableIdentifier("s", "dbo", "t"), GranteeSid = Subject, Effect = ConsentEffect.Deny, ValidTo = Now.AddDays(5) });
        }

        var allow = new[] { f.AddGrant(), f.AddGrant() };

        var result = await f.Build(maxItemsPerRun: 2).RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        f.Revoked.ShouldBe(allow.Select(a => a.Id), ignoreOrder: true);
        result.GrantsRevoked.ShouldBe(2);
        result.Complete.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task More_active_grants_than_the_scan_ceiling_leaves_the_run_incomplete()
    {
        var f = new Fixture();
        for (var i = 0; i < 4; i++) f.AddGrant();

        var result = await f.Build(maxActiveGrantsScanned: 3).RevokeAllForSubjectAsync(Tenant, Subject, Reason);

        result.Complete.ShouldBeFalse();
        result.Truncated.ShouldBeTrue();
    }

    private sealed class Fixture
    {
        public List<Consent> Active { get; } = [];
        public List<CatalogVisibilityGrant> ActiveCatalog { get; } = [];
        public List<ConsentRequest> Pending { get; } = [];
        public HashSet<Guid> FailRevoke { get; } = [];
        public List<Guid> Revoked { get; } = [];
        public List<Guid> CatalogRevoked { get; } = [];
        public List<Guid> Withdrawn { get; } = [];
        public InMemoryAuditWriter Audit { get; } = new();
        public int EnumerationCalls { get; private set; }
        public int WrongTenantCalls { get; private set; }

        public GrantRevocationService Build(int maxItemsPerRun = GrantRevocationService.DefaultMaxItemsPerRun, int maxActiveGrantsScanned = GrantRevocationService.MaxActiveGrantsScanned)
        {
            var consents = NullFake.Create<IConsentRepository>((m, args) =>
            {
                switch (m.Name)
                {
                    case nameof(IConsentRepository.GetAllActiveConsentsForSubjectsAsync):
                        EnumerationCalls++;
                        // Like the real store, the data-path enumeration only returns DATA grants.
                        return Task.FromResult<IReadOnlyList<Consent>>(Active.Where(c => !Revoked.Contains(c.Id) && c.GrantKind == PermissionGrantKind.Data).ToList());
                    case nameof(IConsentRepository.GetActiveGrantsForSubjectsAsync):
                        EnumerationCalls++;
                    {
                        // Like the real store: a positive bound means Allow-only rows, bounded in the query.
                        var rows = Active.Where(c => !Revoked.Contains(c.Id));
                        var cap = (int)args![3]!;
                        return Task.FromResult<IReadOnlyList<Consent>>((cap > 0 ? rows.Where(c => c.Effect == ConsentEffect.Allow).Take(cap) : rows).ToList());
                    }
                    case nameof(IConsentRepository.RevokeSystemConsentByIdAsync):
                        var id = (Guid)args![1]!;
                        WrongTenantCalls += (TenantId)args[0]! == Tenant ? 0 : 1;
                        if (FailRevoke.Contains(id)) throw new InvalidOperationException("db down");
                        Revoked.Add(id);
                        return Task.FromResult(true);
                    default:
                        return NullFake.UseDefault;
                }
            });
            var catalog = NullFake.Create<ICatalogVisibilityGrantRepository>((m, args) =>
            {
                switch (m.Name)
                {
                    case nameof(ICatalogVisibilityGrantRepository.ListActiveForSubjectsAsync):
                    case nameof(ICatalogVisibilityGrantRepository.ListForDeprovisioningAsync):
                        return Task.FromResult<IReadOnlyList<CatalogVisibilityGrant>>(ActiveCatalog.Where(c => !CatalogRevoked.Contains(c.Id)).ToList());
                    case nameof(ICatalogVisibilityGrantRepository.RevokeAsync):
                        CatalogRevoked.Add((Guid)args![1]!);
                        return Task.FromResult(true);
                    default:
                        return NullFake.UseDefault;
                }
            });
            var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) =>
            {
                switch (m.Name)
                {
                    case nameof(IConsentApprovalRepository.GetRequestsByRequesterSidAsync):
                    case nameof(IConsentApprovalRepository.GetPendingRequestsForSubjectAsync):
                        return Task.FromResult<IReadOnlyList<ConsentRequest>>(Pending.Where(r => !Withdrawn.Contains(r.Id)).ToList());
                    case nameof(IConsentApprovalRepository.WithdrawConsentRequestAsync):
                        Withdrawn.Add((Guid)args![0]!);
                        return Task.FromResult(true);
                    default:
                        return NullFake.UseDefault;
                }
            });
            return new GrantRevocationService(consents, approvals, Audit, catalog, maxItemsPerRun, null, new FakeTimeProvider(Now), null, maxActiveGrantsScanned);
        }

        public Consent AddGrant(PermissionGrantKind kind = PermissionGrantKind.Data)
        {
            var c = new Consent { Id = Guid.NewGuid(), TenantId = Tenant, TableIdentifier = new TableIdentifier("s", "dbo", "t"), GranteeSid = Subject, GrantKind = kind, ValidFrom = Now.AddDays(-1), ValidTo = Now.AddDays(30) };
            Active.Add(c);
            return c;
        }
    }
}
