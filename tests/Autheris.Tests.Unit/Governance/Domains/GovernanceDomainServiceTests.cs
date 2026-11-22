using System.Security.Claims;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Protection;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Domains;

[Trait("Category", "Security")]
public sealed class GovernanceDomainServiceTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid GovSid = new("S-1-5-21-1001-2002-3003-10");
    private static readonly Sid OwnerSid = new("S-1-5-21-1001-2002-3003-20");
    private static readonly Sid OwnerGroupSid = new("S-1-5-21-1001-2002-3003-512");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static SecurityPrincipalContext MakeCaller(Sid sid, string role, IReadOnlySet<Sid>? groups = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid.Value),
            new("tenant_id", TenantA.Value),
            new(ClaimTypes.Role, role)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: TenantA) with
        {
            GroupSids = groups ?? new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
    }

    private static (GovernanceDomainService Service, IGovernanceDomainRepository Domains, InMemoryTableMetadataRepository Tables) Build()
    {
        var domains = Substitute.For<IGovernanceDomainRepository>();
        var tables = new InMemoryTableMetadataRepository();
        var consents = Substitute.For<IConsentRepository>();
        var audit = new InMemoryAuditWriter();
        var caps = new GovernanceCapabilities(domainRepository: domains);
        var service = new GovernanceDomainService(domains, tables, consents, audit, caps, timeProvider: new FakeTimeProvider(T0));
        return (service, domains, tables);
    }

    [Fact]
    public async Task Sid_and_tid_format_validated()
    {
        var (service, domains, _) = Build();
        var gov = MakeCaller(GovSid, "GovernanceAdmin");
        var domainId = Guid.NewGuid();
        var existing = new GovernanceDomain(domainId, TenantA, "Finance", null, null, null, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, T0, T0);
        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>()).Returns(existing);

        var badSid = await service.AssignOwnerGroupAsync(gov, TenantA, domainId, "not-a-sid");
        badSid.Status.ShouldBe(DomainServiceStatus.Invalid);
        badSid.ErrorCode.ShouldBe("INVALID_OWNER_GROUP_SID");

        var emailSid = await service.AssignOwnerGroupAsync(gov, TenantA, domainId, "owner@example.com");
        emailSid.Status.ShouldBe(DomainServiceStatus.Invalid);

        var badGuid = await service.AssignOwnerGroupAsync(gov, TenantA, domainId, Guid.NewGuid().ToString("D"));
        badGuid.Status.ShouldBe(DomainServiceStatus.Invalid);

        domains.UpsertAsync(Arg.Any<GovernanceDomain>(), 1, GovSid, Arg.Any<CancellationToken>())
            .Returns(call => new GovernanceDomainUpsertResult(
                GovernanceDomainWriteStatus.Updated,
                call.ArgAt<GovernanceDomain>(0) with { Version = 2 }));

        var good = await service.AssignOwnerGroupAsync(gov, TenantA, domainId, OwnerGroupSid.Value);
        good.Status.ShouldBe(DomainServiceStatus.Ok);
        good.Value!.OwnerGroupSid.ShouldBe(OwnerGroupSid);
    }

    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JAVASCRIPT:alert(1)", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("data:text/html,<script>", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://files.example.com", false)]
    [InlineData("mailto:stewards@example.com", true)]
    [InlineData("https://intranet.example.com/stewards", true)]
    [InlineData("stewards@example.com", true)]
    public async Task Contact_scheme_restricted(string contact, bool expectedAllowed)
    {
        var (service, domains, _) = Build();
        var gov = MakeCaller(GovSid, "GovernanceAdmin");
        domains.UpsertAsync(Arg.Any<GovernanceDomain>(), 0, GovSid, Arg.Any<CancellationToken>())
            .Returns(call => new GovernanceDomainUpsertResult(
                GovernanceDomainWriteStatus.Created,
                call.ArgAt<GovernanceDomain>(0) with { Version = 1 }));

        var res = await service.CreateDomainAsync(gov, TenantA, new CreateGovernanceDomainInput("HR", Contact: contact));

        if (expectedAllowed)
        {
            res.Status.ShouldBe(DomainServiceStatus.Created);
        }
        else
        {
            res.Status.ShouldBe(DomainServiceStatus.Invalid);
        }
    }

    [Fact]
    public async Task Lengths_bounded()
    {
        var (service, _, _) = Build();
        var gov = MakeCaller(GovSid, "GovernanceAdmin");

        (await service.CreateDomainAsync(gov, TenantA, new CreateGovernanceDomainInput(new string('A', 129)))).Status.ShouldBe(DomainServiceStatus.Invalid);
        (await service.CreateDomainAsync(gov, TenantA, new CreateGovernanceDomainInput("HR", Description: new string('D', 1025)))).Status.ShouldBe(DomainServiceStatus.Invalid);
        (await service.CreateDomainAsync(gov, TenantA, new CreateGovernanceDomainInput("HR", Contact: new string('C', 257)))).Status.ShouldBe(DomainServiceStatus.Invalid);
        (await service.CreateDomainAsync(gov, TenantA, new CreateGovernanceDomainInput("HR\nBad"))).Status.ShouldBe(DomainServiceStatus.Invalid);
        (await service.CreateDomainAsync(gov, TenantA, new CreateGovernanceDomainInput("Unassigned"))).Status.ShouldBe(DomainServiceStatus.Invalid);
    }

    [Fact]
    public async Task Owner_member_can_classify_while_it_cannot()
    {
        var (service, domains, tables) = Build();
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var tableIdRef = new TableIdentifier("crm", "public", "customers");

        domains.GetAsync(TenantA, domainId, Arg.Any<CancellationToken>())
            .Returns(new GovernanceDomain(domainId, TenantA, "CRM", null, null, OwnerGroupSid, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, T0, T0));
        domains.GetContainerForTableAsync(TenantA, tableId, Arg.Any<CancellationToken>())
            .Returns(new ContainerDefaults(new ContainerRef(ContainerKind.Domain, domainId), OwnerGroupSid, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, IsValid: true));
        domains.GetProtectionAsync(TenantA, tableId, Arg.Any<CancellationToken>())
            .Returns((null, new Dictionary<string, ObjectProtection>()));

        tables.Add(new TableMetadata
        {
            Identifier = tableIdRef,
            OwnerTenantId = TenantA.Value,
            Table = new Table { Id = tableId, SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true },
            Columns = [new TableColumn { ColumnName = "email" }]
        });

        var itCaller = MakeCaller(new Sid("S-1-5-21-999"), "TenantAdmin");
        var itResult = await service.SetTableProtectionAsync(itCaller, TenantA, tableIdRef.ToString(), ProtectionClass.Internal, null);
        itResult.Status.ShouldBe(DomainServiceStatus.Forbidden);

        var ownerCaller = MakeCaller(OwnerSid, "Consumer", new HashSet<Sid> { OwnerGroupSid });
        var ownerResult = await service.SetTableProtectionAsync(ownerCaller, TenantA, tableIdRef.ToString(), ProtectionClass.Internal, null);
        ownerResult.Status.ShouldBe(DomainServiceStatus.Ok);
        ownerResult.Value!.Effective.TableClass.ShouldBe(ProtectionClass.Internal);

        var unknownCol = await service.SetColumnProtectionAsync(ownerCaller, TenantA, tableIdRef.ToString(), "non_existent", ProtectionClass.Secret, null);
        unknownCol.Status.ShouldBe(DomainServiceStatus.NotFound);
    }
}
