using Autheris.Tests.Unit.TestSupport;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Declarative grants are authored by Data Governance only, with a justification and an end date, under the same class rules, maxima and separation of duties as requests.</summary>
[Trait("Category", "Security")]
public sealed class DeclarativeGrantAlignmentTests
{
    private const string AdminSid = "S-1-5-21-ADMIN";
    private const string Justification = "quarterly analytics access";

    private static ClaimsPrincipal Caller(string role = "GovernanceAdmin", params Claim[] extra) => new(new ClaimsIdentity(
        [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, AdminSid), new Claim(ClaimTypes.Role, role), .. extra], "test"));

    private static ITableMetadataRepository Tables(bool sensitive = false)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                if (id.TableName.Contains('*', StringComparison.Ordinal))
                {
                    return null;
                }

                return new TableMetadata
                {
                    Table = new Table { Id = Guid.NewGuid() },
                    Identifier = id,
                    Columns = [new TableColumn { ColumnName = "id" }],
                    ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(sensitive ? new[] { "id" } : Array.Empty<string>())
                };
            });
        return repo;
    }

    private static DeclarativeConsentPolicy Policy(
        string type = "Group",
        string granteeRef = "S-1-5-21-GROUP-A",
        string? description = Justification,
        DateTimeOffset? validUntil = null,
        string table = "orders",
        bool noEndDate = false) =>
        new("default", "sales", table, type, granteeRef, "Allow",
            Description: description,
            ValidUntil: noEndDate ? null : validUntil ?? DateTimeOffset.UtcNow.AddDays(30));

    private static DeclarativeConsentApplyService Sut(FakeConsentRepository repo, bool sensitive = false) =>
        new(Tables(sensitive), repo, Substitute.For<IAuditLogRepository>());

    private static DeclarativeConsentManifest Manifest(params DeclarativeConsentPolicy[] policies) => new("1", policies);

    [Theory]
    [InlineData("ClusterAdmin")]
    [InlineData("TenantAdmin")]
    public async Task It_cannot_author(string role)
    {
        var repo = new FakeConsentRepository();

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() =>
            Sut(repo).ApplyManifestAsync(Caller(role), Manifest(Policy())));

        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Governance_admin_can_author()
    {
        var repo = new FakeConsentRepository();

        var result = await Sut(repo).ApplyManifestAsync(Caller(), Manifest(Policy()));

        result.Applied.ShouldBe(1);
        repo.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too short")]
    [InlineData("\u0001\u0002 short \u0003")]
    public async Task Justification_required(string? description)
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<DeclarativeManifestValidationException>(() =>
            Sut(repo).ApplyManifestAsync(Caller(), Manifest(Policy(description: description))));

        ex.Violation.ShouldBe(DeclarativeManifestViolation.Description);
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Invalid_second_entry_refuses_the_whole_manifest()
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<DeclarativeManifestValidationException>(() =>
            Sut(repo).ApplyManifestAsync(Caller(), Manifest(Policy(), Policy(granteeRef: "S-1-5-21-GROUP-B", description: null))));

        ex.PolicyIndex.ShouldBe(1);
        repo.ReplaceCalls.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Fact]
    public async Task End_date_required()
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Sut(repo).ApplyManifestAsync(Caller(), Manifest(Policy(noEndDate: true))));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.RequiresExpiry);
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Reapply_after_purge_requires_end_date()
    {
        // After the start-fresh purge no grant exists; re-applying an entry without an end date must not recreate an open-ended grant,
        // not even when the entry's table is unknown (skipped entries are validated as well).
        var repo = new FakeConsentRepository();

        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Sut(repo).ApplyManifestAsync(Caller(), Manifest(Policy(noEndDate: true, table: "unknown*"))));

        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Fact]
    public async Task Maxima_enforced()
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Sut(repo).ApplyManifestAsync(Caller(), Manifest(Policy(validUntil: DateTimeOffset.UtcNow.AddYears(50)))));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.ExceedsMaximum);
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Secret_self_grant_refused()
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Sut(repo, sensitive: true).ApplyManifestAsync(Caller(), Manifest(Policy("User", AdminSid))));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Pattern_name_never_matches_a_secret_object()
    {
        var repo = new FakeConsentRepository();

        var result = await Sut(repo, sensitive: true).ApplyManifestAsync(Caller(), Manifest(Policy(table: "*")));

        result.Applied.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Fact]
    public async Task Named_secret_follows_class_rules()
    {
        var repo = new FakeConsentRepository();

        // An individual grant on a sensitive object needs an approval and is refused; a named group grant is applied under the declarative maxima.
        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Sut(repo, sensitive: true).ApplyManifestAsync(Caller(), Manifest(Policy("User", "S-1-5-21-ALICE"))));
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();

        var result = await Sut(repo, sensitive: true).ApplyManifestAsync(Caller(), Manifest(Policy("Group", "S-1-5-21-GROUP-A")));
        result.Applied.ShouldBe(1);
    }

    [Theory]
    [InlineData("User", AdminSid)]
    [InlineData("Group", "S-1-5-21-1-2-3-2001")]
    public async Task Applier_not_grantee(string type, string granteeRef)
    {
        var repo = new FakeConsentRepository();
        var caller = Caller(extra: IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-2001"));

        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Sut(repo).ApplyManifestAsync(caller, Manifest(Policy(type, granteeRef))));

        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Degraded_group_resolution_refused()
    {
        var repo = new FakeConsentRepository();
        var caller = Caller(extra: GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Degraded));

        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Sut(repo).ApplyManifestAsync(caller, Manifest(Policy("Group", "S-1-5-21-GROUP-A"))));

        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Validate_and_diff_has_no_side_effects()
    {
        var repo = new FakeConsentRepository();

        var plan = await Sut(repo).ValidateAndDiffAsync(Caller(), Manifest(Policy()));

        plan.Preview.DryRun.ShouldBeTrue();
        plan.Preview.Applied.ShouldBe(1);
        plan.StateHash.ShouldNotBeNullOrEmpty();
        repo.ReplaceCalls.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_after_validate_equals_one_shot_apply()
    {
        var validUntil = DateTimeOffset.UtcNow.AddDays(30);
        var manifest = Manifest(Policy(validUntil: validUntil), Policy(granteeRef: "S-1-5-21-GROUP-B", validUntil: validUntil));
        var oneShotRepo = new FakeConsentRepository();
        var twoStepRepo = new FakeConsentRepository();

        var oneShot = await Sut(oneShotRepo).ApplyManifestAsync(Caller(), manifest);
        var sut = Sut(twoStepRepo);
        var plan = await sut.ValidateAndDiffAsync(Caller(), manifest);
        var twoStep = await sut.ApplyAsync(plan, Caller());

        (twoStep.Applied, twoStep.Unchanged, twoStep.Revoked, twoStep.TotalEvaluated)
            .ShouldBe((oneShot.Applied, oneShot.Unchanged, oneShot.Revoked, oneShot.TotalEvaluated));
        twoStepRepo.Active(DateTimeOffset.UtcNow).Count.ShouldBe(oneShotRepo.Active(DateTimeOffset.UtcNow).Count);
        twoStepRepo.ReplaceCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Stale_plan_is_refused()
    {
        var repo = new FakeConsentRepository();
        var sut = Sut(repo);
        var manifest = Manifest(Policy());
        var plan = await sut.ValidateAndDiffAsync(Caller(), manifest);

        await sut.ApplyManifestAsync(Caller(), manifest);

        await Should.ThrowAsync<DeclarativePlanStaleException>(() => sut.ApplyAsync(plan, Caller()));
        repo.ReplaceCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Plan_cannot_be_applied_by_another_caller()
    {
        var repo = new FakeConsentRepository();
        var sut = Sut(repo);
        var plan = await sut.ValidateAndDiffAsync(Caller(), Manifest(Policy()));
        var other = new ClaimsPrincipal(new ClaimsIdentity(
            [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-OTHER"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "test"));

        await Should.ThrowAsync<ConsentApplyForbiddenException>(() => sut.ApplyAsync(plan, other));
        repo.ReplaceCalls.ShouldBe(0);
    }
}
