using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

[Trait("Category", "Security")]
public sealed class Issue1096TenantBoundaryTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);

    private static TableMetadata VirtualTable(TenantId owner, string name = "v_orders", string? ownerTenantId = null)
    {
        var id = SemanticVirtualTables.IdentifierFor(owner, name);
        return new TableMetadata
        {
            Identifier = id,
            OwnerTenantId = ownerTenantId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = name,
                DocumentationSource = SemanticVirtualTables.SourceMarker,
                IsActive = true,
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };
    }

    private static TableMetadata PhysicalTable(
        string schema,
        string name,
        bool isActive = true,
        bool discoveryHidden = false,
        string? ownerTenantId = null) =>
        new()
        {
            Identifier = new TableIdentifier("sales", schema, name),
            OwnerTenantId = ownerTenantId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = schema,
                TableName = name,
                IsActive = isActive,
                DiscoveryHidden = discoveryHidden,
                DataSourceType = DataSourceType.Sql
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };

    [Theory]
    [InlineData("inactive")]
    [InlineData("hidden")]
    [InlineData("foreign_owner")]
    [InlineData("malformed_owner")]
    [InlineData("reserved_owner")]
    [InlineData("foreign_virtual")]
    public async Task ProfileAssignmentApplier_Rejects_Ineligible_Or_CrossTenant_Target_With_404(string scenario)
    {
        var meta = scenario switch
        {
            "inactive" => PhysicalTable("dbo", "inactive_t", isActive: false, ownerTenantId: TenantA.Value),
            "hidden" => PhysicalTable("dbo", "hidden_t", discoveryHidden: true, ownerTenantId: TenantA.Value),
            "foreign_owner" => PhysicalTable("dbo", "foreign_t", ownerTenantId: TenantB.Value),
            "malformed_owner" => PhysicalTable("dbo", "bad_owner_t", ownerTenantId: "bad tenant!!"),
            "reserved_owner" => PhysicalTable("dbo", "reserved_t", ownerTenantId: "_system"),
            _ => VirtualTable(TenantB, "foreign_model", ownerTenantId: null)
        };

        var profiles = NullFake.Create<IAccessProfileRepository>((m, _) => m.Name switch
        {
            nameof(IAccessProfileRepository.GetProfileAsync) => Task.FromResult<AccessProfile?>(new AccessProfile
            {
                ProfileId = ProfileId.ToString("D"),
                Name = "Restricted",
                TenantId = TenantA,
                TargetTables = [meta.Identifier.ToString()]
            }),
            _ => NullFake.UseDefault
        });
        var tables = new InMemoryTableMetadataRepository();
        tables.Add(meta);

        var applier = new ProfileAssignmentApplier(profiles, tables, NullFake.Create<IGrantWriter>());
        using var rawTx = new DummyDbTransaction();
        var tx = NullFake.Create<IGovernanceTransaction>((m, _) =>
            m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            RequesterSid = new Sid("user:alice"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:alice",
            BusinessJustification = "Profile access request",
            RequestKind = ConsentRequestKind.ProfileAssignment,
            GrantKind = PermissionGrantKind.Data,
            ProfileId = ProfileId
        };

        var ex = await Should.ThrowAsync<ConsentDecisionException>(() =>
            applier.ApplyAsync(tx, new ApprovedPermission(req, new Sid("user:dg"), ApproverCapacity.Governance, false, Now, Now, Now.AddDays(10))));

        ex.ErrorCode.ShouldBe("NOT_FOUND");
        ex.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task ProfileAssignmentApplier_Admits_Active_NonHidden_Physical_And_Own_Virtual_Table()
    {
        var physical = PhysicalTable("dbo", "orders", ownerTenantId: null);
        var ownVirtual = VirtualTable(TenantA, "own_model", ownerTenantId: null);
        var profiles = NullFake.Create<IAccessProfileRepository>((m, _) => m.Name switch
        {
            nameof(IAccessProfileRepository.GetProfileAsync) => Task.FromResult<AccessProfile?>(new AccessProfile
            {
                ProfileId = ProfileId.ToString("D"),
                Name = "Valid",
                TenantId = TenantA,
                TargetTables = [physical.Identifier.ToString(), ownVirtual.Identifier.ToString()]
            }),
            _ => NullFake.UseDefault
        });
        var tables = new InMemoryTableMetadataRepository();
        tables.Add(physical);
        tables.Add(ownVirtual);

        var inserted = 0;
        var grantWriter = NullFake.Create<IGrantWriter>((m, args) =>
        {
            if (m.Name == nameof(IGrantWriter.InsertAndGrantAsync))
            {
                inserted++;
                var c = (Consent)args![1]!;
                return Task.FromResult(new GrantRecord(TenantA.Value, inserted, Guid.NewGuid(), GrantRecordType.Issued, c.Id, c.ConsentRequestId,
                    c.GrantSource, null, c.GranteeType.ToString(), c.GranteeSid!.Value.Value, c.TableIdentifier.ToString(), "{}", c.ValidFrom, c.ValidTo, "user:dg", "{}", Now, Now, GrantRecordChain.GenesisHash));
            }

            return NullFake.UseDefault;
        });

        var applier = new ProfileAssignmentApplier(profiles, tables, grantWriter);
        using var rawTx = new DummyDbTransaction();
        var tx = NullFake.Create<IGovernanceTransaction>((m, _) =>
            m.Name == "get_" + nameof(IGovernanceTransaction.Transaction) ? rawTx : NullFake.UseDefault);
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            RequesterSid = new Sid("user:alice"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:alice",
            BusinessJustification = "Valid profile access",
            RequestKind = ConsentRequestKind.ProfileAssignment,
            GrantKind = PermissionGrantKind.Data,
            ProfileId = ProfileId
        };

        var created = await applier.ApplyAsync(tx, new ApprovedPermission(req, new Sid("user:dg"), ApproverCapacity.Governance, false, Now, Now, Now.AddDays(10)));
        created.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("foreign_virtual")]
    [InlineData("malformed_owner")]
    [InlineData("reserved_owner")]
    public async Task GovernanceDomainService_Rejects_ForeignVirtual_And_InvalidOwnerTenant_Tables(string scenario)
    {
        var domainId = Guid.NewGuid();
        var domain = new GovernanceDomain(domainId, TenantA, "Sales", null, null, new Sid("S-1-5-21-1-2-3-512"), ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, Now, Now);
        var domainRepo = NullFake.Create<IGovernanceDomainRepository>((m, _) => m.Name switch
        {
            nameof(IGovernanceDomainRepository.GetAsync) => Task.FromResult<GovernanceDomain?>(domain),
            _ => NullFake.UseDefault
        });
        var tables = new InMemoryTableMetadataRepository();
        var meta = scenario switch
        {
            "foreign_virtual" => VirtualTable(TenantB, "foreign_v", ownerTenantId: null),
            "malformed_owner" => PhysicalTable("dbo", "bad_owner", ownerTenantId: "bad tenant!!"),
            _ => PhysicalTable("dbo", "reserved_owner", ownerTenantId: "_platform")
        };
        tables.Add(meta);

        var service = new GovernanceDomainService(
            domainRepo,
            tables,
            NullFake.Create<IConsentRepository>(),
            NullFake.Create<IAuditWriter>(),
            new GovernanceAdminCapabilities(),
            timeProvider: new FakeTimeProvider(Now));
        var gov = GovAdmin(TenantA);

        var assign = await service.AssignTablesAsync(gov, TenantA, domainId, [meta.Table.Id]);
        assign.Status.ShouldBe(DomainServiceStatus.NotFound);

        var prot = await service.SetTableProtectionAsync(gov, TenantA, meta.Table.Id.ToString("D"), ProtectionClass.Secret, null);
        prot.Status.ShouldBe(DomainServiceStatus.NotFound);
    }

    [Theory]
    [InlineData("foreign_virtual")]
    [InlineData("malformed_owner")]
    [InlineData("reserved_owner")]
    public async Task PermissionRequestEngine_Submit_Rejects_ForeignVirtual_And_InvalidOwnerTenant(string scenario)
    {
        var meta = scenario switch
        {
            "foreign_virtual" => VirtualTable(TenantB, "foreign_v", ownerTenantId: null),
            "malformed_owner" => PhysicalTable("dbo", "bad_owner", ownerTenantId: "bad tenant!!"),
            _ => PhysicalTable("dbo", "reserved_owner", ownerTenantId: "_system")
        };
        var tables = new InMemoryTableMetadataRepository();
        tables.Add(meta);

        var engine = new PermissionRequestEngine(
            NullFake.Create<IConsentApprovalRepository>(),
            new InMemoryAuditWriter(),
            new ApproverResolver(metadataRepository: tables),
            new ApproverEligibility(new GovernanceCapabilities()),
            new SeparationOfDutiesService(),
            new GrantPolicy(),
            GrantValidityPolicy.CreateDefault(),
            Array.Empty<IGrantEffectApplier>(),
            metadataRepository: tables,
            timeProvider: new FakeTimeProvider(Now));

        var caller = GovAdmin(TenantA);
        var ex = await Should.ThrowAsync<ConsentRequestException>(() => engine.SubmitAsync(
            new PermissionRequestSubmission(
                caller.Principal!,
                TenantA,
                ConsentRequestKind.Data,
                PermissionTarget.ForTable(meta.Identifier),
                GranteeType.User,
                "user:gov",
                "Attempt to target cross-tenant or malformed owner table.",
                ValidTo: Now.AddDays(10),
                CallerContext: caller)));

        ex.Code.ShouldBe("TARGET_NOT_FOUND");
    }

    [Fact]
    public void CatalogTenantOwnership_Enforces_VirtualTable_And_MalformedOrReserved_OwnerTenantId_Boundary()
    {
        var physicalNull = PhysicalTable("dbo", "orders", ownerTenantId: null);
        var physicalHidden = PhysicalTable("dbo", "orders_hidden", discoveryHidden: true, ownerTenantId: null);
        var physicalInactive = PhysicalTable("dbo", "orders_off", isActive: false, ownerTenantId: null);
        var foreignOwner = PhysicalTable("dbo", "foreign_t", ownerTenantId: TenantB.Value);
        var malformedOwner = PhysicalTable("dbo", "bad_t", ownerTenantId: "bad tenant!!");
        var reservedOwner = PhysicalTable("dbo", "reserved_t", ownerTenantId: "_system");
        var ownVirtual = VirtualTable(TenantA, "own_m", ownerTenantId: null);
        var foreignVirtual = VirtualTable(TenantB, "foreign_m", ownerTenantId: null);

        CatalogTenantOwnership.IsOwnedByAnotherTenant(physicalNull, TenantA).ShouldBeFalse();
        CatalogTenantOwnership.IsOwnedByAnotherTenant(ownVirtual, TenantA).ShouldBeFalse();
        CatalogTenantOwnership.IsOwnedByAnotherTenant(foreignVirtual, TenantA).ShouldBeTrue();
        CatalogTenantOwnership.IsOwnedByAnotherTenant(foreignOwner, TenantA).ShouldBeTrue();
        CatalogTenantOwnership.IsOwnedByAnotherTenant(malformedOwner, TenantA).ShouldBeTrue();
        CatalogTenantOwnership.IsOwnedByAnotherTenant(reservedOwner, TenantA).ShouldBeTrue();

        CatalogTenantOwnership.IsEligibleTargetInTenant(physicalNull, TenantA).ShouldBeTrue();
        CatalogTenantOwnership.IsEligibleTargetInTenant(physicalHidden, TenantA).ShouldBeFalse();
        CatalogTenantOwnership.IsEligibleTargetInTenant(physicalHidden, TenantA, allowDiscoveryHidden: true).ShouldBeTrue();
        CatalogTenantOwnership.IsEligibleTargetInTenant(physicalInactive, TenantA).ShouldBeFalse();
        CatalogTenantOwnership.IsEligibleTargetInTenant(foreignVirtual, TenantA).ShouldBeFalse();
        CatalogTenantOwnership.IsEligibleTargetInTenant(malformedOwner, TenantA).ShouldBeFalse();
    }

    private static SecurityPrincipalContext GovAdmin(TenantId tenant)
    {
        var claims = new List<Claim>
        {
            IdentityClaimIssuers.CreatePrimarySidClaim("user:gov"),
            new("tenant_id", tenant.Value),
            new(ClaimTypes.Role, "GovernanceAdmin"),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete),
            GroupResolutionClaims.CreateExpiresAt(Now.AddHours(1))
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return new SecurityPrincipalContext
        {
            UserSid = new Sid("user:gov"),
            ResolvedTenant = tenant,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(["GovernanceAdmin"], StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true,
            Claims = claims,
            Principal = principal
        };
    }

    private sealed class GovernanceAdminCapabilities : IGovernanceCapabilities
    {
        public bool IsGovernance(ClaimsPrincipal? principal, TenantId tenant) => true;
        public bool IsItAdmin(ClaimsPrincipal? principal, TenantId tenant) => false;
        public ValueTask<bool> IsOwnerMemberAsync(SecurityPrincipalContext caller, ContainerRef container, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<bool> CanClassifyAsync(SecurityPrincipalContext caller, ContainerRef container, TenantId tenant, CancellationToken ct = default) => ValueTask.FromResult(false);
        public bool CanEditPolicy(ClaimsPrincipal? principal, TenantId tenant) => false;
    }

    private sealed class DummyDbTransaction : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
