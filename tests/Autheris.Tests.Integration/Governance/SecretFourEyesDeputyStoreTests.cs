using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

using Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Issues #1121 and #1126 against the real persistence of both engines: the first approval by a deputy A acting for B stores B's
/// identifier forms with the step, so after a reload B is refused as the second approver while a third owner-group member completes.
/// </summary>
public abstract class SecretFourEyesDeputyStoreTestsBase : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Sid OwnerGroup = new("S-1-5-21-100-200-300-7000");
    private const string Realm = "corp";
    private const string DeputySid = "S-1-5-21-100-200-300-7001";
    private const string PrincipalSid = "S-1-5-21-100-200-300-7002";
    private const string ThirdSid = "S-1-5-21-100-200-300-7003";

    protected GovernanceTestStore Store { get; private set; } = null!;

    protected abstract Task<GovernanceTestStore> CreateStoreAsync();

    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    public async Task DisposeAsync()
    {
        if (Store is not null)
        {
            await Store.DisposeAsync();
        }
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Deputy_for_b_and_b_are_one_authority_after_a_reload()
    {
        if (!Store.IsAvailable) return;
        var (engine, applier, request) = await ArrangeAsync();

        (await engine.DecideAsync(Approve(request.Id, 1, Person(DeputySid)))).Request.Status.ShouldBe(SecretFourEyesRule.PendingSecondApproval);
        var step = (await Store.ConsentApprovals.GetConsentRequestAsync(request.Id)).ShouldNotBeNull().ApprovalSteps.ShouldHaveSingleItem();
        step.ApproverIdentifiers.ShouldContain(PrincipalSid, "the stored authority includes B");

        var same = await Should.ThrowAsync<ConsentDecisionException>(() =>
            engine.DecideAsync(Approve(request.Id, 2, Person(PrincipalSid, owner: true))));
        same.ErrorCode.ShouldBe(ConsentDecisionErrorCodes.FourEyesSameApprover);
        await applier.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);

        var done = await engine.DecideAsync(Approve(request.Id, 2, Person(ThirdSid, owner: true)));
        done.Request.Status.ShouldBe("APPROVED");
        await applier.ReceivedWithAnyArgs(1).ApplyAsync(default!, default!, default);
    }

    private static PermissionDecisionCommand Approve(Guid id, int version, SecurityPrincipalContext decider) => new(id, version, decider, Approve: true);

    private SecurityPrincipalContext Person(string sid, bool owner = false) => new()
    {
        UserSid = new Sid(sid),
        TenantId = Store.TenantA,
        ResolvedTenant = Store.TenantA,
        GroupSids = owner ? new HashSet<Sid> { OwnerGroup } : new HashSet<Sid>(),
        GroupResolutionStatus = GroupResolutionStatus.Complete,
        TenantRoles = new HashSet<string>(["Reader"], StringComparer.OrdinalIgnoreCase),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer",
        Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new(ClaimTypes.PrimarySid, sid), new("tenant_id", Store.TenantA.Value), new("autheris:realm", Realm), new(ClaimTypes.Role, "Reader")], "Bearer"))
    };

    private async Task<(PermissionRequestEngine Engine, IGrantEffectApplier Applier, ConsentRequest Request)> ArrangeAsync()
    {
        var table = new TableIdentifier("sales", "dbo", "deputy_four_eyes_" + Guid.NewGuid().ToString("N")[..8]);
        var meta = await Store.TableMetadata.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = table,
            OwnerTenantId = Store.TenantA.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = table.Domain, SchemaName = table.Schema, TableName = table.TableName, IsActive = true },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }]
        });

        var provider = new FixedProtectionProvider(new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), OwnerGroup, ProtectionClass.Secret, ProtectionClass.Secret, null,
            new Dictionary<string, ColumnProtection>(), CatalogVisibilityMode.Open));
        var applier = Substitute.For<IGrantEffectApplier>();
        applier.RequestKind.Returns(ConsentRequestKind.Data);
        applier.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<GrantRef>>([new GrantRef(Guid.NewGuid(), PermissionGrantKind.Data, table)]));

        var capabilities = new GroupOwnerCapabilities(OwnerGroup);
        var engine = new PermissionRequestEngine(
            Store.ConsentApprovals,
            Substitute.For<IAuditWriter>(),
            new ApproverResolver(protectionSnapshotProvider: provider),
            new ApproverEligibility(capabilities, new SingleRelationDeputies(Store.TenantA)),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            [applier],
            protectionProvider: provider,
            capabilities: capabilities,
            timeProvider: new FakeTimeProvider(Now));

        var request = await Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = meta.Identifier,
            RequesterSid = new Sid("S-1-5-21-100-200-300-9001"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-100-200-300-9001",
            BusinessJustification = "Quarter close needs the secret payroll figures.",
            Status = "PENDING",
            RequestedAt = Now.AddMinutes(-5),
            RequestedValidFrom = Now,
            RequestedValidTo = Now.AddDays(30),
            TenantId = Store.TenantA
        });
        return (engine, applier, request);
    }

    /// <summary>Owner membership is the owner group in the caller's groups.</summary>
    private sealed class GroupOwnerCapabilities(Sid ownerGroup) : IGovernanceCapabilities
    {
        private readonly GovernanceCapabilities _inner = new();

        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => _inner.IsGovernance(principal, tenant);

        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => _inner.IsItAdmin(principal, tenant);

        public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext principal, ContainerRef container, CancellationToken ct) =>
            ValueTask.FromResult(principal.GroupSids.Contains(ownerGroup));

        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext principal, ContainerRef container, TenantId objectTenant, CancellationToken ct) =>
            ValueTask.FromResult(false);

        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId objectTenant) => false;
    }

    /// <summary>One active relation "the deputy acts for the principal"; the principal is an owner-group member and never a request subject.</summary>
    private sealed class SingleRelationDeputies(TenantId tenant) : IDeputyAuthority
    {
        private readonly DeputyRelation _relation = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Source = "hr",
            ExternalId = "a-for-b",
            PrincipalRef = Realm + "|b-key",
            PrincipalKey = DeputyKey.Qualified(Realm, "b-key"),
            PrincipalAltKey = DeputyKey.Qualified(Realm, PrincipalSid),
            DeputyRef = Realm + "|a-key",
            DeputyKey = DeputyKey.Qualified(Realm, "a-key"),
            DeputyAltKey = DeputyKey.Qualified(Realm, DeputySid),
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(10),
            CreatedBy = new Sid("hr-sync"),
            CreatedAt = Now
        };

        public ValueTask<IReadOnlyList<DeputyRelation>> GetActiveRelationsAsync(SecurityPrincipalContext? caller, TenantId requestTenant, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<DeputyRelation>>(caller?.UserSid.Value == DeputySid ? [_relation] : []);

        public ValueTask<bool> IsPrincipalOwnerMemberAsync(TenantId requestTenant, DeputyRelation relation, Sid ownerGroupSid, CancellationToken ct = default) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> IsPrincipalSubjectAsync(TenantId requestTenant, DeputyRelation relation, string? subjectRef, CancellationToken ct = default) =>
            ValueTask.FromResult(false);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlSecretFourEyesDeputyStoreTests(GovernancePostgresFixture fixture) : SecretFourEyesDeputyStoreTestsBase
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() => GovernanceTestStore.CreatePostgreSqlAsync(fixture, "secret_four_eyes_deputy");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerSecretFourEyesDeputyStoreTests(GovernanceSqlServerFixture fixture) : SecretFourEyesDeputyStoreTestsBase
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() => GovernanceTestStore.CreateSqlServerAsync(fixture, "secret_four_eyes_deputy");
}
