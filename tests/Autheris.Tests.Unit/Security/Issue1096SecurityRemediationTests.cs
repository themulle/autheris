using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Identity;
using Autheris.Application.Products;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class Issue1096SecurityRemediationTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier TableA = new("sales", "dbo", "orders");
    private static readonly TableIdentifier TableB = new("finance", "dbo", "invoices");
    private const string OwnerGroupA = "S-1-5-21-1000-2000-3000-512";
    private const string OwnerGroupB = "S-1-5-21-1000-2000-3000-513";
    private const string RealmOwnerGroupA = "realm:corp-oidc:data-owners";

    [Fact]
    public async Task GovernanceDomain_CreateDomain_Accepts_RealmBound_GroupKey()
    {
        var gov = CompleteContext("S-1-5-21-1-2-3-900", TenantA, roles: ["GovernanceAdmin"]);
        var domainRepo = NullFake.Create<IGovernanceDomainRepository>((m, args) => m.Name switch
        {
            nameof(IGovernanceDomainRepository.UpsertAsync) =>
                Task.FromResult(new GovernanceDomainUpsertResult(GovernanceDomainWriteStatus.Created, (GovernanceDomain)args![0]!)),
            _ => NullFake.UseDefault
        });
        var service = new GovernanceDomainService(
            domainRepo,
            NullFake.Create<ITableMetadataRepository>(),
            NullFake.Create<IConsentRepository>(),
            NullFake.Create<IAuditWriter>(),
            new RoleOnlyCapabilities(),
            timeProvider: new FakeTimeProvider(Now));

        var result = await service.CreateDomainAsync(
            gov,
            TenantA,
            new CreateGovernanceDomainInput("Partner Domain", OwnerGroupSid: RealmOwnerGroupA));

        result.Status.ShouldBe(DomainServiceStatus.Created);
        result.Value.ShouldNotBeNull();
        result.Value!.OwnerGroupSid.ShouldBe(new Sid(RealmOwnerGroupA));
    }

    [Fact]
    public void AccessPolicyOptionsValidator_Accepts_RealmBound_EmployeeGroupSids()
    {
        var validator = new AccessPolicyOptionsValidator();
        var options = new AccessPolicyOptions
        {
            Internal = new InternalAccessPolicySettings
            {
                EmployeeGroupSids = [OwnerGroupA, RealmOwnerGroupA]
            }
        };

        var result = validator.Validate(name: null, options);

        result.Succeeded.ShouldBeTrue(result.FailureMessage);
    }

    [Fact]
    public async Task ProductAuthorityService_Accepts_RealmBound_OwnerGroup_Across_All_EntryPoints()
    {
        var ownership = NullFake.Create<IContainerOwnership>((m, _) => m.Name switch
        {
            nameof(IContainerOwnership.GetOwnedOrDelegatedTablesAsync) =>
                Task.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier> { TableA }),
            _ => NullFake.UseDefault
        });
        var authority = new ProductAuthorityService(ownership, NullFake.Create<IDataProductRepository>(), new FakeTimeProvider(Now));

        var ownerCaller = new ProductCaller(new Sid("S-1-5-21-1-2-3-101"), TenantA, IsDataProductAdmin: false);
        var adminCaller = new ProductCaller(new Sid("S-1-5-21-1-2-3-102"), TenantA, IsDataProductAdmin: true);
        var product = new DataProduct(
            Guid.NewGuid(),
            TenantA,
            "sales",
            "orders_dp",
            DataProductKind.View,
            "SELECT * FROM sales.dbo.orders",
            "hash",
            [TableA],
            [],
            adminCaller.Sid,
            ProductCreatorAuthority.DataProductAdmin,
            DataProductStatus.Active,
            null,
            new LoadSettings(LoadMode.Live),
            ProductLimits.Default,
            null);

        var baseDecision = await authority.AuthorizeCreationAsync(ownerCaller, DataProductKind.View, [TableA], new Sid(RealmOwnerGroupA));
        var adminDecision = await authority.AuthorizeCreationAsync(adminCaller, DataProductKind.View, [TableA], new Sid(RealmOwnerGroupA));
        var changeDecision = authority.AuthorizeOwnerGroupChange(adminCaller, product, new Sid(RealmOwnerGroupA));

        baseDecision.Allowed.ShouldBeTrue();
        baseDecision.OwnerGroupSid.ShouldBe(new Sid(RealmOwnerGroupA));
        adminDecision.Allowed.ShouldBeTrue();
        adminDecision.OwnerGroupSid.ShouldBe(new Sid(RealmOwnerGroupA));
        changeDecision.Allowed.ShouldBeTrue();
        changeDecision.OwnerGroupSid.ShouldBe(new Sid(RealmOwnerGroupA));
    }

    [Fact]
    public async Task ApproverResolver_DoesNot_Fallback_To_DefaultRealm_When_Multiple_Realms_Exist()
    {
        var defaultB2cRealm = new IdentityRealm(
            "default",
            IdentityRealmKind.Customer,
            TenantBindingSpecification.Fixed(TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim);
        var corpRealm = new IdentityRealm(
            "corp",
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim);
        var realms = new StubRealmRegistry(defaultB2cRealm, corpRealm) { DefaultRealm = defaultB2cRealm };
        var domainId = Guid.NewGuid();
        var groupStore = NullFake.Create<IScimGroupStore>((m, args) => m.Name switch
        {
            nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync) when (string)args![1]! == "default" =>
                Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string> { OwnerGroupA }, false)),
            _ => Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(), false))
        });
        var protection = new FakeProtectionProvider(new Dictionary<TableIdentifier, EffectiveProtection>
        {
            [TableA] = new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, domainId),
                new Sid(OwnerGroupA),
                ProtectionClass.Confidential,
                ProtectionClass.Confidential,
                null,
                new Dictionary<string, ColumnProtection>(),
                CatalogVisibilityMode.OnRequest)
        });

        var resolver = new ApproverResolver(
            protectionSnapshotProvider: protection,
            groupStore: groupStore,
            realmRegistry: realms);

        // Unqualified requester must not fall back to the default customer realm when multiple realms exist.
        var set = await resolver.ResolveAsync(
            TenantA,
            ConsentRequestKind.Data,
            PermissionTarget.ForTable(TableA),
            new Sid("alice"),
            "alice");

        set.A7GovernanceFallback.ShouldBeFalse();
        set.OwnerGroupAssigned.ShouldBeTrue();
    }

    [Fact]
    public async Task ApproverResolver_Rejects_Workforce_Ad_GroupSid_From_NonWorkforce_Realm()
    {
        var b2cRealm = new IdentityRealm(
            "b2c",
            IdentityRealmKind.Customer,
            TenantBindingSpecification.Fixed(TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim);
        var realms = new StubRealmRegistry(b2cRealm) { DefaultRealm = b2cRealm };
        var domainId = Guid.NewGuid();
        var groupStore = NullFake.Create<IScimGroupStore>((m, _) => m.Name switch
        {
            nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync) =>
                Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string> { OwnerGroupA }, false)),
            _ => NullFake.UseDefault
        });
        var protection = new FakeProtectionProvider(new Dictionary<TableIdentifier, EffectiveProtection>
        {
            [TableA] = new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, domainId),
                new Sid(OwnerGroupA),
                ProtectionClass.Confidential,
                ProtectionClass.Confidential,
                null,
                new Dictionary<string, ColumnProtection>(),
                CatalogVisibilityMode.OnRequest)
        });

        var resolver = new ApproverResolver(
            protectionSnapshotProvider: protection,
            groupStore: groupStore,
            realmRegistry: realms);

        var set = await resolver.ResolveAsync(
            TenantA,
            ConsentRequestKind.Data,
            PermissionTarget.ForTable(TableA),
            new Sid("b2c|alice"),
            "b2c|alice");

        // Customer realm cannot hold a Windows AD domain SID, so the requester is not counted as a container owner member.
        set.A7GovernanceFallback.ShouldBeFalse();
    }

    [Fact]
    public async Task RealmExternalApproverResolver_Binds_Realm_Strips_Prefix_And_Blocks_External_Sod_SelfApproval()
    {
        var b2cRealm = new IdentityRealm(
            "b2c",
            IdentityRealmKind.Customer,
            TenantBindingSpecification.Fixed(TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim);
        var defaultRealm = new IdentityRealm(
            "default",
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim);
        var realms = new StubRealmRegistry(defaultRealm, b2cRealm) { DefaultRealm = defaultRealm };
        var scimUser = new ScimUser("u-1", "ext-1", "alice", "Alice", "alice@example.test", true, null, Now, Now);
        var users = NullFake.Create<IScimUserStore>((m, args) =>
        {
            if (args is { Length: >= 3 } && (string)args[1]! == "b2c" && (string)args[2]! is "u-1" or "alice" or "ext-1")
            {
                return Task.FromResult<ScimUser?>(scimUser);
            }

            return Task.FromResult<ScimUser?>(null);
        });
        var groups = NullFake.Create<IScimGroupStore>((m, args) => m.Name switch
        {
            nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync) when (string)args![1]! == "b2c" && (string)args[2]! == "u-1" =>
                Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string> { "realm:b2c:approvers" }, false)),
            _ => Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(), false))
        });

        var resolver = new RealmExternalApproverResolver(users, groups, realms, new FakeTimeProvider(Now));
        var resolved = await resolver.ResolveAsync(TenantA, new AssertedApprover("b2c|alice"));

        resolved.ShouldNotBeNull();
        resolved!.UserSid.ShouldBe(new Sid("b2c|u-1"));
        resolved.GroupSids.ShouldContain(new Sid("realm:b2c:approvers"));

        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = TableA,
            RequesterSid = new Sid("b2c|u-1"),
            RequesterIdentifiers = ["b2c|u-1"],
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "b2c|u-1",
            Status = "PENDING"
        };
        var sod = new SeparationOfDutiesService();
        var decision = await sod.CheckPermissionDecisionAsync(TenantA, resolved, req);
        decision.Allowed.ShouldBeFalse();
        decision.Kind.ShouldBe(SodViolationKind.SelfApproval);

        // Self-approval under SCIM ExternalId alias in the same realm is also blocked.
        var extAliasReq = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = TableA,
            RequesterSid = new Sid("b2c|ext-1"),
            RequesterIdentifiers = ["b2c|ext-1"],
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "b2c|ext-1",
            Status = "PENDING"
        };
        (await sod.CheckPermissionDecisionAsync(TenantA, resolved, extAliasReq)).Allowed.ShouldBeFalse();

        // An invalid/unknown realm prefix must fail closed.
        (await resolver.ResolveAsync(TenantA, new AssertedApprover("ghost|alice"))).ShouldBeNull();
    }

    [Fact]
    public async Task GrantRevokeAuthority_Checks_Raw_Degraded_Claims_First_And_Allows_ServicePrincipal_SelfRevoke()
    {
        var domainId = Guid.NewGuid();
        var authority = CreateRevokeAuthority(domainId, OwnerGroupA);

        var spCaller = CompleteContext(
            "entra|app-oid",
            TenantA,
            extraClaims: [new Claim("client_id", "sp-client-1")]);
        var spGrant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = TableA,
            GranteeType = GranteeType.ServicePrincipal,
            GranteeSid = new Sid("sp-client-1"),
            GrantKind = PermissionGrantKind.Data,
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(5)
        };

        (await authority.EvaluateAsync(spCaller, spGrant)).ShouldBe(GrantRevokeCapacity.Grantee);

        var degradedRoleGov = CompleteContext(
            "user:gov",
            TenantA,
            roles: ["GovernanceAdmin"],
            extraClaims: [new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded)]);
        var userGrant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = TableA,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("user:alice"),
            GrantKind = PermissionGrantKind.Data,
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(5)
        };

        (await authority.EvaluateAsync(degradedRoleGov, userGrant)).ShouldBeNull();

        var catalogGrant = new CatalogVisibilityGrant
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ContainerKind = ContainerKind.Domain,
            ContainerId = domainId,
            GranteeSid = new Sid("user:alice"),
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(5)
        };
        (await authority.EvaluateCatalogAsync(degradedRoleGov, catalogGrant)).ShouldBeNull();
    }

    [Fact]
    public async Task RevokeProfileAssignment_Rejects_ClusterAdmin_And_PartialOwner_Allows_FullOwner_And_Governance()
    {
        var assignmentId = Guid.NewGuid();
        var domainA = Guid.NewGuid();
        var domainB = Guid.NewGuid();
        var req = new ConsentRequest
        {
            Id = assignmentId,
            TenantId = TenantA,
            RequesterSid = new Sid("user:alice"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:alice",
            RequestKind = ConsentRequestKind.ProfileAssignment,
            Status = "APPROVED"
        };
        var grantA = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = TableA,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("user:alice"),
            ProfileAssignmentId = assignmentId,
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(10)
        };
        var grantB = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = TableB,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("user:alice"),
            ProfileAssignmentId = assignmentId,
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(10)
        };

        var approvalRepo = NullFake.Create<IConsentApprovalRepository>((m, _) => m.Name switch
        {
            nameof(IConsentApprovalRepository.GetConsentRequestAsync) => Task.FromResult<ConsentRequest?>(req),
            _ => NullFake.UseDefault
        });
        var assignmentStore = NullFake.Create<IProfileAssignmentStore>((m, _) => m.Name switch
        {
            nameof(IProfileAssignmentStore.GetConsentsByProfileAssignmentAsync) => Task.FromResult<IReadOnlyList<Consent>>([grantA, grantB]),
            _ => NullFake.UseDefault
        });
        var revokedCalls = 0;
        var maintenance = NullFake.Create<IProfileAssignmentMaintenanceService>((m, _) =>
        {
            if (m.Name == nameof(IProfileAssignmentMaintenanceService.RevokeAssignmentAsync))
            {
                revokedCalls++;
                return Task.FromResult(2);
            }

            return NullFake.UseDefault;
        });

        var domains = DomainMap((domainA, OwnerGroupA), (domainB, OwnerGroupB));
        var protection = ProtectionMap((TableA, domainA, OwnerGroupA), (TableB, domainB, OwnerGroupB));
        var caps = new GovernanceCapabilities(domainRepository: domains);
        var authority = new GrantRevokeAuthority(
            new ApproverResolver(domains, protectionSnapshotProvider: protection),
            new ApproverEligibility(caps),
            caps);

        // ClusterAdmin without GovernanceAdmin must be forbidden.
        var clusterAdminCtx = HttpContextFor(CompleteContext("user:root", TenantA, clusterRoles: ["ClusterAdmin"]));
        var clusterStatus = await ExecuteStatusAsync(
            clusterAdminCtx,
            await ConsentApiEndpoints.RevokeProfileAssignmentAsync(
                assignmentId, new RevokeConsentGrantBody("cluster admin check"), clusterAdminCtx, approvalRepo,
                NullFake.Create<IConsentWorkflowConfigRepository>(), assignmentStore, maintenance, authority,
                timeProvider: new FakeTimeProvider(Now)));
        clusterStatus.ShouldBe(StatusCodes.Status403Forbidden);

        // Partial owner (owns TableA only, not TableB) must be forbidden.
        var partialOwnerCtx = HttpContextFor(CompleteContext("user:owner-a", TenantA, groups: [new Sid(OwnerGroupA)]));
        var partialStatus = await ExecuteStatusAsync(
            partialOwnerCtx,
            await ConsentApiEndpoints.RevokeProfileAssignmentAsync(
                assignmentId, new RevokeConsentGrantBody("partial owner check"), partialOwnerCtx, approvalRepo,
                NullFake.Create<IConsentWorkflowConfigRepository>(), assignmentStore, maintenance, authority,
                timeProvider: new FakeTimeProvider(Now)));
        partialStatus.ShouldBe(StatusCodes.Status403Forbidden);
        revokedCalls.ShouldBe(0);

        // GovernanceAdmin succeeds.
        var govCtx = HttpContextFor(CompleteContext("user:gov", TenantA, roles: ["GovernanceAdmin"]));
        var govStatus = await ExecuteStatusAsync(
            govCtx,
            await ConsentApiEndpoints.RevokeProfileAssignmentAsync(
                assignmentId, new RevokeConsentGrantBody("gov revoke"), govCtx, approvalRepo,
                NullFake.Create<IConsentWorkflowConfigRepository>(), assignmentStore, maintenance, authority,
                timeProvider: new FakeTimeProvider(Now)));
        govStatus.ShouldBe(StatusCodes.Status200OK);
        revokedCalls.ShouldBe(1);

        // Full owner (owns both domains) succeeds through both endpoint and GrantRevokeAuthority directly.
        var fullOwner = CompleteContext("user:owner-both", TenantA, groups: [new Sid(OwnerGroupA), new Sid(OwnerGroupB)]);
        (await authority.EvaluateProfileAssignmentAsync(fullOwner, TenantA, req, [grantA, grantB])).ShouldBe(GrantRevokeCapacity.ContainerOwner);
        (await authority.EvaluateProfileAssignmentAsync(CompleteContext("user:owner-a", TenantA, groups: [new Sid(OwnerGroupA)]), TenantA, req, [grantA, grantB])).ShouldBeNull();
        var fullOwnerCtx = HttpContextFor(fullOwner);
        (await ExecuteStatusAsync(
            fullOwnerCtx,
            await ConsentApiEndpoints.RevokeProfileAssignmentAsync(
                assignmentId, new RevokeConsentGrantBody("full owner revoke"), fullOwnerCtx, approvalRepo,
                NullFake.Create<IConsentWorkflowConfigRepository>(), assignmentStore, maintenance, authority,
                timeProvider: new FakeTimeProvider(Now)))).ShouldBe(StatusCodes.Status200OK);
        revokedCalls.ShouldBe(2);

        // Pattern + table assignment: approving pattern grant never bypasses table ownership check.
        var patternGrant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = default,
            ObjectPattern = "sales.dbo.*",
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("user:alice"),
            ProfileAssignmentId = assignmentId,
            ApprovingOwners = ["user:owner-a"],
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(10)
        };
        (await authority.EvaluateProfileAssignmentAsync(CompleteContext("user:owner-a", TenantA, groups: [new Sid(OwnerGroupA)]), TenantA, req, [patternGrant, grantB])).ShouldBeNull();

        // Direct SidFormat helpers ([L-2]).
        SidFormat.IsGroupOrRealmGroupIdentifier(RealmOwnerGroupA).ShouldBeTrue();
        SidFormat.IsValidGroupIdentifierForRealm(OwnerGroupA, IdentityRealmKind.Customer, "b2c").ShouldBeFalse();
        SidFormat.IsValidGroupIdentifierForRealm("realm:b2c:app", IdentityRealmKind.Customer, "b2c").ShouldBeTrue();
    }

    [Fact]
    public async Task ApproverEligibility_Enforces_TenantMatch_On_IsOwnerMember_Branch()
    {
        var domainId = Guid.NewGuid();
        var caps = new AlwaysOwnerCapabilities();
        var eligibility = new ApproverEligibility(caps);
        var set = new ApproverSet(
            TenantA,
            ConsentRequestKind.Data,
            new ContainerRef(ContainerKind.Domain, domainId),
            new Sid(OwnerGroupA),
            OwnerGroupAssigned: true,
            GovernanceEligible: false,
            ItAdminEligible: false);
        var crossTenantDecider = CompleteContext("user:owner", TenantB, groups: [new Sid(OwnerGroupA)]);

        (await eligibility.EvaluateAsync(crossTenantDecider, set)).ShouldBeNull();
    }

    [Fact]
    public async Task PermissionRequestEngine_Unmask_Allows_Active_DataGrant_And_FailsClosed_On_DegradedGroups()
    {
        var domainId = Guid.NewGuid();
        var approvals = NullFake.Create<IConsentApprovalRepository>((m, args) => m.Name switch
        {
            nameof(IConsentApprovalRepository.CreateConsentRequestAsync) => Task.FromResult((ConsentRequest)args![0]!),
            _ => NullFake.UseDefault
        });
        var metadata = NullFake.Create<ITableMetadataRepository>((m, _) => m.Name switch
        {
            nameof(ITableMetadataRepository.GetTableMetadataAsync) => Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Identifier = TableA,
                OwnerTenantId = TenantA.Value,
                Table = new Table { Id = Guid.NewGuid(), SourceName = TableA.Domain, SchemaName = TableA.Schema, TableName = TableA.TableName, IsActive = true },
                Columns = [new TableColumn { ColumnName = "email", DataType = "varchar" }]
            }),
            _ => NullFake.UseDefault
        });
        var protection = ProtectionMap((TableA, domainId, OwnerGroupA, CatalogVisibilityMode.OnRequest));
        var catalogGrants = NullFake.Create<ICatalogVisibilityGrantRepository>((m, _) => m.Name switch
        {
            nameof(ICatalogVisibilityGrantRepository.ListActiveForSubjectsAsync) => Task.FromResult<IReadOnlyList<CatalogVisibilityGrant>>([]),
            _ => NullFake.UseDefault
        });
        var dataConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = TableA,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("user:alice"),
            GrantKind = PermissionGrantKind.Data,
            Effect = ConsentEffect.Allow,
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(5)
        };
        var consentRepo = NullFake.Create<IConsentRepository>((m, _) => m.Name switch
        {
            nameof(IConsentRepository.GetActiveConsentsForSubjectsAsync) => Task.FromResult<IReadOnlyList<Consent>>([dataConsent]),
            _ => NullFake.UseDefault
        });

        var engine = new PermissionRequestEngine(
            approvals,
            new InMemoryAuditWriter(),
            new ApproverResolver(protectionSnapshotProvider: protection, metadataRepository: metadata),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            metadataRepository: metadata,
            protectionProvider: protection,
            catalogVisibilityRepository: catalogGrants,
            consentRepository: consentRepo,
            capabilities: new GovernanceCapabilities(),
            timeProvider: new FakeTimeProvider(Now));

        var completeAlice = CompleteContext("user:alice", TenantA);
        var submitted = await engine.SubmitAsync(new PermissionRequestSubmission(
            completeAlice.Principal!,
            TenantA,
            ConsentRequestKind.Unmask,
            PermissionTarget.ForTable(TableA, ["email"]),
            GranteeType.User,
            "user:alice",
            "Need unmasked email for fraud review.",
            ValidTo: Now.AddDays(5),
            CallerContext: completeAlice));
        submitted.Request.RequestKind.ShouldBe(ConsentRequestKind.Unmask);

        var degradedAlice = CompleteContext(
            "user:alice",
            TenantA,
            groupStatus: GroupResolutionStatus.Degraded,
            extraClaims: [new Claim(GroupResolutionClaims.StatusClaimType, "degraded")]);
        var ex = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(new PermissionRequestSubmission(
            degradedAlice.Principal!,
            TenantA,
            ConsentRequestKind.Unmask,
            PermissionTarget.ForTable(TableA, ["email"]),
            GranteeType.User,
            "user:alice",
            "Need unmasked email for fraud review.",
            ValidTo: Now.AddDays(5),
            CallerContext: degradedAlice)));
        ex.Code.ShouldBe("TARGET_NOT_FOUND");
    }

    private static GrantRevokeAuthority CreateRevokeAuthority(Guid domainId, string ownerGroupSid)
    {
        var domains = DomainMap((domainId, ownerGroupSid));
        var protection = ProtectionMap((TableA, domainId, ownerGroupSid));
        var caps = new GovernanceCapabilities(domainRepository: domains);
        return new GrantRevokeAuthority(
            new ApproverResolver(domains, protectionSnapshotProvider: protection),
            new ApproverEligibility(caps),
            caps);
    }

    private static IGovernanceDomainRepository DomainMap(params (Guid Id, string OwnerGroup)[] items)
    {
        var map = new Dictionary<Guid, GovernanceDomain>();
        foreach (var (id, owner) in items)
        {
            map[id] = new GovernanceDomain(id, TenantA, $"D-{id:N}", null, null, new Sid(owner), ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, Now, Now);
        }

        return NullFake.Create<IGovernanceDomainRepository>((m, args) => m.Name switch
        {
            nameof(IGovernanceDomainRepository.GetAsync) when args![1] is Guid g && map.TryGetValue(g, out var d) => Task.FromResult<GovernanceDomain?>(d),
            _ => NullFake.UseDefault
        });
    }

    private static IProtectionSnapshotProvider ProtectionMap(params (TableIdentifier Table, Guid DomainId, string OwnerGroup)[] items) =>
        ProtectionMap(items.Select(i => (i.Table, i.DomainId, i.OwnerGroup, CatalogVisibilityMode.Open)).ToArray());

    private static IProtectionSnapshotProvider ProtectionMap(params (TableIdentifier Table, Guid DomainId, string OwnerGroup, CatalogVisibilityMode Visibility)[] items)
    {
        var map = new Dictionary<TableIdentifier, EffectiveProtection>();
        foreach (var (table, id, owner, vis) in items)
        {
            map[table] = new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, id),
                new Sid(owner),
                ProtectionClass.Confidential,
                ProtectionClass.Confidential,
                null,
                new Dictionary<string, ColumnProtection>(),
                vis);
        }

        return new FakeProtectionProvider(map);
    }

    private sealed class FakeProtectionProvider(IReadOnlyDictionary<TableIdentifier, EffectiveProtection> map) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(map[table]);
    }

    private static SecurityPrincipalContext CompleteContext(
        string sid,
        TenantId tenant,
        IReadOnlyList<Sid>? groups = null,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? clusterRoles = null,
        GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete,
        IReadOnlyList<Claim>? extraClaims = null)
    {
        var claims = new List<Claim>
        {
            IdentityClaimIssuers.CreatePrimarySidClaim(sid),
            new("tenant_id", tenant.Value),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(groupStatus),
            GroupResolutionClaims.CreateExpiresAt(Now.AddHours(1))
        };
        foreach (var g in groups ?? []) claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(g.Value));
        foreach (var r in roles ?? []) claims.Add(new Claim(ClaimTypes.Role, r));
        foreach (var r in clusterRoles ?? []) claims.Add(new Claim(ClaimTypes.Role, r));
        if (extraClaims is not null) claims.AddRange(extraClaims);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return new SecurityPrincipalContext
        {
            UserSid = new Sid(sid),
            ResolvedTenant = tenant,
            GroupSids = new HashSet<Sid>(groups ?? []),
            GroupResolutionStatus = groupStatus,
            TenantRoles = new HashSet<string>(roles ?? [], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(clusterRoles ?? [], StringComparer.OrdinalIgnoreCase),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true,
            Claims = claims,
            Principal = principal
        };
    }

    private static HttpContext HttpContextFor(SecurityPrincipalContext sec)
    {
        var ctx = new DefaultHttpContext
        {
            User = sec.Principal!,
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        ctx.Items[SecurityPrincipalContext.ItemKey] = sec;
        ctx.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("N");
        return ctx;
    }

    private static async Task<int> ExecuteStatusAsync(HttpContext ctx, IResult result)
    {
        ctx.Response.Body = new System.IO.MemoryStream();
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    private sealed class StubRealmRegistry(params IdentityRealm[] realms) : IIdentityRealmRegistry
    {
        public IdentityRealm? DefaultRealm { get; init; }
        public IdentityRealm? ResolveByIssuer(string issuer) => null;
        public IdentityRealm? ResolveRealmForScheme(string? issuer, string? authenticationType) => null;
        public IdentityRealm? GetRealm(string realmId) => Array.Find(realms, r => string.Equals(r.Id, realmId, StringComparison.OrdinalIgnoreCase));
        public IdentityRealm? GetDefaultRealm() => DefaultRealm;
        public IReadOnlyList<IdentityRealm> GetAllRealms() => realms;
        public void RegisterRealm(IdentityRealm realm) { }
        public bool TryGetRealm(string realmId, out IdentityRealm? realm)
        {
            realm = GetRealm(realmId);
            return realm is not null;
        }
    }

    private sealed class RoleOnlyCapabilities : IGovernanceCapabilities
    {
        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => principal?.IsInRole("GovernanceAdmin") == true;
        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => false;
        public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext caller, ContainerRef container, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext caller, ContainerRef container, TenantId tenant, CancellationToken ct = default) => ValueTask.FromResult(false);
        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId tenant) => false;
    }

    private sealed class AlwaysOwnerCapabilities : IGovernanceCapabilities
    {
        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => false;
        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => false;
        public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext caller, ContainerRef container, CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext caller, ContainerRef container, TenantId tenant, CancellationToken ct = default) => ValueTask.FromResult(false);
        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId tenant) => false;
    }
}
