namespace Autheris.Tests.Unit.Identity.Attributes;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static CallerAttributeTestSupport;

/// <summary>GOV-12: the shared materialisation guard (A-10 across tenants, registry unavailable fails closed).</summary>
[Trait("Category", "Security")]
public sealed class ConsentMaterializationGuardTests
{
    private static readonly TableIdentifier Source = new("hr_db", "main", "person_attributes");

    private static CallerAttributeSnapshot SnapshotWithSource() => new(1,
    [
        Definition("dept", "attribute-table", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60,
            ("dataSource", "hr_db"), ("schema", "main"), ("table", "person_attributes"), ("subjectKeyKind", "EntraObjectId"),
            ("subjectKeyColumn", "person_oid"), ("valueColumn", "cost_center"), ("tenantColumn", "tenant_id"))
    ], []);

    private static CallerAttributeConsentGuard Guard(Func<CallerAttributeSnapshot> snapshot, CallerAttributeOptions? options = null)
    {
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(snapshot()));
        var tables = Substitute.For<ITableMetadataRepository>();
        return new CallerAttributeConsentGuard(
            () => registry, () => tables, Options.Create(new GatewayOptions { CallerAttributes = options ?? Options(true) }));
    }

    [Fact]
    public async Task AttributeSourceOfOtherTenant_Refused()
    {
        // The definition belongs to tenant_a; the consent is requested in tenant_b on the same physical table.
        var guard = Guard(SnapshotWithSource);

        await Should.ThrowAsync<ConsentOnAttributeSourceException>(async () =>
            await guard.EnsureMayMaterializeAsync(new TenantId("tenant_b"), Source, []));
    }

    [Fact]
    public async Task AttributeSourceOfSameTenant_Refused()
    {
        var guard = Guard(SnapshotWithSource);

        var ex = await Should.ThrowAsync<ConsentOnAttributeSourceException>(async () =>
            await guard.EnsureMayMaterializeAsync(Tenant, Source, []));
        ex.Code.ShouldBe("CONSENT_ON_ATTRIBUTE_SOURCE");
    }

    [Fact]
    public async Task AllowListedSource_Allowed()
    {
        var guard = Guard(SnapshotWithSource, new CallerAttributeOptions { Enabled = true, AllowAttributeSourceInCatalog = ["main.person_attributes"] });

        await guard.EnsureMayMaterializeAsync(new TenantId("tenant_b"), Source, []);
    }

    [Fact]
    public async Task OrdinaryTable_Allowed()
    {
        var guard = Guard(SnapshotWithSource);

        await guard.EnsureMayMaterializeAsync(Tenant, new TableIdentifier("hr_db", "main", "orders"), []);
    }

    [Fact]
    public async Task RegistryUnavailable_Throws()
    {
        var guard = Guard(() => throw new InvalidOperationException("registry unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await guard.EnsureMayMaterializeAsync(Tenant, new TableIdentifier("hr_db", "main", "orders"), []));
    }
}
