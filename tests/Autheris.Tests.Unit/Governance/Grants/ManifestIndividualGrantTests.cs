using Autheris.Tests.Unit.TestSupport;
using System.Security.Claims;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

/// <summary>WP-SC-16 / SEC-SC-20: the declarative manifest path never creates a permanent or over-limit individual grant.</summary>
public sealed class ManifestIndividualGrantTests
{
    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "test"));

    private static ITableMetadataRepository Tables(bool sensitive = false)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid() },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }],
                ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(sensitive ? new[] { "id" } : Array.Empty<string>())
            });
        return repo;
    }

    private static DeclarativeConsentPolicy User(DateTimeOffset? until, string type = "User") =>
        new("default", "sales", "orders", type, "S-1-5-21-ALICE", "Allow", ValidUntil: until, Description: "analytics access");

    private static DeclarativeApplyResult? Last;

    private static async Task<FakeConsentRepository> ApplyAsync(
        DeclarativeConsentPolicy p,
        bool dryRun = false,
        FakeConsentRepository? repo = null,
        IGrantValidityPolicy? validityPolicy = null,
        bool sensitive = false)
    {
        repo ??= new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(
            Tables(sensitive),
            repo,
            Substitute.For<IAuditLogRepository>(),
            grantValidityPolicy: validityPolicy);
        Last = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [p]), dryRun);
        return repo;
    }

    [Fact]
    public async Task SEC_SC_20_IndividualManifestGrant_WithoutValidUntil_IsRejected_AndNothingChanges()
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() => ApplyAsync(User(null), repo: repo));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.RequiresExpiry);
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task SEC_SC_20_IndividualManifestGrant_ValidUntilInThePast_IsRejected()
    {
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() => ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(-1))));
        ex.ErrorCode.ShouldBe(GrantErrorCodes.ExpiryInPast);
    }

    [Fact]
    public async Task SEC_SC_20_IndividualManifestGrant_BeyondClassMaximum_IsRejected_AlsoInDryRun()
    {
        // No compiled-in class ceilings: only the configured (declarative) maximum bounds the manifest grant.
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(5 * 365 + 30)), dryRun: true));
        ex.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);

        var tight = new GrantValidityPolicy(Options.Create(new GrantValidityOptions { DefaultDuration = "P1D", MaxDuration = "P90D" }));
        var ex2 = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(100)), sensitive: true, validityPolicy: tight));
        ex2.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);

        // A multi-year manifest grant is fine under the default multi-year test maximum, also for sensitive tables.
        var repo = await ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(3 * 365)), sensitive: false);
        repo.Active(DateTimeOffset.UtcNow).Count.ShouldBe(1);
    }

    [Fact]
    public async Task SEC_SC_20_IndividualManifestGrant_WithinLimit_IsCreatedWithExactlyThatExpiry()
    {
        var until = DateTimeOffset.UtcNow.AddDays(30);

        var repo = await ApplyAsync(User(until));

        var c = repo.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem();
        c.ValidTo.ShouldBe(until);
    }

    [Fact]
    public async Task SEC_SC_20_ReApplyingTheSameManifest_NeverExtendsAnIndividualGrant()
    {
        var first = DateTimeOffset.UtcNow.AddDays(10);
        var repo = await ApplyAsync(User(first));

        // The manifest later asks for a longer validity for an otherwise identical policy.
        await ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(50)), repo: repo);

        Last!.Unchanged.ShouldBe(1);
        Last.Applied.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem().ValidTo.ShouldBe(first);
    }

    [Fact]
    public async Task SEC_SC_20_ServicePrincipalManifestGrant_Allows365Days_AndRequiresExpiry()
    {
        var spValidity = new GrantValidityPolicy(Options.Create(new GrantValidityOptions
        {
            DefaultDuration = "P2W",
            MaxDuration = "P2M",
            DeclarativeMaxDuration = "P365D"
        }));
        var repo = await ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(300), "ServicePrincipal"), validityPolicy: spValidity);
        repo.Active(DateTimeOffset.UtcNow).Count.ShouldBe(1);

        await Should.ThrowAsync<GrantPolicyViolationException>(() => ApplyAsync(User(null, "ServicePrincipal"), validityPolicy: spValidity));
    }

    [Fact]
    public async Task SEC_SC_20_GroupManifestGrants_RequireExplicitValidUntil()
    {
        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(new DeclarativeConsentPolicy("default", "sales", "orders", "Group", "S-1-5-21-1-2-3-2002", "Allow")));

        var repo = await ApplyAsync(new DeclarativeConsentPolicy(
            "default", "sales", "orders", "Group", "S-1-5-21-1-2-3-2002", "Allow",
            ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));

        var c = repo.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem();
        (c.ValidTo - c.ValidFrom).TotalDays.ShouldBe(30, 0.5);
    }

    [Fact]
    public async Task SEC_SC_20_Manifest_CanShortenAnIndividualGrant_ButNotExtendIt()
    {
        var repo = await ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(60)));
        var first = repo.Active(DateTimeOffset.UtcNow).Single();

        // an earlier validUntil is a change: the old grant is revoked and replaced by a shorter one
        await ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(30)), repo: repo);
        Last!.Applied.ShouldBe(1);
        Last.Revoked.ShouldBe(1);
        var shortened = repo.Active(DateTimeOffset.UtcNow).Single();
        shortened.Id.ShouldNotBe(first.Id);
        shortened.ValidTo.ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(31));

        // a later validUntil is not a change (renewal needs its own approval)
        await ApplyAsync(User(DateTimeOffset.UtcNow.AddDays(45)), repo: repo);
        Last!.Applied.ShouldBe(0);
        Last.Unchanged.ShouldBe(1);
        repo.Active(DateTimeOffset.UtcNow).Single().Id.ShouldBe(shortened.Id);
    }

    [Fact]
    public async Task SEC_SC_20_Manifest_AppliedAuditTravelsIntoTheReplaceTransaction_NotAfterCommit()
    {
        var repo = new FakeConsentRepository();
        var audit = Substitute.For<IAuditLogRepository>();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, audit);

        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [User(DateTimeOffset.UtcNow.AddDays(30))]));

        repo.ReplaceAuditEntries.ShouldContain(e => e.EventType == "GITOPS_CONSENT_APPLY" && e.Decision == "APPLIED");
        await audit.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!);
    }

    [Fact]
    public async Task SEC_SC_20_Manifest_DenyWithoutValidUntil_IsRejected_AndWithValidUntilStaysActive()
    {
        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(new DeclarativeConsentPolicy("default", "sales", "orders", "User", "S-1-5-21-ALICE", "Deny", Description: "block")));

        var repo = await ApplyAsync(new DeclarativeConsentPolicy(
            "default", "sales", "orders", "User", "S-1-5-21-ALICE", "Deny",
            Description: "block", ValidUntil: DateTimeOffset.UtcNow.AddDays(30)));

        var deny = repo.Active(DateTimeOffset.UtcNow.AddDays(1)).Single();
        deny.Effect.ShouldBe(ConsentEffect.Deny);
    }
}
