namespace Autheris.Tests.Unit.Tenancy;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Application.FinOps.Interfaces;
using Autheris.Application.Identity;
using Autheris.Application.Interfaces;
using Autheris.Application.Performance.IncrementalDelivery;
using Autheris.Application.Services;
using Autheris.Application.Tenancy;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-DT-4 Phase 5 review findings: no "default" tenant bucket, one tenant claim reader, fail-closed FinOps, stable tenant source.</summary>
[Trait("Category", "Security")]
public sealed class Wp4ReviewFindingsTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public async Task DT_SC_03_Datasource_Test_Audit_Of_Claimless_Caller_Is_Not_Written_Under_Default_Tenant()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var service = new DatasourceTestingService(
            Substitute.For<ITableMetadataRepository>(), Substitute.For<IHttpClientFactory>(), audit,
            Substitute.For<IKeyVaultSecretProvider>(), env, NullLogger<DatasourceTestingService>.Instance);
        var caller = Principal(new Claim(ClaimTypes.NameIdentifier, "u"), new Claim(ClaimTypes.Role, "Admin"));

        await Record.ExceptionAsync(() => service.TestDatasourceAsync("crm.dbo.customers", new DatasourceTestRequest(), caller));

        await audit.DidNotReceive().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.TenantId.Value == "default"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void DT_SC_03_IdentitySubjectResolver_Conflicting_Tenant_Claims_Are_Rejected_Not_First_Wins()
    {
        var resolver = new IdentitySubjectResolver();
        var conflicting = Principal(new Claim("sub", "u"), new Claim("tenant_id", "tenant-a"), new Claim("tid", "tenant-b"));

        Should.Throw<TenantClaimInvalidException>(() => resolver.ResolveSubject(conflicting));
    }

    [Fact]
    public void DT_SC_03_IdentitySubjectResolver_Absent_Tenant_Stays_Null_And_Valid_Is_Read()
    {
        var resolver = new IdentitySubjectResolver();

        resolver.ResolveSubject(Principal(new Claim("sub", "u"))).Tenant.ShouldBeNull();
        resolver.ResolveSubject(Principal(new Claim("sub", "u"), new Claim("tid", "tenant-a"))).Tenant!.Value.Value.ShouldBe("tenant-a");
    }

    [Fact]
    public void DT_SC_03_ResourceGroup_Tenant_Key_Has_No_Default_Bucket_For_Claimless_Callers()
    {
        var claimless = new DefaultHttpContext { User = Principal(new Claim("sub", "u")) };
        var invalid = new DefaultHttpContext { User = Principal(new Claim("tenant_id", "a"), new Claim("tid", "b")) };
        var valid = new DefaultHttpContext { User = Principal(new Claim("tid", "tenant-a")) };

        ResourceGroupMiddleware.ResolveTenantKey(claimless).ShouldNotBe("default");
        ResourceGroupMiddleware.ResolveTenantKey(invalid).ShouldNotBe("a");
        ResourceGroupMiddleware.ResolveTenantKey(invalid).ShouldNotBe("b");
        ResourceGroupMiddleware.ResolveTenantKey(valid).ShouldBe("tenant-a");
    }

    [Fact]
    public async Task DT_SC_03_FinOps_Authenticated_Caller_With_Invalid_Tenant_Claim_Does_Not_Skip_The_Budget_Check()
    {
        var accounting = Substitute.For<IFinOpsAccountingService>();
        accounting.IsEnabled.Returns(true);
        var nextCalled = false;
        var middleware = new FinOpsBudgetMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, NullLogger<FinOpsBudgetMiddleware>.Instance);
        var context = new DefaultHttpContext { User = Principal(new Claim("tenant_id", "a"), new Claim("tid", "b")) };
        context.Request.Path = "/graphql";

        await Should.ThrowAsync<TenantClaimInvalidException>(() => middleware.InvokeAsync(context, accounting));
        nextCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task DT_SC_03_FinOps_Authenticated_Caller_Without_Tenant_Is_Not_Waved_Through()
    {
        var accounting = Substitute.For<IFinOpsAccountingService>();
        accounting.IsEnabled.Returns(true);
        var nextCalled = false;
        var middleware = new FinOpsBudgetMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, NullLogger<FinOpsBudgetMiddleware>.Instance);
        var context = new DefaultHttpContext { User = Principal(new Claim("sub", "u")) };
        context.Request.Path = "/graphql";

        await Should.ThrowAsync<TenantUnresolvedException>(() => middleware.InvokeAsync(context, accounting));
        nextCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task DT_SC_03_IncrementalDelivery_Client_Key_Reads_The_Tenant_Through_The_Standard_Reader()
    {
        var manager = Substitute.For<IIncrementalDeliveryManager>();
        manager.IsEnabled.Returns(true);
        manager.TryAcquireStreamSlot(Arg.Any<string>()).Returns(true);
        var middleware = new IncrementalDeliveryMiddleware(_ => Task.CompletedTask, NullLogger<IncrementalDeliveryMiddleware>.Instance);
        var context = new DefaultHttpContext { User = Principal(new Claim("tid", "tenant-a"), new Claim("sub", "u")) };
        context.Request.Headers.Accept = "multipart/mixed";

        await middleware.InvokeAsync(context, manager);

        manager.Received().TryAcquireStreamSlot("tenant-a:u");
    }

    [Fact]
    public async Task DT_SC_03_IncrementalDelivery_Invalid_Tenant_Claim_Is_Not_Used_In_The_Client_Key()
    {
        var manager = Substitute.For<IIncrementalDeliveryManager>();
        manager.IsEnabled.Returns(true);
        manager.TryAcquireStreamSlot(Arg.Any<string>()).Returns(true);
        var middleware = new IncrementalDeliveryMiddleware(_ => Task.CompletedTask, NullLogger<IncrementalDeliveryMiddleware>.Instance);
        var context = new DefaultHttpContext { User = Principal(new Claim("tenant_id", "a"), new Claim("tid", "b"), new Claim("sub", "u")) };
        context.Request.Headers.Accept = "multipart/mixed";

        await middleware.InvokeAsync(context, manager);

        manager.Received().TryAcquireStreamSlot("u");
    }

    [Fact]
    public void DT_SC_05_Realm_Claim_Without_A_Registry_Is_Invalid_Not_The_Claim_Chain()
    {
        var resolver = new TenantResolver(
            () => new TenancyOptions { DefaultTenantId = "dflt" }, realms: null, NullLogger<TenantResolver>.Instance);
        var principal = Principal(new Claim("autheris:realm", "r1"), new Claim("tenant_id", "tenant-a"));

        resolver.Resolve(principal).Outcome.ShouldBe(TenantResolutionOutcome.Invalid);
    }

    [Fact]
    public async Task DT_SC_05_Second_Claims_Transformation_Keeps_The_Original_Tenant_Source()
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        var resolver = new TenantResolver(() => new TenancyOptions { DefaultTenantId = "dflt" }, registry, NullLogger<TenantResolver>.Instance);
        var transformation = new EnterpriseClaimsTransformation(registry, tenantResolver: resolver);
        var principal = Principal(new Claim("sub", "u"));

        await transformation.TransformAsync(principal);
        await transformation.TransformAsync(principal);

        principal.FindFirst(TenantSourceClaim.ClaimType)!.Value.ShouldBe(TenantSourceClaim.Default);
        principal.FindAll(TenantSourceClaim.ClaimType).Count().ShouldBe(1);
        principal.FindFirst("tenant_id")!.Value.ShouldBe("dflt");
    }

    [Fact]
    public async Task DT_SC_05_Inbound_Tenant_Source_Is_Still_Stripped_On_The_First_Transformation()
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        var resolver = new TenantResolver(() => new TenancyOptions { DefaultTenantId = "dflt" }, registry, NullLogger<TenantResolver>.Instance);
        var transformation = new EnterpriseClaimsTransformation(registry, tenantResolver: resolver);
        var principal = Principal(new Claim("sub", "u"), new Claim(TenantSourceClaim.ClaimType, TenantSourceClaim.Realm));

        await transformation.TransformAsync(principal);

        principal.FindFirst(TenantSourceClaim.ClaimType)!.Value.ShouldBe(TenantSourceClaim.Default);
    }
}
