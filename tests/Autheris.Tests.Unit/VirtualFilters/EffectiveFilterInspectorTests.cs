namespace Autheris.Tests.Unit.VirtualFilters;

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-07: authorization and audit of the effective-filters inspection live in the use case.</summary>
public sealed class EffectiveFilterInspectorTests
{
    private static readonly TableMetadata Air1 = MandatoryRowFilterResolverTests.Table("fms", "air1", "id", "client_id");

    private static MandatoryRowFilterResolver Resolver() =>
        new(new MandatoryRowFilterResolverTests.FixedSnapshot(new VirtualFilterSnapshot(1, [], [])), new MandatoryRowFilterResolverTests.PlaceholderPredicates());

    private static InMemoryTableMetadataRepository Catalog() => new(Air1);

    private static SecurityPrincipalContext Caller(params string[] roles) => new()
    {
        UserSid = new Sid("S-1-5-21-AUDITOR"),
        TenantId = MandatoryRowFilterResolverTests.Tenant,
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(roles.Where(r => r != "ClusterAdmin")),
        ClusterRoles = new HashSet<string>(roles.Where(r => r == "ClusterAdmin")),
        AuthenticationScheme = "Test"
    };

    private static EffectiveFilterQuery Query(string? tenant = null, string? table = "lwetem_prod.fms.air1") =>
        new("S-1-5-21-DAVID", null, null, tenant, table, null, null);

    [Fact]
    public async Task NonAdminCaller_IsDenied_AndDenialIsAudited()
    {
        var audit = new InMemoryAuditWriter();
        var catalog = Catalog();
        var sut = new EffectiveFilterInspector(Resolver(), catalog, audit);

        var result = await sut.InspectAsync(Caller("Consumer"), Query(), CancellationToken.None);

        result.Kind.ShouldBe(EffectiveFilterInspectionKind.Forbidden);
        audit.Entries.Count(e => e.EventType == "VIRTUAL_FILTER_ADMIN_DENIED" && e.Decision == "DENY" && e.TargetTable == "effective-filters").ShouldBe(1);
        audit.Entries.ShouldNotContain(e => e.EventType == "VIRTUAL_FILTER_EFFECTIVE_INSPECTED");
        catalog.GetCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NonClusterAdmin_OtherTenant_IsForbidden()
    {
        var audit = new InMemoryAuditWriter();
        var sut = new EffectiveFilterInspector(Resolver(), Catalog(), audit);

        var result = await sut.InspectAsync(Caller("GovernanceAdmin"), Query(tenant: "other-tenant"), CancellationToken.None);

        result.Kind.ShouldBe(EffectiveFilterInspectionKind.Forbidden);
        audit.Entries.ShouldNotContain(e => e.EventType == "VIRTUAL_FILTER_EFFECTIVE_INSPECTED");
    }

    [Fact]
    public async Task AuditStoreDown_FailsClosed()
    {
        var audit = new InMemoryAuditWriter(new AuditUnavailableException("down"));
        var sut = new EffectiveFilterInspector(Resolver(), Catalog(), audit);

        await Should.ThrowAsync<AuditUnavailableException>(() => sut.InspectAsync(Caller("GovernanceAdmin"), Query(), CancellationToken.None));
        await Should.ThrowAsync<AuditUnavailableException>(() => sut.InspectAsync(Caller("Consumer"), Query(), CancellationToken.None));
    }

    [Fact]
    public async Task ListMode_ClampsPageSize_AndAuditsOnce()
    {
        var audit = new InMemoryAuditWriter();
        var sut = new EffectiveFilterInspector(Resolver(), Catalog(), audit);

        var result = await sut.InspectAsync(Caller("SecurityAuditor"), new("S-1-5-21-DAVID", null, null, null, null, "1", "9999"), CancellationToken.None);

        result.Kind.ShouldBe(EffectiveFilterInspectionKind.Page);
        result.PageSize.ShouldBe(500);
        result.Total.ShouldBe(1);
        audit.Entries.Count(e => e.TargetTable == "all_tables").ShouldBe(1);
    }
}
