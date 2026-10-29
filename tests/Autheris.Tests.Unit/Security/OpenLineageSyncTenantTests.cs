namespace Autheris.Tests.Unit.Security;

using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class OpenLineageSyncTenantTests
{
    private static async Task<int> GetStatusCodeAsync(IResult result, HttpContext context)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        context.RequestServices ??= services.BuildServiceProvider();
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task Sync_UsesRealmBoundTenant_NotTenantIdClaim()
    {
        var client = Substitute.For<IOpenLineageClient>();
        client.PushLineageGraphAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(true);

        const string boundTenant = "tenant-from-realm";
        var realm = new IdentityRealm("realm-1", IdentityRealmKind.EntraWorkforce, TenantBindingSpecification.Fixed(new TenantId(boundTenant)));
        Sid.RealmLookup = r => r == "realm-1" ? realm : null;

        try
        {
            var context = new DefaultHttpContext();
            context.User = DefaultTenantCaller.StampRealm(realm, new ClaimsIdentity([
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"),
                new Claim(ClaimTypes.Role, "GovernanceAdmin"),
                new Claim("autheris:realm", "realm-1"),
                new Claim("tenant_id", "ignored-tenant-claim")
            ], "TestAuth"));

            var result = await GovernanceEndpoints.SyncOpenLineageAsync(client, context, CancellationToken.None);
            var status = await GetStatusCodeAsync(result, context);
            status.ShouldBe(StatusCodes.Status200OK);

            await client.Received(1).PushLineageGraphAsync(
                Arg.Is<TenantId>(t => t.Value == boundTenant),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Sid.RealmLookup = null;
        }
    }

    [Fact]
    public async Task Sync_EntraTidCaller_PushesTidTenant()
    {
        var client = Substitute.For<IOpenLineageClient>();
        client.PushLineageGraphAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tid", "tenant-entra-guid")
        ], "TestAuth"));

        var result = await GovernanceEndpoints.SyncOpenLineageAsync(client, context, CancellationToken.None);
        var status = await GetStatusCodeAsync(result, context);
        status.ShouldBe(StatusCodes.Status200OK);

        await client.Received(1).PushLineageGraphAsync(
            Arg.Is<TenantId>(t => t.Value == "tenant-entra-guid"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sync_DataOwnerOnly_Returns403()
    {
        var client = Substitute.For<IOpenLineageClient>();

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-OWNER"),
            new Claim(ClaimTypes.Role, "DataOwner"), // Not GovernanceAdmin
            new Claim("tenant_id", "tenant-1")
        ], "TestAuth"));

        var result = await GovernanceEndpoints.SyncOpenLineageAsync(client, context, CancellationToken.None);
        var status = await GetStatusCodeAsync(result, context);

        status.ShouldBe(StatusCodes.Status403Forbidden);
        await client.DidNotReceiveWithAnyArgs().PushLineageGraphAsync(default, default);
    }

    [Fact]
    public async Task Sync_MalformedTenantClaim_DoesNotReturn500()
    {
        var client = Substitute.For<IOpenLineageClient>();

        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", "malformed/tenant/spaces and illegal!")
        ], "TestAuth"));

        // Fail closed (DT-A-3): the invalid claim raises TenantClaimInvalidException, which the exception handler answers with 403
        // TENANT_CLAIM_INVALID (never 500); nothing is pushed.
        await Should.ThrowAsync<Autheris.Domain.Exceptions.TenantClaimInvalidException>(
            () => GovernanceEndpoints.SyncOpenLineageAsync(client, context, CancellationToken.None));
        await client.DidNotReceiveWithAnyArgs().PushLineageGraphAsync(default, default);
    }
}
