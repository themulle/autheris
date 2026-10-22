namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SyncDataCatalogAuthorizationTests
{
    private static IHttpContextAccessor Accessor(string authType, string tenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-SYNC"), new("tenant_id", tenant) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authType)) };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        return accessor;
    }

    [Fact]
    public async Task SyncDataCatalog_ForwardAuthClusterAdmin_IsForbidden()
    {
        var catalogSync = Substitute.For<IDataCatalogSyncService>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var accessor = Accessor("ForwardAuth", "tenant-a", "ClusterAdmin");

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().SyncDataCatalogAsync(
            dryRun: false,
            catalogSyncService: catalogSync,
            httpContextAccessor: accessor,
            auditWriter: auditWriter));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        await catalogSync.DidNotReceiveWithAnyArgs().SyncCatalogAsync(default, default, default);
    }

    [Fact]
    public async Task SyncDataCatalog_DeveloperAdmin_IsForbidden()
    {
        var catalogSync = Substitute.For<IDataCatalogSyncService>();
        var auditWriter = Substitute.For<IAuditWriter>();
        var accessor = Accessor("Bearer", "tenant-a", "DeveloperAdmin");

        var ex = await Should.ThrowAsync<GraphQLException>(() => new Mutation().SyncDataCatalogAsync(
            dryRun: false,
            catalogSyncService: catalogSync,
            httpContextAccessor: accessor,
            auditWriter: auditWriter));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        await catalogSync.DidNotReceiveWithAnyArgs().SyncCatalogAsync(default, default, default);
    }

    [Fact]
    public async Task SyncDataCatalog_GovernanceAdmin_PassesOwnTenantRestriction()
    {
        var catalogSync = Substitute.For<IDataCatalogSyncService>();
        catalogSync.SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CatalogSyncResult(1, 2, 0, 0, [], [], true)));

        var auditWriter = Substitute.For<IAuditWriter>();
        var accessor = Accessor("Bearer", "tenant-a", "GovernanceAdmin");

        var payload = await new Mutation().SyncDataCatalogAsync(
            dryRun: false,
            catalogSyncService: catalogSync,
            httpContextAccessor: accessor,
            auditWriter: auditWriter);

        payload.Success.ShouldBeTrue();
        await catalogSync.Received(1).SyncCatalogAsync(false, new TenantId("tenant-a"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncDataCatalog_WritesRequiredAuditEvent()
    {
        var catalogSync = Substitute.For<IDataCatalogSyncService>();
        catalogSync.SyncCatalogAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CatalogSyncResult(3, 10, 2, 1, [], [], true)));

        var auditWriter = Substitute.For<IAuditWriter>();
        var accessor = Accessor("Bearer", "tenant-a", "GovernanceAdmin");

        var payload = await new Mutation().SyncDataCatalogAsync(
            dryRun: false,
            catalogSyncService: catalogSync,
            httpContextAccessor: accessor,
            auditWriter: auditWriter);

        payload.SyncedTablesCount.ShouldBe(3);
        await auditWriter.Received().RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == AuditEventTypes.DataCatalogSync &&
                e.Decision == "ALLOW" &&
                e.DetailsJson.Contains("syncedTables")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncDataCatalog_AuditFailure_FailsClosedBeforeSync()
    {
        var catalogSync = Substitute.For<IDataCatalogSyncService>();
        var auditWriter = Substitute.For<IAuditWriter>();
        auditWriter.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Audit store unavailable"));

        var accessor = Accessor("Bearer", "tenant-a", "GovernanceAdmin");

        await Should.ThrowAsync<InvalidOperationException>(() => new Mutation().SyncDataCatalogAsync(
            dryRun: false,
            catalogSyncService: catalogSync,
            httpContextAccessor: accessor,
            auditWriter: auditWriter));

        await catalogSync.DidNotReceiveWithAnyArgs().SyncCatalogAsync(default, default, default);
    }
}
