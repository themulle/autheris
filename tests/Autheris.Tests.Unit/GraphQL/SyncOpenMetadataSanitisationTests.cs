namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SR-L-23: syncOpenMetadata authorises via the canonical cluster admin definition or GovernanceAdmin, scopes
/// non-cluster-admins to their tenant and never returns exception text or foreign table names.
/// </summary>
[Trait("Category", "Security")]
public sealed class SyncOpenMetadataSanitisationTests
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
    public async Task SyncFailure_ReturnsNoExceptionText()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        sync.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<TenantId?>())
            .Returns<Task<OpenMetadataSyncResult>>(_ => throw new InvalidOperationException("Server=db.internal;Password=hunter2 table other_tenant.secret"));

        var ex = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sync, Accessor("Bearer", "tenant-a", "GovernanceAdmin")));

        ex.Errors[0].Code.ShouldBe("SYNC_FAILED");
        ex.Message.ShouldNotContain("hunter2");
        ex.Message.ShouldNotContain("other_tenant");
    }

    [Fact]
    public async Task Warnings_AreReturnedAsCodesWithCounts()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        sync.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<TenantId?>())
            .Returns(Task.FromResult(new OpenMetadataSyncResult(1, 0, 0, [], [
                "Failed to map or upsert table tenant_b.db.dbo.secret: boom",
                "Failed to map or upsert table tenant_b.db.dbo.other: boom"
            ], true)));

        var payload = await new Mutation().SyncOpenMetadataAsync(false, sync, Accessor("Bearer", "tenant-a", "GovernanceAdmin"));

        payload.Warnings.ShouldBe(["OM_TABLE_MAP_FAILED: 2"]);
    }

    [Fact]
    public async Task NonClusterAdmin_OtherTenantTable_NotSynced()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        sync.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<TenantId?>())
            .Returns(Task.FromResult(new OpenMetadataSyncResult(0, 0, 0, [], [], true)));

        await new Mutation().SyncOpenMetadataAsync(false, sync, Accessor("Bearer", "tenant-a", "GovernanceAdmin"));

        await sync.Received(1).SyncPermissionsAsync(false, Arg.Any<CancellationToken>(), new TenantId("tenant-a"));
    }

    [Fact]
    public async Task ClusterAdmin_SyncsWithoutTenantRestriction()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        sync.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<TenantId?>())
            .Returns(Task.FromResult(new OpenMetadataSyncResult(0, 0, 0, [], [], true)));

        await new Mutation().SyncOpenMetadataAsync(false, sync, Accessor("Bearer", "tenant-a", "ClusterAdmin"));

        await sync.Received(1).SyncPermissionsAsync(false, Arg.Any<CancellationToken>(), (TenantId?)null);
    }

    [Fact]
    public async Task RawClusterAdminRoleStringWithoutCanonicalClaims_Denied()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();

        // A ForwardAuth identity asserting the ClusterAdmin role string is never a canonical cluster admin.
        var ex = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sync, Accessor("ForwardAuth", "tenant-a", "ClusterAdmin")));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        await sync.DidNotReceiveWithAnyArgs().SyncPermissionsAsync(default, default, default);
    }
}
