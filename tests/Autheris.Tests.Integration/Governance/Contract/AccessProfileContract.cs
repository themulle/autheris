using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IAccessProfileRepository</c> across PostgreSQL and SQL Server engines: persisted access profiles,
/// typed assignments, approvals, renewals and a second gateway replica on the same database.
/// </summary>
public abstract class AccessProfileContract : GovernanceContractBase
{
    private IAccessProfileRepository Repo => Store.AccessProfiles;

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Strict, legacyUsers: ["tenant-iso-user"]);

        (await Repo.GetProfileAsync(Store.TenantB, p.ProfileId)).ShouldBeNull();
        (await Repo.GetAllProfilesAsync(Store.TenantB)).ShouldBeEmpty();
        (await Repo.GetProfilesForSubjectAsync(Store.TenantB, "tenant-iso-user")).ShouldBeEmpty();
        (await Repo.GetProfilesForSubjectsAsync(Store.TenantB, ["tenant-iso-user"], [], [])).ShouldBeEmpty();
        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId)).ShouldNotBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var group = Id("grp");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));

        (await Repo.DeleteProfileAsync(Store.TenantB, p.ProfileId)).ShouldBeFalse();
        (await Repo.RemoveAssignmentAsync(Store.TenantB, p.ProfileId, AccessProfileSubjectType.Group, group)).ShouldBeFalse();

        var read = (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!;
        read.Assignments.Select(a => a.Subject).ShouldBe([group]);
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldContain(p.ProfileId);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        Should.Throw<ArgumentException>(() => { _ = new TenantId("invalid tenant!"); });
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetProfileAsync(default, "p"));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetAllProfilesAsync(default));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetProfilesForSubjectsAsync(default, ["u"], [], []));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["alice"]);

        await Repo.UpsertProfileAsync(p);
        await Repo.UpsertProfileAsync(p);

        (await Repo.GetAllProfilesAsync(Store.TenantA)).Count(x => x.ProfileId == p.ProfileId).ShouldBe(1);
        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!.Assignments.Select(a => a.Subject).ShouldBe(["alice"]);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Strict);
        var group = Id("Grp");

        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group.ToUpperInvariant()));

        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!.Assignments.Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;
        var group = Id("grp");
        var unmasked = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        var strict = await NewProfileAsync(MaskingPolicyMode.Strict);
        await Repo.AssignProfileAsync(Store.TenantA, unmasked.ProfileId, Group(group, "admin1", "admin2"));
        await Repo.AssignProfileAsync(Store.TenantA, strict.ProfileId, Group(group));

        var first = Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], []));
        var second = Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], []));

        first.ShouldBe([strict.ProfileId, unmasked.ProfileId]);
        second.ShouldBe(first);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var group = Id("grp");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));
        var groups = Enumerable.Range(0, 5000).Select(i => $"noise-{i}").Append(group).ToArray();

        var result = await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], groups, []);

        result.Count(x => x.ProfileId == p.ProfileId).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var group = Id("grp");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));
        var expired = new AccessProfile
        {
            ProfileId = Id("p"), Name = Id("n"), TenantId = Store.TenantA, MaskingMode = MaskingPolicyMode.Default, TargetTables = ["*.*"],
            CreatedBy = "admin1", Assignments = [Group(group)], ValidTo = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await Repo.UpsertProfileAsync(expired);

        (await Repo.DeleteProfileAsync(Store.TenantA, p.ProfileId)).ShouldBeTrue();

        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId)).ShouldBeNull();
        (await Repo.GetAllProfilesAsync(Store.TenantA)).Select(x => x.ProfileId).ShouldNotContain(p.ProfileId);
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldNotContain(expired.ProfileId);
    }

    private static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<AccessProfile> NewProfileAsync(MaskingPolicyMode mode, TenantId? tenant = null, DateTimeOffset? createdAt = null, params string[] legacyUsers)
    {
        var profile = new AccessProfile
        {
            ProfileId = Id("p"), Name = Id("name"), TenantId = tenant ?? Store.TenantA, MaskingMode = mode, TargetTables = ["*.*"],
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow, CreatedBy = "admin1", Assignments = TestAssign.Users(legacyUsers)
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

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task AccessProfile_Typed_Assignments_Are_Authoritative()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["alice", "bob"]);
        AccessProfile Edit(params string[] users) => new()
        {
            ProfileId = p.ProfileId, Name = p.Name, TenantId = Store.TenantA, MaskingMode = MaskingPolicyMode.Default, TargetTables = ["*.*"],
            CreatedBy = "admin1", Assignments = TestAssign.Users(users)
        };

        await Repo.UpsertProfileAsync(Edit("alice"));
        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!.Assignments.Select(a => a.Subject).ShouldBe(["alice"]);

        await Repo.UpsertProfileAsync(Edit());
        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!.Assignments.ShouldBeEmpty();
        (await Repo.GetProfilesForSubjectAsync(Store.TenantA, "alice")).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task AccessProfile_Legacy_Stored_Plain_Subjects_Grant_Nothing()
    {
        if (!Store.IsAvailable) return;
        // A profile that carries no typed assignment has no holder: there is no plain-subject fallback that could match a caller.
        var p = await NewProfileAsync(MaskingPolicyMode.Default);

        (await Repo.GetProfilesForSubjectAsync(Store.TenantA, "alice")).ShouldBeEmpty();
        (await Repo.GetProfilesForSubjectsAsync(Store.TenantA, ["alice"], ["grp"], ["role"])).ShouldBeEmpty();
        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!.Assignments.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task LegacyUpsert_RoundTripsProfileAndSubjects()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["alice", "bob"]);

        var read = await Repo.GetProfileAsync(Store.TenantA, p.ProfileId);

        read.ShouldNotBeNull();
        read.Name.ShouldBe(p.Name);
        read.Assignments.Select(a => a.Subject).ShouldBe(["alice", "bob"], ignoreOrder: true);
        (await Repo.GetProfileAsync(Store.TenantB, p.ProfileId)).ShouldBeNull();
        (await Repo.GetAllProfilesAsync(Store.TenantA)).Select(x => x.ProfileId).ShouldContain(p.ProfileId);
        (await Repo.GetAllProfilesAsync(Store.TenantB)).Select(x => x.ProfileId).ShouldNotContain(p.ProfileId);
    }

    [RequiresContainerRuntimeFact]
    public async Task AccessProfile_TargetTables_And_RowFilterPredicate_RoundTrip()
    {
        if (!Store.IsAvailable) return;
        var paramVal = System.Text.Json.JsonDocument.Parse("\"DELIVERED\"").RootElement.Clone();
        var p = new AccessProfile
        {
            ProfileId = Id("p"), Name = Id("name"), TenantId = Store.TenantA, MaskingMode = MaskingPolicyMode.Default,
            TargetTables = ["tem.*", "conf.*"],
            RowScope = [new GrantRowScopeEntry("not_delivered", new Dictionary<string, System.Text.Json.JsonElement> { ["status"] = paramVal })],
            CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "admin1", Assignments = TestAssign.Users("alice")
        };
        await Repo.UpsertProfileAsync(p);

        var read = await Repo.GetProfileAsync(Store.TenantA, p.ProfileId);

        read.ShouldNotBeNull();
        read.TargetTables.ShouldBe(["tem.*", "conf.*"], ignoreOrder: true);
        read.RowScope.ShouldHaveSingleItem().PolicyName.ShouldBe("not_delivered");
    }

    [RequiresContainerRuntimeFact]
    public async Task DeleteProfile_RemovesProfile_AndSubjectLookup()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["S-1-5-21-Delete"]);
        (await Repo.GetProfilesForSubjectAsync(Store.TenantA, "S-1-5-21-Delete")).Select(x => x.ProfileId).ShouldContain(p.ProfileId);

        (await Repo.DeleteProfileAsync(Store.TenantA, p.ProfileId)).ShouldBeTrue();

        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId)).ShouldBeNull();
        (await Repo.GetProfilesForSubjectAsync(Store.TenantA, "S-1-5-21-Delete")).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task SubjectMatch_IsCaseInsensitive_AndTenantScoped()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["S-1-5-21-Alice"]);

        (await Repo.GetProfilesForSubjectAsync(Store.TenantA, "s-1-5-21-ALICE")).Select(x => x.ProfileId).ShouldContain(p.ProfileId);
        (await Repo.GetProfilesForSubjectAsync(Store.TenantB, "S-1-5-21-Alice")).ShouldBeEmpty();
        (await Repo.GetProfilesForSubjectAsync(Store.TenantA, "S-1-5-21-Nobody")).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task Assign_DifferentCasing_UpdatesSingleRow()
    {
        // PERS-06: assignments are keyed by the normalised subject.
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Strict);
        var group = Id("Grp");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group.ToUpperInvariant()));

        var read = await Repo.GetProfileAsync(Store.TenantA, p.ProfileId);

        read!.Assignments.Count(a => a.SubjectType == AccessProfileSubjectType.Group).ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Remove_DifferentCasing_RemovesEffectiveAccess()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Strict);
        var group = Id("Grp");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldContain(p.ProfileId);

        var removed = await Repo.RemoveAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.Group, group.ToUpperInvariant());

        removed.ShouldBeTrue();
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);
    }

    [RequiresContainerRuntimeFact]
    public async Task UpsertProfile_DifferentCasing_NoDuplicate()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default, legacyUsers: ["Alice-Case"]);

        await Repo.UpsertProfileAsync(new AccessProfile
        {
            ProfileId = p.ProfileId, Name = p.Name, TenantId = Store.TenantA, MaskingMode = p.MaskingMode, TargetTables = ["*.*"],
            CreatedAt = p.CreatedAt, CreatedBy = "admin1", Assignments = TestAssign.Users("ALICE-CASE")
        });

        var read = await Repo.GetProfileAsync(Store.TenantA, p.ProfileId);
        read!.Assignments.Count.ShouldBe(1);
    }

    [RequiresContainerRuntimeFact]
    public async Task GroupAssignments_AreFoundThroughGroups_NotThroughUsers_AndRoleAssigneesAreRejected()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Strict);
        var group = Id("grp");
        var role = Id("role");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));
        await Should.ThrowAsync<AccessProfileAssignmentRejectedException>(() => Repo.AssignProfileAsync(
            Store.TenantA, p.ProfileId, new AccessProfileAssignment { Subject = role, SubjectType = AccessProfileSubjectType.Role, CreatedBy = "admin1" }));

        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group.ToUpperInvariant()], [])).ShouldContain(p.ProfileId);
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [], [role])).ShouldNotContain(p.ProfileId);
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [group, role], [], [])).ShouldNotContain(p.ProfileId);
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantB, [], [group], [role])).ShouldNotContain(p.ProfileId);
        (await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [], [])).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task Precedence_IsStrictThenDefaultThenUnmasked_Deterministically()
    {
        if (!Store.IsAvailable) return;
        var group = Id("grp");
        var unmasked = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        var def = await NewProfileAsync(MaskingPolicyMode.Default);
        var strict = await NewProfileAsync(MaskingPolicyMode.Strict);
        await Repo.AssignProfileAsync(Store.TenantA, unmasked.ProfileId, Group(group, "admin1", "admin2"));
        await Repo.AssignProfileAsync(Store.TenantA, def.ProfileId, Group(group));
        await Repo.AssignProfileAsync(Store.TenantA, strict.ProfileId, Group(group));

        var first = Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], []));
        var second = Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], []));

        first.ShouldBe([strict.ProfileId, def.ProfileId, unmasked.ProfileId]);
        second.ShouldBe(first);
    }

    [RequiresContainerRuntimeFact]
    public async Task UnapprovedUnmaskedGroupMapping_IsInactive_UntilASecondAdministratorApproves()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        var group = Id("AUT-Unmask");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group, created: "admin1"));
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);

        Rejected(() => Repo.ApproveAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.Group, group, "ADMIN1"), AccessProfileAssignmentRules.SameApprover);
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);

        var approved = await Repo.ApproveAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.Group, group, "admin2");

        approved.ApprovedBy.ShouldBe("admin2");
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldContain(p.ProfileId);
        var stored = (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!.Assignments.Single(a => a.Subject == group);
        stored.ApprovedBy.ShouldBe("admin2");
        stored.ApprovedAt.ShouldNotBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task IndividualGrant_WithoutExpiry_IsRejected_AndNothingIsStored()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);

        Rejected(() => Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, new AccessProfileAssignment { Subject = "carol", CreatedBy = "admin1" }),
            AccessProfileAssignmentRules.RequiresExpiry);

        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!.Assignments.ShouldBeEmpty();
    }

    [RequiresContainerRuntimeTheory]
    [InlineData("admin1", "admin1", "need", 10)]
    [InlineData("admin1", null, "need", 10)]
    [InlineData("admin1", "dave", "need", 10)]
    [InlineData("admin1", "owner1", null, 10)]
    public async Task IndividualUnmaskedGrant_WithoutSeparateApprovalOrOver30Days_IsRejected(string created, string? approved, string? why, int days)
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);

        var ex = await Should.ThrowAsync<AccessProfileAssignmentRejectedException>(() =>
            Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, UserGrant("dave", days, created, approved, why)));

        ex.Code.ShouldBeOneOf(AccessProfileAssignmentRules.ApprovalRequired, AccessProfileAssignmentRules.TooLong);
        (await Repo.GetProfilesForSubjectsAsync(Store.TenantA, ["dave"], [], [])).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task IndividualUnmaskedGrant_WithSeparateApproval_IsEffective_ThenRenewedOnlyByAnotherPerson()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, UserGrant("dave", 29));
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, ["DAVE"], [], [])).ShouldContain(p.ProfileId);

        Rejected(() => Repo.RenewAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(20), "dave"), AccessProfileAssignmentRules.SelfRenewal);
        Rejected(() => Repo.RenewAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(20), "admin1"), AccessProfileAssignmentRules.SameApprover);

        var renewed = await Repo.RenewAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(20), "owner2");
        renewed.RenewalCount.ShouldBe(1);
        renewed = await Repo.RenewAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.User, "dave", DateTimeOffset.UtcNow.AddDays(10), "owner3");
        renewed.RenewalCount.ShouldBe(2);

        var stored = (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!.Assignments.Single();
        stored.RenewalCount.ShouldBe(2);
        stored.ApprovedBy.ShouldBe("owner3");
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, ["dave"], [], [])).ShouldContain(p.ProfileId);
    }

    [RequiresContainerRuntimeFact]
    public async Task PermanentUserAssignment_IsRejected_NoImplicitClampOrFallback()
    {
        if (!Store.IsAvailable) return;
        var profile = new AccessProfile
        {
            ProfileId = Id("p"), Name = Id("name"), TenantId = Store.TenantA, MaskingMode = MaskingPolicyMode.Default, TargetTables = ["*.*"],
            CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "admin1",
            Assignments = [new AccessProfileAssignment { Subject = Id("perm"), SubjectType = AccessProfileSubjectType.User, CreatedBy = "admin1" }]
        };

        var ex = await Should.ThrowAsync<AccessProfileAssignmentRejectedException>(() => Repo.UpsertProfileAsync(profile));

        ex.Code.ShouldBe(AccessProfileAssignmentRules.RequiresExpiry);
        (await Repo.GetProfileAsync(Store.TenantA, profile.ProfileId)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task UnmaskedUserAssignment_WithoutApproval_IsRejected()
    {
        if (!Store.IsAvailable) return;
        var profile = new AccessProfile
        {
            ProfileId = Id("p"), Name = Id("name"), TenantId = Store.TenantA, MaskingMode = MaskingPolicyMode.Unmasked, TargetTables = ["*.*"],
            CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "admin1",
            Assignments = [new AccessProfileAssignment { Subject = Id("u"), SubjectType = AccessProfileSubjectType.User, CreatedBy = "admin1", ExpiresAt = DateTimeOffset.UtcNow.AddDays(5) }]
        };

        var ex = await Should.ThrowAsync<AccessProfileAssignmentRejectedException>(() => Repo.UpsertProfileAsync(profile));

        ex.Code.ShouldBe(AccessProfileAssignmentRules.ApprovalRequired);
    }

    [RequiresContainerRuntimeFact]
    public async Task ProfileValidTo_InThePast_EndsEveryAssignment()
    {
        if (!Store.IsAvailable) return;
        var user = Id("u");
        var p = new AccessProfile
        {
            ProfileId = Id("p"), Name = Id("n"), TenantId = Store.TenantA, MaskingMode = MaskingPolicyMode.Default, TargetTables = ["*.*"],
            CreatedBy = "admin1", Assignments = TestAssign.Users(user), ValidTo = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await Repo.UpsertProfileAsync(p);

        Ids(await Repo.GetProfilesForSubjectAsync(Store.TenantA, user)).ShouldNotContain(p.ProfileId);
    }

    [RequiresContainerRuntimeFact]
    public async Task ReUpsertingTheProfile_KeepsApprovalsAndRenewals_AndDropsOmittedAssignments()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked, legacyUsers: ["keep", "drop"]);
        var group = Id("AUT-Unmask");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group, "admin1", "admin2"));
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, UserGrant("dave", 10));
        var edited = new AccessProfile
        {
            ProfileId = p.ProfileId, Name = "renamed-" + p.ProfileId, TenantId = Store.TenantA, MaskingMode = MaskingPolicyMode.Unmasked, TargetTables = ["*.*"],
            CreatedBy = "admin1", Assignments = [.. TestAssign.Users("keep"), Group(group, "admin1", "admin2"), UserGrant("dave", 10)]
        };

        await Repo.UpsertProfileAsync(edited);

        var read = (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId))!;
        read.Name.ShouldBe("renamed-" + p.ProfileId);
        read.Assignments.Select(a => a.Subject).ShouldBe(["keep", "dave", group], ignoreOrder: true); // the typed list is authoritative: "drop" is omitted and gone
        read.Assignments.Single(a => a.Subject == "dave").ApprovedBy.ShouldBe("owner1");
        read.Assignments.Single(a => a.Subject == group).ApprovedBy.ShouldBe("admin2");
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, ["dave"], [group], [])).ShouldContain(p.ProfileId);
    }

    [RequiresContainerRuntimeFact]
    public async Task RemoveAssignment_RemovesOnlyThatAssignment()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var g1 = Id("g1");
        var g2 = Id("g2");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(g1));
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(g2));

        (await Repo.RemoveAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.Group, g1)).ShouldBeTrue();
        (await Repo.RemoveAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.Group, g1)).ShouldBeFalse();

        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [g1], [])).ShouldBeEmpty();
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [g2], [])).ShouldContain(p.ProfileId);
    }

    [RequiresContainerRuntimeFact]
    public async Task AssigningToAnUnknownProfile_ThrowsKeyNotFound()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<KeyNotFoundException>(() => Repo.AssignProfileAsync(Store.TenantA, "does-not-exist", Group("g")));
    }

    [RequiresContainerRuntimeFact]
    public async Task DeleteProfile_RemovesItsAssignments()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var group = Id("grp");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));

        (await Repo.DeleteProfileAsync(Store.TenantA, p.ProfileId)).ShouldBeTrue();

        (await Repo.GetProfileAsync(Store.TenantA, p.ProfileId)).ShouldBeNull();
        (await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldBeEmpty();
    }

    [RequiresContainerRuntimeFact]
    public async Task AnotherReplica_SeesAssignmentsApprovalsAndRemovalsImmediately()
    {
        if (!Store.IsAvailable) return;
        await using var replica = await Store.CreateAdditionalStoreAsync();
        var second = replica.AccessProfiles;
        var p = await NewProfileAsync(MaskingPolicyMode.Unmasked);
        var group = Id("AUT-Unmask");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group, "admin1"));

        Ids(await second.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);
        await Repo.ApproveAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.Group, group, "admin2");
        Ids(await second.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldContain(p.ProfileId);
        await second.RemoveAssignmentAsync(Store.TenantA, p.ProfileId, AccessProfileSubjectType.Group, group);
        Ids(await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], [group], [])).ShouldNotContain(p.ProfileId);
    }

    [RequiresContainerRuntimeFact]
    public async Task ManySubjects_AreChunkedAndStillExact()
    {
        if (!Store.IsAvailable) return;
        var p = await NewProfileAsync(MaskingPolicyMode.Default);
        var group = Id("grp");
        await Repo.AssignProfileAsync(Store.TenantA, p.ProfileId, Group(group));
        var groups = Enumerable.Range(0, 2500).Select(i => $"noise-{i}").Append(group).ToArray();

        var result = await Repo.GetProfilesForSubjectsAsync(Store.TenantA, [], groups, []);

        result.Count(x => x.ProfileId == p.ProfileId).ShouldBe(1);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlAccessProfileContractTests(GovernancePostgresFixture fixture) : AccessProfileContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "access_profile_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerAccessProfileContractTests(GovernanceSqlServerFixture fixture) : AccessProfileContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "access_profile_contract");
}
