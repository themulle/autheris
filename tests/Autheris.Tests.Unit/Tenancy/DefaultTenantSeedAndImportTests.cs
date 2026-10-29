using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Tenancy;

/// <summary>DT-A-9 (WP-DT-2a): seeds and importers use the configured default tenant and never invent one.</summary>
public sealed class DefaultTenantSeedAndImportTests
{
    private static RebacSeedHostedService Seed(IRebacStore store, IAuditLogRepository audit, ITenantResolver? resolver)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        return new RebacSeedHostedService(
            store,
            Options.Create(new GatewayOptions()),
            NullLogger<RebacSeedHostedService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(audit),
            env,
            resolver);
    }

    [Fact]
    public async Task Rebac_Seed_Uses_Default_Tenant()
    {
        var store = Substitute.For<IRebacStore>();
        var audit = Substitute.For<IAuditLogRepository>();
        var resolver = Substitute.For<ITenantResolver>();
        resolver.DefaultTenant.Returns(new TenantId("tenant-d"));

        await Seed(store, audit, resolver).StartAsync(CancellationToken.None);

        await store.Received(1).AddTuplesAsync(
            Arg.Is<IReadOnlyList<RebacTuple>>(list => list.Any(t => t.TenantId == "tenant-d") && list.All(t => t.TenantId != "default")),
            Arg.Any<CancellationToken>());
        await audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.RebacTupleSeed && e.TenantId == TenantId.Platform),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rebac_Seed_Skipped_Without_Default()
    {
        var store = Substitute.For<IRebacStore>();
        var resolver = Substitute.For<ITenantResolver>();
        resolver.DefaultTenant.Returns((TenantId?)null);

        await Seed(store, Substitute.For<IAuditLogRepository>(), resolver).StartAsync(CancellationToken.None);

        await store.DidNotReceiveWithAnyArgs().AddTuplesAsync(default!, default);
    }

    [Fact]
    public void OpenMetadata_Entry_Without_Mapping_Uses_Default_Tenant_Else_Is_Skipped()
    {
        var options = new OpenMetadataOptions();

        OpenMetadataTableIdentity.TryResolveTenant("svc", "db", options, out var withDefault, out _, new TenantId("tenant-d")).ShouldBeTrue();
        withDefault.Value.ShouldBe("tenant-d");

        OpenMetadataTableIdentity.TryResolveTenant("svc", "db", options, out _, out var reason).ShouldBeFalse();
        reason.ShouldContain("no tenant mapping");

        new OpenMetadataOptions().DefaultTenantId.ShouldBeNull();
    }
}
