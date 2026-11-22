using System.Security.Claims;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

[Trait("Category", "Security")]
public sealed class ClassificationLevelMappingTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid ProposerSid = new("S-1-5-21-100-200-300-11");
    private static readonly Sid ReviewerSid = new("S-1-5-21-100-200-300-22");
    private static readonly Sid ItSid = new("S-1-5-21-100-200-300-33");
    private static readonly Sid OwnerGroupSid = new("S-1-5-21-100-200-300-512");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static ClaimsPrincipal PrincipalFor(Sid sid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid.Value),
            new("tenant_id", TenantA.Value)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Theory]
    [InlineData("L1_PUBLIC", ProtectionClass.Public)]
    [InlineData("L2_INTERNAL", ProtectionClass.Internal)]
    [InlineData("L3_CONFIDENTIAL", ProtectionClass.Confidential)]
    [InlineData("L4_STRICTLY_CONFIDENTIAL", ProtectionClass.Secret)]
    public void Level_keys_map_to_expected_protection_classes(string levelKey, ProtectionClass expected)
    {
        var options = new ClassificationOptions();
        options.ResolveProtectionClass(levelKey).ShouldBe(expected);
        options.SensitivityLevels.Single(l => l.Key == levelKey).ProtectionClass.ShouldBe(expected);
    }

    [Fact]
    public async Task Engine_populates_proposed_protection_class_and_mask_category()
    {
        var gatewayOptions = new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions { Classification = new ClassificationOptions() });
        var engine = new ClassificationEngine(gatewayOptions, NullLogger<ClassificationEngine>.Instance);

        var context = new TableClassificationContext(
            new TableIdentifier("crm", "public", "customers"),
            [
                new ColumnMetadataContext("email", "varchar"),
                new ColumnMetadataContext("iban", "varchar"),
                new ColumnMetadataContext("diagnosis", "varchar"),
                new ColumnMetadataContext("notes", "varchar")
            ]);

        var proposal = await engine.ClassifyTableAsync(context);

        var email = proposal.Columns.Single(c => c.ColumnName == "email");
        email.ProposedProtectionClass.ShouldBe(ProtectionClass.Internal);
        email.ProposedMaskCategory.ShouldBe(ClassificationScopes.PiiCode);

        var iban = proposal.Columns.Single(c => c.ColumnName == "iban");
        iban.ProposedProtectionClass.ShouldBe(ProtectionClass.Confidential);
        iban.ProposedMaskCategory.ShouldBe(ClassificationScopes.FinancialCode);

        var diagnosis = proposal.Columns.Single(c => c.ColumnName == "diagnosis");
        diagnosis.ProposedProtectionClass.ShouldBe(ProtectionClass.Secret);
        diagnosis.ProposedMaskCategory.ShouldBe(ClassificationScopes.PiiSpecialCode);

        var notes = proposal.Columns.Single(c => c.ColumnName == "notes");
        notes.ProposedProtectionClass.ShouldBe(ProtectionClass.Internal);
        notes.ProposedMaskCategory.ShouldBeNull();
    }

    [Fact]
    public async Task Requester_cannot_decide_own_downgrade()
    {
        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var sm = new ClassificationWorkflowStateMachine(
            new StaticOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance);

        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            new TableIdentifier("crm", "public", "customers"),
            ProposerSid.Value,
            "PENDING_OWNER_REVIEW",
            [
                new("email", "varchar", "L3_CONFIDENTIAL", 3, "EMAIL", "MASK", 0.95, false, Reasoning: null, ProposedProtectionClass: ProtectionClass.Confidential, ProposedMaskCategory: ClassificationScopes.PiiCode)
            ],
            T0,
            ProposedBySid: ProposerSid.Value);

        var request = new TableClassificationApprovalRequest(
            proposal.ProposalId,
            proposal.TableIdentifier,
            ProposerSid,
            ReviewerRole: "DATA_OWNER",
            Action: "APPROVE",
            ColumnDecisions:
            [
                new("email", "OVERRIDE", "L1_PUBLIC", 1, "NONE", EffectiveProtectionClass: ProtectionClass.Public)
            ],
            Justification: "Requester trying to lower own proposal.",
            TenantId: TenantA,
            Caller: PrincipalFor(ProposerSid, "Consumer"));

        var result = await sm.TransitionAsync(proposal, request);

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("SOD_REQUESTER_DOWNGRADE_FORBIDDEN");
    }

    [Fact]
    public async Task Approval_checks_CanClassifyAsync_refuses_ItAdmin_and_writes_object_protection()
    {
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var table = new TableIdentifier("crm", "public", "customers");
        var meta = new TableMetadata
        {
            Identifier = table,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = tableId, SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true },
            Columns =
            [
                new TableColumn { TableId = tableId, ColumnName = "email", DataType = "varchar" },
                new TableColumn { TableId = tableId, ColumnName = "notes", DataType = "varchar" }
            ]
        };

        var domains = Substitute.For<IGovernanceDomainRepository>();
        var container = new ContainerDefaults(new ContainerRef(ContainerKind.Domain, domainId), OwnerGroupSid, ProtectionClass.Internal, CatalogVisibilityMode.OnRequest, IsValid: true);
        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "CRM", null, null, OwnerGroupSid, ProtectionClass.Internal, CatalogVisibilityMode.OnRequest, false, 1, T0, T0));
        domains.GetContainerForTableAsync(TenantA, tableId, Arg.Any<CancellationToken>()).Returns(container);
        domains.GetProtectionAsync(TenantA, tableId, Arg.Any<CancellationToken>())
            .Returns((null, new Dictionary<string, ObjectProtection>()));

        var tables = new InMemoryTableMetadataRepository(meta);
        var consents = Substitute.For<IConsentRepository>();
        consents.GetConsentPageAsync(Arg.Any<ConsentPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentPage([], null));
        var audit = new InMemoryAuditWriter();
        var caps = new GovernanceCapabilities(domainRepository: domains);
        var domainService = new GovernanceDomainService(domains, tables, consents, audit, caps, timeProvider: new FakeTimeProvider(T0));

        var options = new GatewayOptions { Classification = new ClassificationOptions() };
        var sm = new ClassificationWorkflowStateMachine(
            new StaticOptionsMonitor<GatewayOptions>(options),
            NullLogger<ClassificationWorkflowStateMachine>.Instance,
            governanceDomainService: domainService,
            capabilities: caps,
            domainRepository: domains,
            tableMetadataRepository: tables);

        var proposal = new TableClassificationProposal(
            Guid.NewGuid(),
            table,
            ProposerSid.Value,
            "PENDING_GOVERNANCE_REVIEW",
            [
                new("email", "varchar", "L3_CONFIDENTIAL", 3, "EMAIL", "MASK", 0.95, false, Reasoning: null, ProposedProtectionClass: ProtectionClass.Confidential, ProposedMaskCategory: ClassificationScopes.PiiCode),
                new("notes", "varchar", "L2_INTERNAL", 2, null, "NONE", 1.0, false, Reasoning: null, ProposedProtectionClass: ProtectionClass.Internal, ProposedMaskCategory: null)
            ],
            T0,
            ProposedBySid: ProposerSid.Value,
            Stage1ApproverSid: ProposerSid.Value);

        // IT admin must be refused classification approval.
        var byIt = await sm.TransitionAsync(
            proposal,
            new TableClassificationApprovalRequest(
                proposal.ProposalId,
                table,
                ItSid,
                "DATA_GOVERNANCE_REVIEWER",
                "APPROVE",
                [],
                TenantId: TenantA,
                Caller: PrincipalFor(ItSid, "TenantAdmin")));
        byIt.Success.ShouldBeFalse();
        byIt.ErrorCode.ShouldBe("CLASSIFY_FORBIDDEN");
        await domains.DidNotReceiveWithAnyArgs().SetTableProtectionAsync(default, default, default!, default!, default, default);

        // Governance reviewer is authorized and approval persists object protection + mask category.
        var byDg = await sm.TransitionAsync(
            proposal,
            new TableClassificationApprovalRequest(
                proposal.ProposalId,
                table,
                ReviewerSid,
                "DATA_GOVERNANCE_REVIEWER",
                "APPROVE",
                [],
                TenantId: TenantA,
                Caller: PrincipalFor(ReviewerSid, "GovernanceAdmin")));
        byDg.Success.ShouldBeTrue(byDg.Message);
        await domains.Received(1).SetTableProtectionAsync(
            TenantA,
            tableId,
            Arg.Is<ObjectProtection>(p => p.Class == ProtectionClass.Internal && p.MaskCategory == null),
            Arg.Any<IReadOnlyList<string>>(),
            ReviewerSid,
            Arg.Any<CancellationToken>());
        await domains.Received(1).SetProtectionAsync(
            TenantA,
            tableId,
            "email",
            Arg.Is<ObjectProtection>(p => p.Class == ProtectionClass.Confidential && p.MaskCategory == ClassificationScopes.PiiCode),
            ReviewerSid,
            Arg.Any<CancellationToken>());
    }
}
