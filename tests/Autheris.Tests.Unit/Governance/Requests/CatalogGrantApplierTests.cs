using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

[Trait("Category", "Security")]
public sealed class CatalogGrantApplierTests
{
    [Fact]
    public async Task Container_bound_tenant_bound_and_never_writes_to_consents()
    {
        CatalogVisibilityGrant? saved = null;
        var catalogRepo = NullFake.Create<ICatalogVisibilityGrantRepository>((m, args) =>
        {
            if (m.Name == nameof(ICatalogVisibilityGrantRepository.CreateAsync))
            {
                saved = (CatalogVisibilityGrant)args![0]!;
                return Task.FromResult(saved);
            }

            return NullFake.UseDefault;
        });

        var applier = new CatalogGrantApplier(catalogRepo);
        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
        var containerId = Guid.NewGuid();
        var tx = NullFake.Create<IGovernanceTransaction>();

        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId("tenant-a"),
            RequesterSid = new Sid("user:alice"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:alice",
            BusinessJustification = "Browse finance domain metadata.",
            RequestKind = ConsentRequestKind.Catalog,
            GrantKind = PermissionGrantKind.Catalog,
            ContainerKind = ContainerKind.Domain,
            ContainerId = containerId
        };

        var refs = await applier.ApplyAsync(tx, new ApprovedPermission(
            req, new Sid("user:it-admin"), ApproverCapacity.ItAdmin, SelfGrant: false, now, now, now.AddDays(365)));

        refs.Count.ShouldBe(1);
        refs[0].GrantKind.ShouldBe(PermissionGrantKind.Catalog);
        refs[0].ContainerKind.ShouldBe(ContainerKind.Domain);
        refs[0].ContainerId.ShouldBe(containerId);
        saved.ShouldNotBeNull();
        saved.TenantId.ShouldBe(new TenantId("tenant-a"));
        saved.ContainerKind.ShouldBe(ContainerKind.Domain);
        saved.ContainerId.ShouldBe(containerId);
        saved.ApproverCapacity.ShouldBe(ApproverCapacity.ItAdmin);
    }

    [Fact]
    public async Task Missing_container_rejected()
    {
        var catalogRepo = NullFake.Create<ICatalogVisibilityGrantRepository>();
        var applier = new CatalogGrantApplier(catalogRepo);
        var now = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
        var req = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId("tenant-a"),
            RequesterSid = new Sid("user:alice"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "user:alice",
            RequestKind = ConsentRequestKind.Catalog
        };

        await Should.ThrowAsync<ConsentDecisionException>(() => applier.ApplyAsync(
            NullFake.Create<IGovernanceTransaction>(),
            new ApprovedPermission(req, new Sid("user:dg"), ApproverCapacity.Governance, false, now, now, now.AddDays(30))));
    }
}
