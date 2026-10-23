using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.AccessProfiles;

/// <summary>
/// WP-SC-03 / SEC-SC-04 / SEC-SC-20 contract of the persisted access profiles; run on SQLite (unit) and on PostgreSQL and SQL Server (integration).
/// Every test uses its own profile ids, so tests share one database.
/// </summary>
public abstract class AccessProfileRepositoryContractBase : IAsyncLifetime
{
    protected static readonly TenantId TenantA = new("tenant-a");
    protected static readonly TenantId TenantB = new("tenant-b");

    protected IAccessProfileRepository Repo { get; private set; } = null!;
    protected bool Available { get; private set; }

    protected abstract Task<IAccessProfileRepository?> CreateRepositoryAsync();

    /// <summary>A second repository instance on the same database (a second gateway replica); null when the engine cannot provide one.</summary>
    protected virtual IAccessProfileRepository? CreateSecondInstance() => null;

    protected abstract Task DisposeRepositoryAsync();

    public async Task InitializeAsync()
    {
        var repo = await CreateRepositoryAsync();
        Available = repo != null;
        if (repo != null)
        {
            Repo = repo;
        }
    }

    public Task DisposeAsync() => DisposeRepositoryAsync();

    private static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<AccessProfile> NewProfileAsync(MaskingPolicyMode mode, TenantId? tenant = null, DateTimeOffset? createdAt = null, params string[] legacyUsers)
    {
        var profile = new AccessProfile
        {
            ProfileId = Id("p"), Name = Id("name"), TenantId = tenant ?? TenantA, MaskingMode = mode, TargetTables = ["*.*"],
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow, CreatedBy = "admin1", AssignedSubjects = [.. legacyUsers]
        };
        await Repo.UpsertProfileAsync(profile);
        return profile;
    }

    private static AccessProfileAssignment Group(string subject, string? created = "admin1", string? approved = null) => new()
    {
        Subject = subject, SubjectType = AccessProfileSubjectType.Group, CreatedBy = created, ApprovedBy = approved
    };

    private static AccessProfileAssignment UserGrant(string subject, int days, string? created = "admin1", string? approved = "owner1", string? why = "need") => new()
    {
        Subject = subject, SubjectType = AccessProfileSubjectType.User, ExpiresAt = DateTimeOffset.UtcNow.AddDays(days),
        CreatedBy = created, ApprovedBy = approved, Justification = why
    };

    private static List<string> Ids(IEnumerable<AccessProfile> profiles) => profiles.Select(p => p.ProfileId).ToList();

    private static void Rejected(Func<Task> act, string code) =>
        Should.ThrowAsync<AccessProfileAssignmentRejectedException>(act).GetAwaiter().GetResult().Code.ShouldBe(code);

    [Fact]
    public async Task LegacyUpsert_RoundTripsProfileAndSubjects()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["alice", "bob"]);

        var read = await Repo.GetProfileAsync(TenantA, p.ProfileId);

        read.ShouldNotBeNull();
        read.Name.ShouldBe(p.Name);
        read.AssignedSubjects.ShouldBe(["alice", "bob"], ignoreOrder: true);
        (await Repo.GetProfileAsync(TenantB, p.ProfileId)).ShouldBeNull();
        (await Repo.GetAllProfilesAsync(TenantA)).Select(x => x.ProfileId).ShouldContain(p.ProfileId);
        (await Repo.GetAllProfilesAsync(TenantB)).Select(x => x.ProfileId).ShouldNotContain(p.ProfileId);
    }

    [Fact]
    public async Task SubjectMatch_IsCaseInsensitive_AndTenantScoped()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["S-1-5-21-Alice"]);

        (await Repo.GetProfilesForSubjectAsync(TenantA, "s-1-5-21-ALICE")).Select(x => x.ProfileId).ShouldContain(p.ProfileId);
        (await Repo.GetProfilesForSubjectAsync(TenantB, "S-1-5-21-Alice")).ShouldBeEmpty();
        (await Repo.GetProfilesForSubjectAsync(TenantA, "S-1-5-21-Nobody")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Assign_DifferentCasing_UpdatesSingleRow()
    {
        // PERS-06: assignments are keyed by the normalised subject.
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Strict);
        var group = Id("Grp");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group));
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group.ToUpperInvariant()));

        var read = await Repo.GetProfileAsync(TenantA, p.ProfileId);

        read!.Assignments.Count(a => a.SubjectType == AccessProfileSubjectType.Group).ShouldBe(1);
    }

    [Fact]
    public async Task Remove_DifferentCasing_RemovesEffectiveAccess()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Strict);
        var group = Id("Grp");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group));
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldContain(p.ProfileId);

        var removed = await Repo.RemoveAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.Group, group.ToUpperInvariant());

        removed.ShouldBeTrue();
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);
    }

    [Fact]
    public async Task UpsertProfile_DifferentCasing_NoDuplicate()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["Alice-Case"]);

        await Repo.UpsertProfileAsync(new AccessProfile
        {
            ProfileId = p.ProfileId, Name = p.Name, TenantId = TenantA, MaskingMode = p.MaskingMode, TargetTables = ["*.*"],
            CreatedAt = p.CreatedAt, CreatedBy = "admin1", AssignedSubjects = ["ALICE-CASE"]
        });

        var read = await Repo.GetProfileAsync(TenantA, p.ProfileId);
        read!.AssignedSubjects.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GroupAndRoleAssignments_AreFoundThroughGroupsAndRoles_NotThroughUsers()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Strict);
        var group = Id("grp");
        var role = Id("role");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group));
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, new AccessProfileAssignment { Subject = role, SubjectType = AccessProfileSubjectType.Role, CreatedBy = "admin1" });

        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group.ToUpperInvariant()], [])).ShouldContain(p.ProfileId);
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [], [role])).ShouldContain(p.ProfileId);
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [group, role], [], [])).ShouldNotContain(p.ProfileId);
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantB, [], [group], [role])).ShouldNotContain(p.ProfileId);
        (await Repo.GetProfilesForSubjectsAsync(TenantA, [], [], [])).ShouldBeEmpty();
    }

    [Fact]
    public async Task Precedence_IsStrictThenDefaultThenUnmasked_Deterministically()
    {
        if (!Available) return;
        var group = Id("grp");
        var unmasked = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        var def = await NewProfileAsync(MaskingPolicyMode.Default);
        var strict = await NewProfileAsync(MaskingPolicyMode.Strict);
        await Repo.AssignProfileAsync(TenantA, unmasked.ProfileId, Group(group, "admin1", "admin2"));
        await Repo.AssignProfileAsync(TenantA, def.ProfileId, Group(group));
        await Repo.AssignProfileAsync(TenantA, strict.ProfileId, Group(group));

        var first = Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], []));
        var second = Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], []));

        first.ShouldBe([strict.ProfileId, def.ProfileId, unmasked.ProfileId]);
        second.ShouldBe(first);
    }

    [Fact]
    public async Task UnapprovedUnmaskedGroupMapping_IsInactive_UntilASecondAdministratorApproves()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        var group = Id("AUT-Unmask");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group, created: "admin1"));
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);

        Rejected(() => Repo.ApproveAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.Group, group, "ADMIN1"), AccessProfileAssignmentRules.SameApprover);
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);

        var approved = await Repo.ApproveAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.Group, group, "admin2");

        approved.ApprovedBy.ShouldBe("admin2");
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldContain(p.ProfileId);
        var stored = (await Repo.GetProfileAsync(TenantA, p.ProfileId))!.Assignments.Single(a => a.Subject == group);
        stored.ApprovedBy.ShouldBe("admin2");
        stored.ApprovedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task IndividualGrant_WithoutExpiry_IsRejected_AndNothingIsStored()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);

        Rejected(() => Repo.AssignProfileAsync(TenantA, p.ProfileId, new AccessProfileAssignment { Subject = "carol", CreatedBy = "admin1" }),
            AccessProfileAssignmentRules.RequiresExpiry);

        (await Repo.GetProfileAsync(TenantA, p.ProfileId))!.Assignments.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("admin1", "admin1", "need", 10)]
    [InlineData("admin1", null, "need", 10)]
    [InlineData("admin1", "dave", "need", 10)]
    [InlineData("admin1", "owner1", null, 10)]
    [InlineData("admin1", "owner1", "need", 31)]
    public async Task IndividualUnmaskedGrant_WithoutFourEyesOrOver30Days_IsRejected(string created, string? approved, string? why, int days)
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);

        var ex = await Should.ThrowAsync<AccessProfileAssignmentRejectedException>(() =>
            Repo.AssignProfileAsync(TenantA, p.ProfileId, UserGrant("dave", days, created, approved, why)));

        ex.Code.ShouldBeOneOf(AccessProfileAssignmentRules.FourEyesRequired, AccessProfileAssignmentRules.TooLong);
        (await Repo.GetProfilesForSubjectsAsync(TenantA, ["dave"], [], [])).ShouldBeEmpty();
    }

    [Fact]
    public async Task IndividualUnmaskedGrant_WithFourEyes_IsEffective_ThenRenewedOnlyByAnotherPerson()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, UserGrant("dave", 29));
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, ["DAVE"], [], [])).ShouldContain(p.ProfileId);

        Rejected(() => Repo.RenewAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(20), "dave"), AccessProfileAssignmentRules.SelfRenewal);
        Rejected(() => Repo.RenewAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(20), "admin1"), AccessProfileAssignmentRules.SameApprover);
        Rejected(() => Repo.RenewAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(45), "owner2"), AccessProfileAssignmentRules.TooLong);

        var renewed = await Repo.RenewAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(20), "owner2");
        renewed.RenewalCount.ShouldBe(1);
        renewed = await Repo.RenewAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(10), "owner3");
        renewed.RenewalCount.ShouldBe(2);

        var stored = (await Repo.GetProfileAsync(TenantA, p.ProfileId))!.Assignments.Single();
        stored.RenewalCount.ShouldBe(2);
        stored.ApprovedBy.ShouldBe("owner3");
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, ["dave"], [], [])).ShouldContain(p.ProfileId);
    }

    [Fact]
    public async Task LegacyPermanentUserAssignment_IsClampedAndInactiveAfterTheClamp_NeverRewritten()
    {
        if (!Available) return;
        var user = Id("legacy");
        var old = await NewProfileAsync(MaskingPolicyMode.Default, createdAt: DateTimeOffset.UtcNow.AddDays(-400), legacyUsers: user);
        var fresh = await NewProfileAsync(MaskingPolicyMode.Default, createdAt: DateTimeOffset.UtcNow.AddDays(-10), legacyUsers: user);

        var ids = Ids(await Repo.GetProfilesForSubjectAsync(TenantA, user));

        ids.ShouldContain(fresh.ProfileId);
        ids.ShouldNotContain(old.ProfileId);
        (await Repo.GetProfileAsync(TenantA, old.ProfileId))!.Assignments.Single().ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task LegacyUnmaskedUserAssignment_WithoutApproval_IsNeverEffective()
    {
        if (!Available) return;
        var user = Id("legacy");
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked, legacyUsers: user);

        Ids(await Repo.GetProfilesForSubjectAsync(TenantA, user)).ShouldNotContain(p.ProfileId);
        (await Repo.GetProfileAsync(TenantA, p.ProfileId))!.AssignedSubjects.ShouldContain(user);
    }

    [Fact]
    public async Task ProfileValidTo_InThePast_EndsEveryAssignment()
    {
        if (!Available) return;
        var user = Id("u");
        var p = new AccessProfile
        {
            ProfileId = Id("p"), Name = Id("n"), TenantId = TenantA, MaskingMode = MaskingPolicyMode.Default, TargetTables = ["*.*"],
            CreatedBy = "admin1", AssignedSubjects = [user], ValidTo = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await Repo.UpsertProfileAsync(p);

        Ids(await Repo.GetProfilesForSubjectAsync(TenantA, user)).ShouldNotContain(p.ProfileId);
    }

    [Fact]
    public async Task ReUpsertingTheProfile_KeepsApprovalsAndRenewals_AndDropsRemovedSubjects()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked, legacyUsers: ["keep", "drop"]);
        var group = Id("AUT-Unmask");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group, "admin1", "admin2"));
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, UserGrant("dave", 10));
        var edited = new AccessProfile
        {
            ProfileId = p.ProfileId, Name = "renamed-" + p.ProfileId, TenantId = TenantA, MaskingMode = MaskingPolicyMode.Unmasked, TargetTables = ["*.*"],
            CreatedBy = "admin1", AssignedSubjects = ["keep", "dave"]
        };

        await Repo.UpsertProfileAsync(edited);

        var read = (await Repo.GetProfileAsync(TenantA, p.ProfileId))!;
        read.Name.ShouldBe("renamed-" + p.ProfileId);
        read.Assignments.Select(a => a.Subject).ShouldBe(["keep", "dave", group], ignoreOrder: true); // typed group mapping is kept
        read.Assignments.Single(a => a.Subject == "dave").ApprovedBy.ShouldBe("owner1");
        read.Assignments.Single(a => a.Subject == group).ApprovedBy.ShouldBe("admin2");
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, ["dave"], [group], [])).ShouldContain(p.ProfileId);
    }

    [Fact]
    public async Task BreakGlass_IsEffectiveWithoutApproval_ButLimitedAndNotRenewable()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        BreakGlassRejected(p.ProfileId, hours: 73, why: "outage");
        BreakGlassRejected(p.ProfileId, hours: 24, why: null);

        var stored = await Repo.AssignProfileAsync(TenantA, p.ProfileId, BreakGlass("erin", 48, "outage"));

        stored.Kind.ShouldBe(AccessProfileAssignmentKind.BreakGlass);
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, ["erin"], [], [])).ShouldContain(p.ProfileId);
        Rejected(() => Repo.RenewAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.User, "erin", DateTimeOffset.UtcNow.AddHours(20), "owner1"), AccessProfileAssignmentRules.BreakGlassNotRenewable);
        Rejected(() => Repo.AssignProfileAsync(TenantA, p.ProfileId, UserGrant("erin", 5)), AccessProfileAssignmentRules.KindConflict);
    }

    private void BreakGlassRejected(string profileId, int hours, string? why) =>
        Should.ThrowAsync<AccessProfileAssignmentRejectedException>(() => Repo.AssignProfileAsync(TenantA, profileId, BreakGlass("erin", hours, why))).GetAwaiter().GetResult();

    private static AccessProfileAssignment BreakGlass(string subject, int hours, string? why) => new()
    {
        Subject = subject, SubjectType = AccessProfileSubjectType.User, Kind = AccessProfileAssignmentKind.BreakGlass,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(hours), CreatedBy = "admin1", Justification = why
    };

    [Fact]
    public async Task RemoveAssignment_RemovesOnlyThatAssignment()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var g1 = Id("g1");
        var g2 = Id("g2");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(g1));
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(g2));

        (await Repo.RemoveAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.Group, g1)).ShouldBeTrue();
        (await Repo.RemoveAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.Group, g1)).ShouldBeFalse();

        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [g1], [])).ShouldBeEmpty();
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [g2], [])).ShouldContain(p.ProfileId);
    }

    [Fact]
    public async Task AssigningToAnUnknownProfile_ThrowsKeyNotFound()
    {
        if (!Available) return;

        await Should.ThrowAsync<KeyNotFoundException>(() => Repo.AssignProfileAsync(TenantA, "does-not-exist", Group("g")));
    }

    [Fact]
    public async Task DeleteProfile_RemovesItsAssignments()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var group = Id("grp");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group));

        (await Repo.DeleteProfileAsync(TenantA, p.ProfileId)).ShouldBeTrue();

        (await Repo.GetProfileAsync(TenantA, p.ProfileId)).ShouldBeNull();
        (await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnotherReplica_SeesAssignmentsApprovalsAndRemovalsImmediately()
    {
        if (!Available) return;
        var second = CreateSecondInstance();
        if (second == null) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        var group = Id("AUT-Unmask");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group, "admin1"));

        Ids(await second.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);
        await Repo.ApproveAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.Group, group, "admin2");
        Ids(await second.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldContain(p.ProfileId);
        await second.RemoveAssignmentAsync(TenantA, p.ProfileId, AccessProfileSubjectType.Group, group);
        Ids(await Repo.GetProfilesForSubjectsAsync(TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);
    }

    [Fact]
    public async Task ManySubjects_AreChunkedAndStillExact()
    {
        if (!Available) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var group = Id("grp");
        await Repo.AssignProfileAsync(TenantA, p.ProfileId, Group(group));
        var groups = Enumerable.Range(0, 2500).Select(i => $"noise-{i}").Append(group).ToArray();

        var result = await Repo.GetProfilesForSubjectsAsync(TenantA, [], groups, []);

        result.Count(x => x.ProfileId == p.ProfileId).ShouldBe(1);
    }
}
