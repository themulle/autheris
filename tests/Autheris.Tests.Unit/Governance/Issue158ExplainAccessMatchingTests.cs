namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Mcp.ScopeLevelTestKit;

/// <summary>
/// Issue #158 INC-0 (PLAN-GOV-158-INC0): the access explanation derives <c>consentDescription</c> and <c>validTo</c> only from
/// allow grants that apply to the caller (same predicate as the access decision). SEC-GOV158-01..07.
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue158ExplainAccessMatchingTests
{
    private const string RoleA = "role-analyst";
    private const string RoleB = "role-FOREIGN-secret";
    private const string GroupMember = "S-1-GRP-MEMBER";
    private const string GroupForeign = "S-1-GRP-FOREIGN";
    private const string LinkedSid = "S-1-LINKED";
    private const string Generic = "Active grant for subject " + UserSid;

    private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

    private static Consent Grant(
        GranteeType type,
        double validToDays,
        string? sid = null,
        string? roleName = null,
        Guid? roleId = null,
        ConsentEffect effect = ConsentEffect.Allow,
        string tenant = Tenant,
        TableIdentifier? table = null,
        double validFromDays = -1,
        bool revoked = false) => new()
    {
        TableIdentifier = table ?? Orders,
        TenantId = new TenantId(tenant),
        Effect = effect,
        GranteeType = type,
        GranteeSid = sid == null ? (Sid?)null : new Sid(sid),
        RoleName = roleName,
        RoleId = roleId,
        ValidFrom = Start.AddDays(validFromDays),
        ValidTo = Start.AddDays(validToDays),
        IsRevoked = revoked,
        ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
    };

    private static Consent UserGrant(double days, string sid = UserSid, string? roleName = null) =>
        Grant(GranteeType.User, days, sid, roleName);

    private static Consent RoleGrant(string role, double days) => Grant(GranteeType.Role, days, roleName: role);

    private static ClaimsPrincipal Caller(string[]? roles = null, string[]? groups = null, string[]? linked = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, UserSid), new("tenant_id", Tenant) };
        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((groups ?? []).Select(g => new Claim(ClaimTypes.GroupSid, g)));
        claims.AddRange((linked ?? []).Select(l => new Claim("oid", l)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "MCP"));
    }

    private static (EffectiveAccessExplanationService Service, ITableMetadataRepository Tables, IConsentRepository Consents) CreateService(
        IReadOnlyList<Consent> consents, IConsentResolutionService? resolver = null)
    {
        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(CreateTable());
        tables.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([CreateTable()]));
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(consents));
        repo.GetAllActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(consents));
        return (new EffectiveAccessExplanationService(tables, repo, resolver ?? new ConsentResolutionService()), tables, repo);
    }

    private static Task<EffectiveTableAccessDto> Explain(IReadOnlyList<Consent> consents, ClaimsPrincipal caller) =>
        CreateService(consents).Service.ExplainMyAccessAsync(caller, Orders);

    private static void ShouldBeSameWithin(DateTimeOffset? actual, DateTimeOffset expected) =>
        actual.ShouldNotBeNull().ShouldBe(expected);

    // Regression fixture of the issue: caller holds role A; the repository also returns a grant of role B (listed first).
    private static List<Consent> RegressionFixture() => [RoleGrant(RoleB, 1), RoleGrant(RoleA, 30)];

    [Fact]
    public async Task RoleGrantOfOtherRole_IsNotDisclosedAsDescription()
    {
        var dto = await Explain(RegressionFixture(), Caller(roles: [RoleA]));

        dto.Decision.ShouldBe("ALLOWED");
        dto.ConsentDescription.ShouldBe(RoleA);
    }

    [Fact]
    public async Task RoleGrantOfOtherRole_DoesNotShortenValidTo()
    {
        var consents = RegressionFixture();
        var dto = await Explain(consents, Caller(roles: [RoleA]));

        ShouldBeSameWithin(dto.ValidTo, consents[1].EffectiveValidTo);
    }

    [Fact]
    public async Task OnlyForeignRoleGrantPlusOwnUserGrant_UsesGenericDescription()
    {
        var own = UserGrant(20);
        var dto = await Explain([RoleGrant(RoleB, 1), own], Caller(roles: [RoleA]));

        dto.ConsentDescription.ShouldBe(Generic);
        ShouldBeSameWithin(dto.ValidTo, own.EffectiveValidTo);
    }

    [Fact]
    public async Task LegacyIndividualGrant_ValidTo_IsEffectiveValidTo()
    {
        var legacy = new Consent
        {
            TableIdentifier = Orders,
            TenantId = new TenantId(Tenant),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            ValidFrom = Start.AddDays(-1),
            ValidTo = Start.AddDays(5000)
        };
        legacy.EffectiveValidTo.ShouldBeLessThan(legacy.ValidTo);

        var dto = await Explain([legacy], Caller());

        ShouldBeSameWithin(dto.ValidTo, legacy.EffectiveValidTo);
    }

    [Fact]
    public async Task MultipleMatchingGrants_ValidTo_IsEarliestEffectiveValidTo()
    {
        var user = UserGrant(10);
        var dto = await Explain(
            [RoleGrant(RoleA, 30), Grant(GranteeType.Group, 20, GroupMember), user],
            Caller(roles: [RoleA], groups: [GroupMember]));

        ShouldBeSameWithin(dto.ValidTo, user.EffectiveValidTo);
    }

    [Fact]
    public async Task GroupGrant_CallerNotMember_IsIgnored()
    {
        var own = UserGrant(30);
        var dto = await Explain([Grant(GranteeType.Group, 2, GroupForeign), own], Caller(groups: [GroupMember]));

        ShouldBeSameWithin(dto.ValidTo, own.EffectiveValidTo);
    }

    [Fact]
    public async Task GroupGrant_Member_Counts()
    {
        var group = Grant(GranteeType.Group, 2, GroupMember);
        var dto = await Explain([group, UserGrant(30)], Caller(groups: [GroupMember]));

        ShouldBeSameWithin(dto.ValidTo, group.EffectiveValidTo);
    }

    [Fact]
    public async Task UserGrant_OtherUser_IsIgnored()
    {
        var own = UserGrant(30);
        var dto = await Explain([UserGrant(2, "S-1-OTHER"), own], Caller());

        ShouldBeSameWithin(dto.ValidTo, own.EffectiveValidTo);
    }

    [Fact]
    public async Task UserGrant_LinkedUserSid_Counts()
    {
        var linkedGrant = UserGrant(2, LinkedSid);
        var dto = await Explain([linkedGrant, UserGrant(30)], Caller(linked: [LinkedSid]));

        ShouldBeSameWithin(dto.ValidTo, linkedGrant.EffectiveValidTo);
    }

    [Fact]
    public async Task RoleGrant_MatchedByRoleId_Counts()
    {
        var id = Guid.NewGuid();
        var byId = Grant(GranteeType.Role, 30, roleName: "stored-label", roleId: id);
        var dto = await Explain([byId], Caller(roles: [id.ToString()]));

        dto.ConsentDescription.ShouldBe("stored-label");
    }

    [Fact]
    public async Task ExpiredAndNotYetValidMatchingGrants_AreIgnored()
    {
        var active = UserGrant(30);
        var dto = await Explain(
            [Grant(GranteeType.User, -0.5, UserSid), Grant(GranteeType.User, 40, UserSid, validFromDays: 5), Grant(GranteeType.User, 3, UserSid, revoked: true), active],
            Caller());

        ShouldBeSameWithin(dto.ValidTo, active.EffectiveValidTo);
    }

    [Fact]
    public async Task DenyAndForeignTableConsents_AreIgnoredForValidTo()
    {
        var own = UserGrant(30);
        var dto = await Explain(
            [Grant(GranteeType.User, 1, UserSid, effect: ConsentEffect.Deny, roleName: "deny-label"), Grant(GranteeType.User, 2, UserSid, table: new TableIdentifier("sales", "public", "other")), own],
            Caller());

        dto.Decision.ShouldBe("ALLOWED");
        ShouldBeSameWithin(dto.ValidTo, own.EffectiveValidTo);
        dto.ConsentDescription.ShouldBe(Generic);
    }

    [Fact]
    public async Task NonRoleConsentWithRoleName_IsNotUsedAsDescription()
    {
        var dto = await Explain([UserGrant(30, roleName: "stale-user-label")], Caller());

        dto.ConsentDescription.ShouldBe(Generic);
    }

    [Fact]
    public async Task AllowedDecision_NoMatchingConsents_ReturnsNullValidToAndGenericText()
    {
        var resolver = ResolverReturning(() => TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear }));
        var (service, _, _) = CreateService([RoleGrant(RoleB, 1)], resolver);

        var dto = await service.ExplainMyAccessAsync(Caller(roles: [RoleA]), Orders);

        dto.Decision.ShouldBe("ALLOWED");
        dto.ValidTo.ShouldBeNull();
        dto.ConsentDescription.ShouldBe(Generic);
    }

    private static McpDatasetCatalog CatalogOver(EffectiveAccessExplanationService service, ITableMetadataRepository tables, IConsentRepository consents) =>
        new(tables, consents, Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(new Autheris.Domain.Options.GatewayOptions()), explanationService: service);

    [Fact]
    public async Task McpDescribeMyAccess_EqualsServiceOutput_ForRegressionFixture()
    {
        var (service, tables, repo) = CreateService(RegressionFixture());
        var caller = Caller(roles: [RoleA]);

        var direct = await service.ExplainMyAccessAsync(caller, Orders);
        var viaMcp = await CatalogOver(service, tables, repo).DescribeMyAccessAsync(caller, "sales.public.orders");

        Json(viaMcp).ShouldBe(Json(direct));
    }

    [Fact]
    public async Task ForeignRoleGrant_NameAbsentFromEntireDto()
    {
        var dto = await Explain(RegressionFixture(), Caller(roles: [RoleA]));

        JsonSerializer.Serialize(dto).ShouldNotContain(RoleB);
    }

    [Fact]
    public async Task ForeignRoleGrant_ExpiryDoesNotInfluenceValidTo()
    {
        var a = RoleGrant(RoleA, 30);
        var dto = await Explain([RoleGrant(RoleB, 0.5), a], Caller(roles: [RoleA]));

        ShouldBeSameWithin(dto.ValidTo, a.EffectiveValidTo);
    }

    [Fact]
    public async Task NoRoleClaims_OwnUserGrantPlusForeignRoleGrant_AllowedWithoutRoleName()
    {
        var own = UserGrant(15);
        var dto = await Explain([RoleGrant(RoleB, 1), own], Caller());

        dto.Decision.ShouldBe("ALLOWED");
        dto.ConsentDescription.ShouldBe(Generic);
        JsonSerializer.Serialize(dto).ShouldNotContain(RoleB);
        ShouldBeSameWithin(dto.ValidTo, own.EffectiveValidTo);
    }

    [Fact]
    public async Task UnknownGranteeType_NeverMatchesNorDiscloses()
    {
        var unknown = Grant((GranteeType)99, 1, UserSid, roleName: RoleB);
        var caller = Caller(roles: [RoleB]);
        var own = UserGrant(30);

        new ConsentResolutionService().ResolveAccess(new Sid(UserSid), caller.GetGroupSids(), caller.GetUserRoles(), Orders, [unknown]).IsAllowed.ShouldBeFalse();

        var dto = await Explain([unknown, own], caller);
        dto.ConsentDescription.ShouldBe(Generic);
        JsonSerializer.Serialize(dto).ShouldNotContain(RoleB);
        ShouldBeSameWithin(dto.ValidTo, own.EffectiveValidTo);
    }

    public static IEnumerable<object[]> ParityCases()
    {
        foreach (var type in new[] { GranteeType.User, GranteeType.Group, GranteeType.Role, GranteeType.ServicePrincipal, (GranteeType)99 })
        foreach (var match in new[] { true, false })
        foreach (var timing in new[] { "active", "expired", "notyet", "revoked" })
        foreach (var sameTable in new[] { true, false })
        {
            yield return [type, match, timing, sameTable];
        }
    }

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void ExplanationApplicability_ParityWithResolver_Theory(GranteeType type, bool match, string timing, bool sameTable)
    {
        var sid = match ? (type == GranteeType.Group ? GroupMember : UserSid) : "S-1-NOPE";
        var consent = Grant(
            type, timing == "expired" ? -0.5 : 10, sid,
            roleName: match ? RoleA : "other-role",
            table: sameTable ? Orders : new TableIdentifier("sales", "public", "other"),
            validFromDays: timing == "notyet" ? 2 : -1,
            revoked: timing == "revoked");
        var caller = Caller(roles: [RoleA], groups: [GroupMember]);
        var groups = caller.GetGroupSids();
        var roles = caller.GetUserRoles();
        var all = caller.GetAllUserSids();
        var now = DateTimeOffset.UtcNow;

        var helper = EffectiveAccessExplanationService.MatchingAllowConsents([consent], Orders, now, new Sid(UserSid), groups, roles, all);
        var decision = new ConsentResolutionService().ResolveAccess(new Sid(UserSid), groups, roles, Orders, [consent], allUserSids: all);

        (helper.Count == 1).ShouldBe(decision.IsAllowed);
        ConsentResolutionService.IsApplicable(consent, Orders, now, new Sid(UserSid), groups, roles, all).ShouldBe(helper.Count == 1);
    }

    [Fact]
    public void ValidTo_BoundaryExactlyAtEffectiveValidTo_IsExcluded()
    {
        var consent = UserGrant(10);
        var caller = Caller();
        var groups = caller.GetGroupSids();
        var roles = caller.GetUserRoles();
        var all = caller.GetAllUserSids();

        ConsentResolutionService.IsApplicable(consent, Orders, consent.EffectiveValidTo, new Sid(UserSid), groups, roles, all).ShouldBeFalse();
        ConsentResolutionService.IsApplicable(consent, Orders, consent.EffectiveValidTo.AddTicks(-1), new Sid(UserSid), groups, roles, all).ShouldBeTrue();
        EffectiveAccessExplanationService.MatchingAllowConsents([consent], Orders, consent.EffectiveValidTo, new Sid(UserSid), groups, roles, all).ShouldBeEmpty();
    }

    [Fact]
    public async Task GroupGrant_NotInToken_NotDisclosed()
    {
        var foreign = Grant(GranteeType.Group, 1, GroupForeign, roleName: RoleB);
        var dto = await Explain([foreign, UserGrant(30)], Caller(groups: [GroupMember]));

        dto.ConsentDescription.ShouldBe(Generic);
        JsonSerializer.Serialize(dto).ShouldNotContain(RoleB);
    }

    [Fact]
    public async Task OtherTenantConsent_NeverUsed()
    {
        var own = UserGrant(30);
        var dto = await Explain([Grant(GranteeType.User, 1, UserSid, tenant: "tenant-other"), Grant(GranteeType.Role, 1, roleName: RoleA, tenant: "tenant-other"), own], Caller(roles: [RoleA]));

        dto.ConsentDescription.ShouldBe(Generic);
        ShouldBeSameWithin(dto.ValidTo, own.EffectiveValidTo);
    }

    [Fact]
    public async Task DeniedAndUnknownTablePaths_UnchangedByFix_NoExistenceOracle()
    {
        var unknownTable = new TableIdentifier("sales", "public", "ghost");
        var (service, tables, _) = CreateService([RoleGrant(RoleB, 1)]);
        tables.GetTableMetadataAsync(unknownTable, Arg.Any<CancellationToken>()).Returns((TableMetadata?)null);
        var caller = Caller(roles: [RoleA]);

        var hidden = await service.ExplainMyAccessAsync(caller, Orders);
        var unknown = await service.ExplainMyAccessAsync(caller, unknownTable);

        hidden.Decision.ShouldBe("DENIED");
        hidden.ValidTo.ShouldBeNull();
        unknown.Decision.ShouldNotBe("ALLOWED");
        JsonSerializer.Serialize(hidden).ShouldNotContain(RoleB);
    }

    [Fact]
    public async Task RoleGrantMatchedByRoleId_ShowsOnlyCallersHeldRoleGrant()
    {
        var held = Guid.NewGuid();
        var other = Guid.NewGuid();
        var dto = await Explain(
            [Grant(GranteeType.Role, 1, roleName: "label-of-other-id", roleId: other), Grant(GranteeType.Role, 30, roleName: "label-of-held-id", roleId: held)],
            Caller(roles: [held.ToString()]));

        dto.ConsentDescription.ShouldBe("label-of-held-id");
    }

    [Fact]
    public async Task DescriptionAndValidTo_IndependentOfRepositoryOrder()
    {
        var consents = new List<Consent> { RoleGrant("role-b-held", 10), RoleGrant("role-a-held", 20), RoleGrant(RoleB, 1), UserGrant(30) };
        var caller = Caller(roles: ["role-a-held", "role-b-held"]);

        var forward = await Explain(consents, caller);
        var reversed = await Explain(Enumerable.Reverse(consents).ToList(), caller);

        Json(forward).ShouldBe(Json(reversed));
        forward.ConsentDescription.ShouldBe("role-a-held");
    }

    [Fact]
    public async Task McpAndGraphQlMyAccess_ShareServiceOutput_NoForeignRole()
    {
        var (service, tables, repo) = CreateService(RegressionFixture());

        var viaMcp = await CatalogOver(service, tables, repo).DescribeMyAccessAsync(Caller(roles: [RoleA]), "sales.public.orders");

        Json(viaMcp).ShouldNotContain(RoleB);
        viaMcp.ConsentDescription.ShouldBe(RoleA);
    }
}
