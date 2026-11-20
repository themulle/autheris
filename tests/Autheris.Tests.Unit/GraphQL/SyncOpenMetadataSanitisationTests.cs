namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Models;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SR-L-23: syncOpenMetadata authorises via the canonical cluster admin definition or GovernanceAdmin, scopes
/// non-cluster-admins to their tenant and never returns exception text or foreign table names.
/// </summary>
public sealed class SyncOpenMetadataSanitisationTests
{
    private static IHttpContextAccessor Accessor(string authType, string? tenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-SYNC") };
        if (tenant is not null)
        {
            claims.Add(new("tenant_id", tenant));
        }

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = string.IsNullOrEmpty(authType)
            ? new ClaimsIdentity()
            : new ClaimsIdentity(claims, authType);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);
        return accessor;
    }

    private static ServiceProvider ProviderFor(
        IOpenMetadataSyncService? sync = null,
        IDataCatalogClient? catalogClient = null,
        IDataCatalogSyncService? catalogSync = null)
    {
        var services = new ServiceCollection();
        if (sync is not null)
        {
            services.AddSingleton(sync);
        }

        if (catalogClient is not null)
        {
            services.AddSingleton(catalogClient);
        }

        if (catalogSync is not null)
        {
            services.AddSingleton(catalogSync);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SyncFailure_ReturnsNoExceptionText()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        sync.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns<Task<OpenMetadataSyncResult>>(_ => throw new InvalidOperationException("Server=db.internal;Password=hunter2 table other_tenant.secret"));
        await using var sp = ProviderFor(sync: sync);

        var ex = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "GovernanceAdmin")));

        ex.Errors[0].Code.ShouldBe("SYNC_FAILED");
        ex.Message.ShouldNotContain("hunter2");
        ex.Message.ShouldNotContain("other_tenant");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Warnings_AreReturnedAsCodesWithCounts()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        sync.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenMetadataSyncResult(1, 0, 0, [], [
                "Failed to map or upsert table tenant_b.db.dbo.secret: boom",
                "Failed to map or upsert table tenant_b.db.dbo.other: boom"
            ], true)));
        await using var sp = ProviderFor(sync: sync);

        var payload = await new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "GovernanceAdmin"));

        payload.Warnings.ShouldBe(["OM_TABLE_MAP_FAILED: 2"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task NonClusterAdmin_OtherTenantTable_NotSynced()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        sync.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenMetadataSyncResult(0, 0, 0, [], [], true)));
        await using var sp = ProviderFor(sync: sync);

        await new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "GovernanceAdmin"));

        await sync.Received(1).SyncPermissionsAsync(false, new TenantId("tenant-a"), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ClusterAdmin_SyncsWithoutTenantRestriction()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        sync.SyncPermissionsAsync(Arg.Any<bool>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OpenMetadataSyncResult(0, 0, 0, [], [], true)));
        await using var sp = ProviderFor(sync: sync);

        await new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "ClusterAdmin"));

        await sync.Received(1).SyncPermissionsAsync(false, (TenantId?)null, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RawClusterAdminRoleStringWithoutCanonicalClaims_Denied()
    {
        var sync = Substitute.For<IOpenMetadataSyncService>();
        await using var sp = ProviderFor(sync: sync);

        // A ForwardAuth identity asserting the ClusterAdmin role string is never a canonical cluster admin.
        var ex = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("ForwardAuth", "tenant-a", "ClusterAdmin")));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        await sync.DidNotReceiveWithAnyArgs().SyncPermissionsAsync(default, default, default);
    }

    [Fact]
    public async Task Sync_mutation_returns_unavailable_without_module()
    {
        await using var sp = ProviderFor();

        var ex = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "GovernanceAdmin")));

        ex.Errors[0].Code.ShouldBe("UNAVAILABLE");
        ex.Errors[0].Message.ShouldBe("OpenMetadata is not available.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Availability_checked_after_role_and_tenant_check()
    {
        await using var sp = ProviderFor();

        var unauth = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("", null)));
        unauth.Errors[0].Code.ShouldBe("UNAUTHORIZED");

        var nonAdmin = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "Reader")));
        nonAdmin.Errors[0].Code.ShouldBe("FORBIDDEN");

        var invalidTenant = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "bad tenant!", "GovernanceAdmin")));
        invalidTenant.Errors[0].Code.ShouldBe("UNAUTHORIZED");

        var validAdmin = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "GovernanceAdmin")));
        validAdmin.Errors[0].Code.ShouldBe("UNAVAILABLE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Non_admin_without_module_gets_forbidden_not_unavailable()
    {
        await using var sp = ProviderFor();

        var ex = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "DeveloperAdmin")));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
    }

    [Fact]
    public async Task SyncCatalog_returns_unavailable_without_catalog_client()
    {
        await using var sp = ProviderFor(catalogSync: new StubCatalogSyncService());

        var ex = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncDataCatalogAsync(false, sp, Accessor("Bearer", "tenant-a", "GovernanceAdmin")));

        ex.Errors[0].Code.ShouldBe("UNAVAILABLE");
        ex.Errors[0].Message.ShouldBe("The data catalog is not available.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SyncDataCatalog_without_client_does_not_fail_before_authentication()
    {
        await using var sp = ProviderFor();

        var unauth = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncDataCatalogAsync(false, sp, Accessor("", null)));
        unauth.Errors[0].Code.ShouldBe("UNAUTHORIZED");

        var nonAdmin = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncDataCatalogAsync(false, sp, Accessor("Bearer", "tenant-a", "Reader")));
        nonAdmin.Errors[0].Code.ShouldBe("FORBIDDEN");

        var invalidTenant = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncDataCatalogAsync(false, sp, Accessor("Bearer", "bad tenant!", "GovernanceAdmin")));
        invalidTenant.Errors[0].Code.ShouldBe("FORBIDDEN");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Unavailable_error_has_fixed_message()
    {
        await using var sp = ProviderFor();

        var omEx = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncOpenMetadataAsync(false, sp, Accessor("Bearer", "tenant-a", "ClusterAdmin")));
        omEx.Errors[0].Code.ShouldBe("UNAVAILABLE");
        omEx.Errors[0].Message.ShouldBe("OpenMetadata is not available.");

        var catEx = await Should.ThrowAsync<GraphQLException>(
            () => new Mutation().SyncDataCatalogAsync(false, sp, Accessor("Bearer", "tenant-a", "ClusterAdmin")));
        catEx.Errors[0].Code.ShouldBe("UNAVAILABLE");
        catEx.Errors[0].Message.ShouldBe("The data catalog is not available.");
    }

    private sealed class StubCatalogSyncService : IDataCatalogSyncService
    {
        public Task<CatalogSyncResult> SyncCatalogAsync(bool dryRun = false, TenantId? restrictToTenant = null, CancellationToken ct = default) =>
            Task.FromResult(new CatalogSyncResult(0, 0, 0, 0, [], [], true));

        public Task<TableMetadata?> EnrichOrReferenceTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(null);
    }
}
