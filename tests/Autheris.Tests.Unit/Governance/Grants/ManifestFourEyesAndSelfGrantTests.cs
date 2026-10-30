using System.Security.Claims;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

/// <summary>
/// SR-M-10: the declarative manifest activates consents immediately and carries no approvals, so it never creates a grant that needs
/// four-eyes, and it never grants the actor access (any identifier form, or a group/role the actor holds).
/// </summary>
public sealed class ManifestFourEyesAndSelfGrantTests
{
    private static ClaimsPrincipal Caller(params Claim[] extra) => new(new ClaimsIdentity(
        [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN"), .. extra], "test"));

    private static ITableMetadataRepository Tables(string sensitivity = "NORMAL")
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), Sensitivity = sensitivity },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }]
            });
        return repo;
    }

    private static DeclarativeConsentPolicy Policy(string type, string granteeRef, string effect = "Allow", string? clearColumn = null) =>
        new("default", "sales", "orders", type, granteeRef, effect,
            ColumnRules: clearColumn is null ? null : [new DeclarativeColumnRule(clearColumn, "Clear")],
            Description: "analytics access",
            ValidUntil: type is "User" or "ServicePrincipal" ? DateTimeOffset.UtcNow.AddDays(10) : null);

    private static Task<DeclarativeApplyResult> ApplyAsync(
        FakeConsentRepository repo,
        DeclarativeConsentPolicy policy,
        ClaimsPrincipal? caller = null,
        bool dryRun = false,
        string sensitivity = "NORMAL",
        SecurityPrincipalContext? callerContext = null) =>
        new DeclarativeConsentApplyService(Tables(sensitivity), repo, Substitute.For<IAuditLogRepository>())
            .ApplyManifestAsync(caller ?? Caller(), new DeclarativeConsentManifest("1", [policy]), dryRun, callerContext);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SR_M_10_SensitiveIndividualManifestGrant_RequiresFourEyes_AndNothingIsWritten(bool dryRun)
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(repo, Policy("User", "S-1-5-21-ALICE"), dryRun: dryRun, sensitivity: "CONFIDENTIAL"));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        ex.Decision.Class.ShouldBe(GrantClass.Sensitive);
        repo.ReplaceCalls.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SR_M_10_ClearColumnRule_OnAnIndividualManifestGrant_IsUnmasked_AndRequiresFourEyes(bool dryRun)
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(repo, Policy("User", "S-1-5-21-ALICE", clearColumn: "id"), dryRun: dryRun));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        ex.Decision.Class.ShouldBe(GrantClass.IndividualUnmasked);
        repo.ReplaceCalls.ShouldBe(0);
    }

    // The grant policy classifies every service principal as GrantClass.ServicePrincipal (no four-eyes flag); the manifest still
    // refuses an Allow on a sensitive table or with a Clear column rule for it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SR_M_10_SensitiveServicePrincipalManifestGrant_RequiresFourEyes_AndNothingIsWritten(bool dryRun)
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(repo, Policy("ServicePrincipal", "00000000-0000-0000-0000-00000000a11c"), dryRun: dryRun, sensitivity: "CONFIDENTIAL"));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        ex.Decision.Class.ShouldBe(GrantClass.ServicePrincipal);
        repo.ReplaceCalls.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SR_M_10_ClearColumnRule_OnAServicePrincipalManifestGrant_RequiresFourEyes(bool dryRun)
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(repo, Policy("ServicePrincipal", "00000000-0000-0000-0000-00000000a11c", clearColumn: "id"), dryRun: dryRun));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        repo.ReplaceCalls.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
    }

    [Fact]
    public async Task SR_M_10_RegularServicePrincipalManifestGrant_IsStillApplied()
    {
        var repo = new FakeConsentRepository();

        var result = await ApplyAsync(repo, Policy("ServicePrincipal", "00000000-0000-0000-0000-00000000a11c"));

        result.Applied.ShouldBe(1);
        repo.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SR_M_10_ServicePrincipalDenyOnASensitiveTable_NeedsNoFourEyes()
    {
        var repo = new FakeConsentRepository();

        var result = await ApplyAsync(repo, Policy("ServicePrincipal", "00000000-0000-0000-0000-00000000a11c", effect: "Deny"), sensitivity: "CONFIDENTIAL");

        result.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task SR_M_10_RegularIndividualManifestGrant_IsStillApplied()
    {
        var repo = new FakeConsentRepository();

        var result = await ApplyAsync(repo, Policy("User", "S-1-5-21-ALICE"));

        result.Applied.ShouldBe(1);
        repo.Active(DateTimeOffset.UtcNow).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SR_M_10_IndividualDenyOnASensitiveTable_NeedsNoFourEyes()
    {
        var repo = new FakeConsentRepository();

        var result = await ApplyAsync(repo, Policy("User", "S-1-5-21-ALICE", effect: "Deny"), sensitivity: "CONFIDENTIAL");

        result.Applied.ShouldBe(1);
    }

    [Theory]
    [InlineData(ClaimTypes.Upn, "admin@corp.example", "admin@corp.example")]
    [InlineData("email", "admin.user@corp.example", "ADMIN.USER@corp.example")]
    [InlineData(ClaimTypes.Name, "CORP\\adminuser", "adminuser@corp.example")]
    [InlineData("oid", "11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555")]
    public async Task SR_M_10_SelfGrant_ViaAnyIdentifierForm_IsRefused(string claimType, string claimValue, string granteeRef)
    {
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(repo, Policy("User", granteeRef), Caller(new Claim(claimType, claimValue))));

        ex.Message.ShouldContain("Self-grant prohibited");
        repo.ReplaceCalls.ShouldBe(0);
    }

    // An app-only GitOps token (no user SID) must not grant to its own client id.
    [Theory]
    [InlineData("appid")]
    [InlineData("azp")]
    [InlineData("client_id")]
    public async Task SR_M_10_SelfGrant_ToTheActorsOwnApplicationId_IsRefused(string claimType)
    {
        const string AppId = "0f0f0f0f-1111-2222-3333-444444444444";
        var caller = Caller(new Claim(claimType, AppId));
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(repo, Policy("ServicePrincipal", AppId), caller));

        ex.Message.ShouldContain("Self-grant prohibited");
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Theory]
    [InlineData("hasgroups", "true")]
    [InlineData("_claim_names", "{\"groups\":\"src1\"}")]
    public async Task SR_M_10_GroupAllow_WithAnEntraGroupOverage_IsRefused_AlsoInDryRun(string claimType, string claimValue)
    {
        var caller = Caller(new Claim(claimType, claimValue));
        var repo = new FakeConsentRepository();

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(repo, Policy("Group", "S-1-5-21-GRP-OTHERS"), caller, dryRun: true));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
        ex.Message.ShouldContain("group overage");
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task SR_M_10_GroupDenyAndRoleGrant_WithAnEntraGroupOverage_StayAllowed()
    {
        var caller = Caller(new Claim("hasgroups", "true"));

        (await ApplyAsync(new FakeConsentRepository(), Policy("Group", "S-1-5-21-GRP-OTHERS", effect: "Deny"), caller)).Applied.ShouldBe(1);
        (await ApplyAsync(new FakeConsentRepository(), Policy("Role", "DataScientist"), caller)).Applied.ShouldBe(1);
    }

    [Fact]
    public async Task SR_M_10_GrantToAGroupTheActorBelongsTo_IsRefused_AlsoInDryRun()
    {
        var caller = Caller(new Claim(ClaimTypes.GroupSid, "S-1-5-21-GRP-ANALYSTS"));

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(new FakeConsentRepository(), Policy("Group", "S-1-5-21-GRP-ANALYSTS"), caller, dryRun: true));

        ex.Message.ShouldContain("Self-grant prohibited");
    }

    [Fact]
    public async Task SR_M_10_GrantToARoleTheActorHolds_IsRefused()
    {
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(new FakeConsentRepository(), Policy("Role", "governance_admin")));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);
    }

    [Fact]
    public async Task SR_M_10_GrantToAPluginEnrichedGroupOrRole_OfTheRequestContext_IsRefused()
    {
        var context = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ADMIN"),
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            GroupSids = new HashSet<Sid> { new("S-1-5-21-GRP-ENRICHED") },
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EnrichedRole" },
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = "test"
        };

        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(new FakeConsentRepository(), Policy("Group", "S-1-5-21-GRP-ENRICHED"), callerContext: context));
        await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            ApplyAsync(new FakeConsentRepository(), Policy("Role", "EnrichedRole"), callerContext: context));
    }

    [Fact]
    public async Task SR_M_10_DenyForAGroupTheActorBelongsTo_StaysAllowed()
    {
        var caller = Caller(new Claim(ClaimTypes.GroupSid, "S-1-5-21-GRP-ANALYSTS"));
        var repo = new FakeConsentRepository();

        var result = await ApplyAsync(repo, Policy("Group", "S-1-5-21-GRP-ANALYSTS", effect: "Deny"), caller);

        result.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task SR_M_10_GrantToAGroupOrRoleTheActorDoesNotHold_IsApplied()
    {
        var caller = Caller(new Claim(ClaimTypes.GroupSid, "S-1-5-21-GRP-ANALYSTS"));

        (await ApplyAsync(new FakeConsentRepository(), Policy("Group", "S-1-5-21-GRP-OTHERS"), caller)).Applied.ShouldBe(1);
        (await ApplyAsync(new FakeConsentRepository(), Policy("Role", "DataScientist"), caller)).Applied.ShouldBe(1);
    }
}
