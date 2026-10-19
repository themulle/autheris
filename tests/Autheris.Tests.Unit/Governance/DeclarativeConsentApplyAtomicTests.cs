using System.Security.Claims;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>WP-SC-02 / SEC-SC-03: manifest role idempotency and atomic, single-transaction apply.</summary>
public sealed class DeclarativeConsentApplyAtomicTests
{
    private static readonly TableIdentifier Orders = new("default", "sales", "orders");
    private static readonly TableIdentifier Items = new("default", "sales", "items");

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")], "test"));

    private static ITableMetadataRepository Tables()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Autheris.Domain.Model.Table { Id = Guid.NewGuid() },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }]
            });
        return repo;
    }

    private static DeclarativeConsentPolicy RolePolicy(string table = "orders", string role = "DataScientist") =>
        new("default", "sales", table, "Role", role, "Allow");

    [Fact]
    public async Task SEC_SC_03_RoleManifest_AppliedTwice_SecondApplyIsUnchanged()
    {
        var repo = new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, Substitute.For<IAuditLogRepository>());
        var manifest = new DeclarativeConsentManifest("1", [RolePolicy()]);

        var first = await svc.ApplyManifestAsync(Admin(), manifest);
        var second = await svc.ApplyManifestAsync(Admin(), manifest);

        first.Applied.ShouldBe(1);
        second.Applied.ShouldBe(0);
        second.Unchanged.ShouldBe(1);
        second.Revoked.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).Count.ShouldBe(1);
        repo.ReplaceCalls.ShouldBe(1);
    }

    [Fact]
    public async Task SEC_SC_03_RoleManifest_Changed_RevokesOldRoleConsent()
    {
        var repo = new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, Substitute.For<IAuditLogRepository>());
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [RolePolicy()]));

        var changed = RolePolicy() with { ColumnRules = [new DeclarativeColumnRule("id", "Mask")] };
        var result = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("2", [changed]));

        result.Applied.ShouldBe(1);
        result.Revoked.ShouldBe(1);
        repo.Active(DateTimeOffset.UtcNow).Count.ShouldBe(1);
    }

    [Fact]
    public async Task SEC_SC_03_MultiPolicyManifest_UsesExactlyOneTransactionalReplace()
    {
        var repo = new FakeConsentRepository();
        var audit = Substitute.For<IAuditLogRepository>();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, audit);

        var result = await svc.ApplyManifestAsync(Admin(),
            new DeclarativeConsentManifest("1", [RolePolicy("orders"), RolePolicy("items")]));

        result.Applied.ShouldBe(2);
        repo.ReplaceCalls.ShouldBe(1);
        repo.DirectCreateCalls.ShouldBe(0);
        repo.DirectRevokeCalls.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).Count.ShouldBe(2);
    }

    [Fact]
    public async Task SEC_SC_03_FaultDuringReplace_LeavesNoChange_AndWritesNoAudit()
    {
        var repo = new FakeConsentRepository();
        var audit = Substitute.For<IAuditLogRepository>();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, audit);
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [RolePolicy("orders")]));
        var before = repo.Active(DateTimeOffset.UtcNow).Select(c => c.Id).ToHashSet();
        audit.ClearReceivedCalls();

        repo.FailReplace = true;
        var changed = RolePolicy("orders") with { ColumnRules = [new DeclarativeColumnRule("id", "Mask")] };
        await Should.ThrowAsync<InvalidOperationException>(() => svc.ApplyManifestAsync(Admin(),
            new DeclarativeConsentManifest("2", [changed, RolePolicy("items")])));

        repo.Active(DateTimeOffset.UtcNow).Select(c => c.Id).ToHashSet().ShouldBe(before);
        await audit.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!);
    }

    [Fact]
    public async Task SEC_SC_03_DryRun_NeverCallsReplace()
    {
        var repo = new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, Substitute.For<IAuditLogRepository>());

        var result = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [RolePolicy()]), dryRun: true);

        result.Applied.ShouldBe(1);
        repo.ReplaceCalls.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Fact]
    public async Task SEC_SC_03_DuplicateTargetInManifest_IsRejected_BeforeAnyChange()
    {
        var repo = new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, Substitute.For<IAuditLogRepository>());

        await Should.ThrowAsync<ArgumentException>(() => svc.ApplyManifestAsync(Admin(),
            new DeclarativeConsentManifest("1", [RolePolicy(), RolePolicy() with { Effect = "Deny" }])));

        repo.ReplaceCalls.ShouldBe(0);
    }
}
