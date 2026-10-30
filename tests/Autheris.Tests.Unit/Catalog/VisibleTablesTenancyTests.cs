namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>
/// T-W0-16: every consumer of <c>CatalogVisibility.VisibleTablesAsync</c> (Trino, Arrow Flight SQL, Iceberg, guardrail, <c>describe_my_access</c>, MCP)
/// shares one confinement rule. Pinned here on the helper and on the Trino metadata handler; the Arrow, Iceberg and explanation services
/// have their own suites that run in <c>Single</c> mode.
/// </summary>
[Trait("Category", "Security")]
public sealed class VisibleTablesTenancyTests
{
    private static readonly TableMetadata OrdersA = Wp0.Table("sales", "orders_a", columns: ["id"]);
    private static readonly TableMetadata InvoicesB = Wp0.Table("sales", "invoices_b", columns: ["id"]);
    private static readonly TableMetadata Countries = Wp0.Table("ref", "countries", columns: ["code"]);

    private static Task<System.Collections.Generic.IReadOnlyList<TableMetadata>> VisibleAsync(
        System.Security.Claims.ClaimsPrincipal user, TenancyShape mode, string tenantForConsents = "default-tenant")
    {
        var consents = Wp0.Consents(
            Wp0.Allow(InvoicesB.Identifier, "S-B-OWNER", Wp0.TenantB),
            Wp0.Allow(OrdersA.Identifier, "S-A-OWNER", Wp0.TenantA),
            Wp0.Allow(Countries.Identifier, "S-CLAIMLESS", Autheris.Tests.Common.TestTenants.Default));
        return CatalogVisibility.VisibleTablesAsync(
            [OrdersA, InvoicesB, Countries], user, new TenantId(tenantForConsents), consents, contract: null, mode.Owner(),
            sharedTables: ["ref.dbo.countries"], default);
    }

    private static string[] Names(System.Collections.Generic.IEnumerable<TableMetadata> tables) => tables.Select(t => t.Identifier.TableName).OrderBy(n => n).ToArray();

    [Theory]
    [InlineData(TenancyShape.Multi)]
    [InlineData(TenancyShape.Single)]
    public async Task TenantGovernanceAdmin_IsConfinedToItsTenantAndSharedTables(TenancyShape mode)
        => Names(await VisibleAsync(Wp0.User("S-B-ADMIN", "tenant-b", ["GovernanceAdmin"]), mode, "tenant-b")).ShouldBe(["countries", "invoices_b"]);

    [Fact]
    public async Task ClaimlessGovernanceAdmin_Multi_SeesNothing()
        => Names(await VisibleAsync(Wp0.User("S-CLAIMLESS", null, ["GovernanceAdmin"]), TenancyShape.Multi)).ShouldBeEmpty();

    [Fact]
    public async Task ClaimlessGovernanceAdmin_Single_SeesEverything()
        => Names(await VisibleAsync(Wp0.User("S-CLAIMLESS", null, ["GovernanceAdmin"]), TenancyShape.Single)).ShouldBe(["countries", "invoices_b", "orders_a"]);

    [Theory]
    [InlineData(TenancyShape.Multi)]
    [InlineData(TenancyShape.Single)]
    public async Task ClusterAdmin_SeesEverything(TenancyShape mode)
        => Names(await VisibleAsync(Wp0.User("root", "tenant-b", ["ClusterAdmin"]), mode, "tenant-b")).ShouldBe(["countries", "invoices_b", "orders_a"]);

    [Fact]
    public async Task OverloadsWithoutTenantSelection_UseTheConfinedMultiView()
    {
        var consents = Wp0.Consents();
        var admin = Wp0.User("S-ADM", null, ["GovernanceAdmin"]);

        (await CatalogVisibility.VisibleTablesAsync([OrdersA], admin, Autheris.Tests.Common.TestTenants.Default, consents, default)).ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidTenantClaim_YieldsNothing()
        => (await VisibleAsync(Wp0.User("S-X", "bad tenant!!", ["GovernanceAdmin"]), TenancyShape.Single)).ShouldBeEmpty();

    [Fact]
    public async Task TrinoShowTables_FollowsTheSameRule()
    {
        var tables = Wp0.Tables(OrdersA, InvoicesB);
        var consents = Wp0.Consents(Wp0.Allow(InvoicesB.Identifier, "S-B-OWNER", Wp0.TenantB));
        var adminB = Wp0.User("S-B-ADMIN", "tenant-b", ["GovernanceAdmin"]);
        var claimless = Wp0.User("S-ADM", null, ["GovernanceAdmin"]);

        var confined = await TrinoMetadataQueryHandler.TryExecuteAsync("SHOW TABLES FROM sales.dbo", adminB, Wp0.TenantB, tables, consents, Wp0.Options(TenancyShape.Multi));
        confined!.Rows.Select(r => r["Table"]?.ToString()).ShouldBe(["invoices_b"]);

        var multi = await TrinoMetadataQueryHandler.TryExecuteAsync("SHOW TABLES FROM sales.dbo", claimless, Autheris.Tests.Common.TestTenants.Default, tables, consents, Wp0.Options(TenancyShape.Multi));
        multi!.Rows.ShouldBeEmpty();

        var single = await TrinoMetadataQueryHandler.TryExecuteAsync("SHOW TABLES FROM sales.dbo", claimless, Autheris.Tests.Common.TestTenants.Default, tables, consents, Wp0.Options(TenancyShape.Single));
        single!.Rows.Select(r => r["Table"]?.ToString()).ShouldBe(["orders_a", "invoices_b"], ignoreOrder: true);
    }
}
