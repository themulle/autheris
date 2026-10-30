namespace Autheris.Tests.Unit.Api;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #282 (SEC-282-8 authorization, SEC-282-9 audit): authorization and audit details of POST /api/extensions/dbt/sync.</summary>
public sealed class DbtSyncEndpointCreationTests
{
    private static DefaultHttpContext Context(string role)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "u-" + role), new(ClaimTypes.Role, role), new(ClaimTypes.PrimarySid, "S-1-5-21-" + role) };
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims.Append(DefaultTenantCaller.Claim), "TestAuth")) };
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
        ctx.Features.Set(new AuditContext());
        return ctx;
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_NonAdmin_Returns403AndServiceNotCalled()
    {
        var service = Substitute.For<IDbtMetadataIngestionService>();
        var ctx = Context("DataOwner");

        var result = await DbtEndpoints.HandleSyncAsync(ctx, service);
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().IngestManifestStreamAsync(default!, default!, default, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_AuditDetailsContainCreatedNamesWithoutSecrets()
    {
        var created = Enumerable.Range(0, 120).Select(i => $"warehouse.public.t{i}").ToArray();
        var service = Substitute.For<IDbtMetadataIngestionService>();
        service.IngestManifestStreamAsync(Arg.Any<Stream>(), Arg.Any<DbtGovernanceImportCaller>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new DbtSyncResult(true, 120, 0, 0, ["column 'ssn' flagged"])
            {
                CreatedTables = created,
                SkippedItems = [new DbtSyncSkippedItem("raw.crm.x", "source", DbtSyncSkipReasons.NoDataSourceMapping)]
            });
        var ctx = Context("DbtAdmin");

        var result = await DbtEndpoints.HandleSyncAsync(ctx, service);
        await result.ExecuteAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var details = ctx.Features.Get<AuditContext>()!.AdditionalDetails;
        details["created_tables_count"].ShouldBe(120);
        ((string[])details["created_tables"]!).Length.ShouldBe(100);
        details["created_tables_truncated"].ShouldBe(true);
        details["skipped_items_count"].ShouldBe(1);
        details["dry_run"].ShouldBe(false);
        details.Keys.ShouldNotContain(k => k.Contains("column", StringComparison.OrdinalIgnoreCase) || k.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_AuditDetailsOnPartialFailure_ContainCreatedNamesWithoutColumnsOrSecrets()
    {
        var service = Substitute.For<IDbtMetadataIngestionService>();
        service.IngestManifestStreamAsync(Arg.Any<Stream>(), Arg.Any<DbtGovernanceImportCaller>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new DbtSyncResult(true, 2, 0, 0, [])
            {
                CreatedTables = ["warehouse.public.ok"],
                SkippedItems = [new DbtSyncSkippedItem("warehouse.public.broken", "model", DbtSyncSkipReasons.CreateFailed)]
            });
        var ctx = Context("DbtAdmin");
        ctx.Request.QueryString = new QueryString("?dryRun=true");

        var result = await DbtEndpoints.HandleSyncAsync(ctx, service);
        await result.ExecuteAsync(ctx);

        var details = ctx.Features.Get<AuditContext>()!.AdditionalDetails;
        ((string[])details["created_tables"]!).ShouldBe(["warehouse.public.ok"]);
        details["dry_run"].ShouldBe(true);
        details.Values.OfType<string>().ShouldNotContain(v => v.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Sync_ServiceThrows_AuditDetailsAreStillWritten()
    {
        var service = Substitute.For<IDbtMetadataIngestionService>();
        service.IngestManifestStreamAsync(Arg.Any<Stream>(), Arg.Any<DbtGovernanceImportCaller>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbtSyncResult>>(_ => throw new InvalidOperationException("boom"));
        var ctx = Context("DbtAdmin");

        await Should.ThrowAsync<InvalidOperationException>(() => DbtEndpoints.HandleSyncAsync(ctx, service));

        var details = ctx.Features.Get<AuditContext>()!.AdditionalDetails;
        details["created_tables_count"].ShouldBe(0);
    }
}
