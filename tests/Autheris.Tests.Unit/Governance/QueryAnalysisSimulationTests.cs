namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-10 residuals: exact simulation-privilege matching and a tenant-bound simulated target.</summary>
[Trait("Category", "Security")]
public sealed class QueryAnalysisSimulationTests
{
    private static ClaimsPrincipal Principal(string sid, string? tenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, sid), new(ClaimTypes.Name, sid), new("sid", sid) };
        if (tenant != null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static readonly QueryAnalysisItem[] Queries = [QueryAnalysisItem.FromSql("SELECT id FROM sales.public.orders", name: "q")];

    private static (QueryAnalysisService Service, IConsentRepository Consents) CreateService()
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "public",
                TableName = "orders",
                DisplayName = "orders",
                Visibility = DatasetVisibility.Full
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "uuid" }],
            PrimaryKeyColumns = ["id"]
        };
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new[] { table });
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Consent>());
        return (new QueryAnalysisService(metadataRepo, consentRepo), consentRepo);
    }

    [Fact]
    public async Task OwnSid_WithTargetRoles_WithoutPrivilege_Throws()
    {
        var service = Substitute.For<IQueryAnalysisService>();
        var request = new AnalyzeQueriesHttpRequest(Queries, TargetSubjectId: "consumer-1", TargetRoles: ["Reader"]);
        var http = new DefaultHttpContext { User = Principal("consumer-1", "tenant-a", "Consumer") };

        var result = await QueryAnalysisAndProfilingEndpoints.HandleAnalyzeQueriesAsync(request, http, service, CancellationToken.None);

        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().AnalyzeQueriesAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("GovernanceAdmin")]
    [InlineData("governance:admin")]
    [InlineData("ClusterAdmin")]
    public void Target_WithGovernanceAdminRole_GetsNoAdminView(string role)
    {
        var target = QueryAnalysisService.CreateSimulatedTargetPrincipal("someone", [role], "tenant-a");

        QueryAnalysisService.IsAdminPrincipal(target).ShouldBeFalse();
        QueryAnalysisService.HasSimulatePrivilege(target).ShouldBeFalse();
    }

    [Theory]
    [InlineData("tenant-a:governance:admin")]
    [InlineData("tenant-a:governance:simulate")]
    [InlineData("xgovernance:simulatex")]
    public void TenantPrefixedAdminRole_IsNotSimulatePrivilege(string role)
    {
        QueryAnalysisService.HasSimulatePrivilege(Principal("user-1", "tenant-a", role)).ShouldBeFalse();
    }

    [Fact]
    public void GenericAdminRole_IsNotSimulatePrivilege()
    {
        QueryAnalysisService.HasSimulatePrivilege(Principal("user-1", "tenant-a", "admin")).ShouldBeFalse();
        QueryAnalysisService.HasSimulatePrivilege(Principal("user-1", "tenant-a", "SuperAdmin")).ShouldBeFalse();
    }

    [Fact]
    public void ExactSimulateRoleAndScopeToken_ArePrivileged()
    {
        QueryAnalysisService.HasSimulatePrivilege(Principal("user-1", "tenant-a", "Governance:Simulate")).ShouldBeTrue();

        var scoped = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-2"), new Claim("scope", "openid governance:simulate")], "TestAuth"));
        QueryAnalysisService.HasSimulatePrivilege(scoped).ShouldBeTrue();

        var lookalike = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-3"), new Claim("scope", "openid governance:simulate-not")], "TestAuth"));
        QueryAnalysisService.HasSimulatePrivilege(lookalike).ShouldBeFalse();
    }

    [Fact]
    public async Task SimulatedTarget_ConsentsScopedToCallerTenant()
    {
        var (service, consents) = CreateService();
        var simulator = Principal("auditor-1", "tenant-a", "governance:simulate");
        var target = QueryAnalysisService.CreateSimulatedTargetPrincipal("someone", ["Reader"], "tenant-a");

        await service.AnalyzeQueriesAsync(Queries, simulator, target);

        await consents.Received().GetAllActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Is<TenantId?>(t => t.HasValue && t.Value.Value == "tenant-a"), Arg.Any<CancellationToken>());
    }
}
