using System.Security.Claims;
using Autheris.Application.Audit;
using Autheris.Application.Products;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ViewDowngradeGuardTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid GovSid = new("S-1-5-21-100-200-300-10");
    private static readonly Sid OwnerSid = new("S-1-5-21-100-200-300-20");
    private static readonly Sid ItSid = new("S-1-5-21-100-200-300-30");
    private static readonly Sid OwnerGroupSid = new("S-1-5-21-100-200-300-512");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static (ViewDowngradeGuard Guard, InMemoryDataProductRepository Products, RecordingAuditWriter Audit, DataProduct Product) Build()
    {
        var products = new InMemoryDataProductRepository();
        var audit = new RecordingAuditWriter();
        var capabilities = new GovernanceCapabilities(productRepository: products);
        var guard = new ViewDowngradeGuard(capabilities, audit, new FakeTimeProvider(T0));

        var table = new TableIdentifier("crm", "public", "customers");
        var product = new DataProduct(
            Guid.NewGuid(),
            TenantA,
            "sales",
            "customer_summary",
            DataProductKind.View,
            "SELECT email FROM crm.public.customers",
            "hash",
            [table],
            [new DataProductColumn("email", "varchar", ProductColumnClass.PiiMasked, [new ColumnLineage(table, "email")], null, null)],
            OwnerSid,
            ProductCreatorAuthority.BaseOwner,
            DataProductStatus.Active,
            null,
            LoadSettings.Default,
            ProductLimits.Default,
            "Customer summary",
            OwnerGroupSid: OwnerGroupSid,
            DefaultClass: ProtectionClass.Confidential,
            Visibility: CatalogVisibilityMode.OnRequest,
            Version: 1,
            CreatedAt: T0,
            UpdatedAt: T0);

        products.CreateProductAsync(product, new DataProductVersion(product.Id, 1, product.DefinitionHash, product.Definition, OwnerSid, product.Authority, T0))
            .GetAwaiter().GetResult();

        return (guard, products, audit, product);
    }

    private static SecurityPrincipalContext Caller(Sid sid, string role, params Sid[] groups)
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
            GroupSids = new HashSet<Sid>(groups),
            GroupResolutionStatus = GroupResolutionStatus.Complete
        };
    }

    [Fact]
    public async Task Only_dg_or_owner_member()
    {
        var (guard, _, _, product) = Build();
        var column = product.Columns[0];

        var byDg = await guard.GuardAndAuditAsync(
            Caller(GovSid, "GovernanceAdmin"),
            product,
            column,
            ProductColumnClass.PiiClearEligible,
            "Authorized compliance reporting requirement.");
        byDg.Class.ShouldBe(ProductColumnClass.PiiClearEligible);

        var byOwner = await guard.GuardAndAuditAsync(
            Caller(OwnerSid, "Consumer", OwnerGroupSid),
            product,
            column,
            ProductColumnClass.NonPii,
            "Verified aggregate bucket with zero personal data.");
        byOwner.Class.ShouldBe(ProductColumnClass.NonPii);

        var stranger = await Should.ThrowAsync<ProductException>(() => guard.GuardAndAuditAsync(
            Caller(ItSid, "Consumer"),
            product,
            column,
            ProductColumnClass.PiiClearEligible,
            "Unauthorized caller trying to downgrade."));
        stranger.Failure.ShouldBe(ProductFailure.Forbidden);
        stranger.Code.ShouldBe(ProductErrorCodes.CreationNotAuthorized);
    }

    [Fact]
    public async Task It_refused()
    {
        var (guard, _, audit, product) = Build();
        var itCaller = Caller(ItSid, "TenantAdmin");

        var ex = await Should.ThrowAsync<ProductException>(() => guard.GuardAndAuditAsync(
            itCaller,
            product,
            product.Columns[0],
            ProductColumnClass.NonPii,
            "IT admin attempting data protection downgrade."));

        ex.Failure.ShouldBe(ProductFailure.Forbidden);
        ex.Code.ShouldBe(ProductErrorCodes.CreationNotAuthorized);
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.ProductAccessDenied && e.Decision == "DENY");
        audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.ViewDowngraded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   short ")]
    [InlineData("123456789")]
    public async Task Short_justification_rejected(string? reason)
    {
        var (guard, _, audit, product) = Build();
        var dg = Caller(GovSid, "GovernanceAdmin");

        var ex = await Should.ThrowAsync<ProductException>(() => guard.GuardAndAuditAsync(
            dg,
            product,
            product.Columns[0],
            ProductColumnClass.PiiClearEligible,
            reason));

        ex.Failure.ShouldBe(ProductFailure.Invalid);
        ex.Code.ShouldBe(ProductErrorCodes.ReasonRequired);
        audit.Entries.ShouldNotContain(e => e.EventType == AuditEventTypes.ViewDowngraded);
    }

    [Fact]
    public async Task Audited()
    {
        var (guard, _, audit, product) = Build();
        var dg = Caller(GovSid, "GovernanceAdmin");

        await guard.GuardAndAuditAsync(
            dg,
            product,
            product.Columns[0],
            ProductColumnClass.PiiClearEligible,
            "Business analytics requires clear identifier");

        var row = audit.Entries.Where(e => e.EventType == AuditEventTypes.ViewDowngraded).ShouldHaveSingleItem();
        row.ActorSid.ShouldBe(GovSid);
        row.TenantId.ShouldBe(TenantA);
        row.Decision.ShouldBe("ALLOW");
        row.TargetTable.ShouldBe("products.sales.customer_summary");
        row.TargetColumn.ShouldBe("email");
        row.DetailsJson.ShouldContain("PiiMasked");
        row.DetailsJson.ShouldContain("PiiClearEligible");
        row.DetailsJson.ShouldContain("Business analytics requires clear identifier");
    }
}
