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
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Security review: the analyze endpoint built a synthetic target from client-supplied TargetSubjectId/TargetRoles, and the
/// simulation privilege was only checked for a foreign SID. A consumer could send its own SID with TargetRoles ["GovernanceAdmin"]
/// and be evaluated as administrator.
/// </summary>
[Trait("Category", "Security")]
public sealed class QueryAnalysisSimulationPrivilegeTests
{
    private const string Dataset = "sales.public.orders";

    private static ClaimsPrincipal CreatePrincipal(string sid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sid),
            new(ClaimTypes.Name, sid),
            new("sid", sid)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims.Append(DefaultTenantCaller.Claim), "TestAuth"));
    }

    private static QueryAnalysisService CreateService()
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

        // Nobody holds a consent: only administrator semantics could release the table.
        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Consent>());

        return new QueryAnalysisService(metadataRepo, consentRepo);
    }

    private static readonly QueryAnalysisItem[] Queries = [QueryAnalysisItem.FromSql("SELECT id FROM sales.public.orders", name: "q")];

    private static bool TreatedAsAdmin(QueryAnalysisResult result) =>
        result.AccessRequirements.MissingConsents.Count == 0 && result.ValidationReport.OutputColumns.Count > 0;

    // ---------------------------------------------------------------- service

    [Fact]
    public async Task Service_UnprivilegedCaller_OwnSidTargetWithAdminRoles_IsEvaluatedAsItself()
    {
        var caller = CreatePrincipal("consumer-1", "Consumer");
        var forgedTarget = CreatePrincipal("consumer-1", "GovernanceAdmin", "ClusterAdmin");

        var result = await CreateService().AnalyzeQueriesAsync(Queries, caller, forgedTarget);

        TreatedAsAdmin(result).ShouldBeFalse();
        result.AccessRequirements.MissingConsents.ShouldContain(c => c.Dataset == Dataset);
    }

    [Fact]
    public async Task Service_UnprivilegedCaller_ForeignTarget_IsRejected()
    {
        var caller = CreatePrincipal("consumer-1", "Consumer");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            CreateService().AnalyzeQueriesAsync(Queries, caller, CreatePrincipal("other-user")));
    }

    [Fact]
    public async Task Service_PrivilegedCaller_TargetWithAdminRoles_NeverGetsAdminSemantics()
    {
        var simulator = CreatePrincipal("auditor-1", "governance:simulate");
        var target = CreatePrincipal("someone", "GovernanceAdmin", "ClusterAdmin");

        var result = await CreateService().AnalyzeQueriesAsync(Queries, simulator, target);

        TreatedAsAdmin(result).ShouldBeFalse();
        result.AccessRequirements.MissingConsents.ShouldContain(c => c.Dataset == Dataset);
    }

    [Fact]
    public async Task Service_AdminCallerEvaluatingItself_KeepsAdminSemantics()
    {
        var admin = CreatePrincipal("admin-1", "GovernanceAdmin");

        var result = await CreateService().AnalyzeQueriesAsync(Queries, admin);

        TreatedAsAdmin(result).ShouldBeTrue();
    }

    [Fact]
    public async Task Service_AdminCallerSimulatingItsOwnSid_HasNoAdminSemantics()
    {
        var admin = CreatePrincipal("admin-1", "GovernanceAdmin");
        var target = QueryAnalysisService.CreateSimulatedTargetPrincipal("admin-1", ["GovernanceAdmin"], DefaultTenantCaller.Claim.Value);

        var result = await CreateService().AnalyzeQueriesAsync(Queries, admin, target);

        TreatedAsAdmin(result).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- synthetic identity

    [Theory]
    [InlineData("GovernanceAdmin")]
    [InlineData("governanceadmin")]
    [InlineData("ClusterAdmin")]
    [InlineData(" clusteradmin ")]
    [InlineData("tenant-a:ClusterAdmin")]
    [InlineData("tenant-a:GovernanceAdmin")]
    [InlineData("governance:simulate")]
    [InlineData("governance:admin")]
    [InlineData("admin")]
    public void CreateSimulatedTargetPrincipal_DropsAdminAndSimulationRoles(string role)
    {
        var target = QueryAnalysisService.CreateSimulatedTargetPrincipal("someone", [role, "Reader"]);

        QueryAnalysisService.IsAdminPrincipal(target).ShouldBeFalse();
        QueryAnalysisService.HasSimulatePrivilege(target).ShouldBeFalse();
        ClusterAdminPolicy.IsCanonicalClusterAdmin(target).ShouldBeFalse();
        target.GetUserRoles().ShouldContain("Reader");
        target.GetUserRoles().Count.ShouldBe(1);
    }

    // ---------------------------------------------------------------- endpoint

    private static HttpContext HttpContextFor(ClaimsPrincipal user) => new DefaultHttpContext { User = user };

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;

    [Fact]
    public async Task Endpoint_ConsumerWithOwnSidAndAdminTargetRoles_IsForbidden_ServiceNotCalled()
    {
        var service = Substitute.For<IQueryAnalysisService>();
        var request = new AnalyzeQueriesHttpRequest(Queries, TargetSubjectId: "consumer-1", TargetRoles: ["GovernanceAdmin"]);

        var result = await QueryAnalysisAndProfilingEndpoints.HandleAnalyzeQueriesAsync(
            request, HttpContextFor(CreatePrincipal("consumer-1", "Consumer")), service, CancellationToken.None);

        StatusOf(result).ShouldBe(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().AnalyzeQueriesAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Endpoint_ConsumerWithTargetRolesOnly_IsForbidden()
    {
        var service = Substitute.For<IQueryAnalysisService>();
        var request = new AnalyzeQueriesHttpRequest(Queries, TargetRoles: ["ClusterAdmin"]);

        var result = await QueryAnalysisAndProfilingEndpoints.HandleAnalyzeQueriesAsync(
            request, HttpContextFor(CreatePrincipal("consumer-1", "Consumer")), service, CancellationToken.None);

        StatusOf(result).ShouldBe(StatusCodes.Status403Forbidden);
        await service.DidNotReceiveWithAnyArgs().AnalyzeQueriesAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Endpoint_PrivilegedCaller_TargetRolesWithoutSubject_IsBadRequest()
    {
        var service = Substitute.For<IQueryAnalysisService>();
        var request = new AnalyzeQueriesHttpRequest(Queries, TargetRoles: ["Reader"]);

        var result = await QueryAnalysisAndProfilingEndpoints.HandleAnalyzeQueriesAsync(
            request, HttpContextFor(CreatePrincipal("auditor-1", "governance:simulate")), service, CancellationToken.None);

        StatusOf(result).ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Endpoint_PrivilegedCaller_AdminTargetRolesAreDropped()
    {
        var service = Substitute.For<IQueryAnalysisService>();
        ClaimsPrincipal? captured = null;
        service.AnalyzeQueriesAsync(
                Arg.Any<IReadOnlyList<QueryAnalysisItem>>(),
                Arg.Any<ClaimsPrincipal>(),
                Arg.Do<ClaimsPrincipal?>(p => captured = p),
                Arg.Any<CancellationToken>())
            .Returns((QueryAnalysisResult)null!);
        var request = new AnalyzeQueriesHttpRequest(Queries, TargetSubjectId: "someone", TargetRoles: ["GovernanceAdmin", "ClusterAdmin", "Reader"]);

        await QueryAnalysisAndProfilingEndpoints.HandleAnalyzeQueriesAsync(
            request, HttpContextFor(CreatePrincipal("auditor-1", "governance:simulate")), service, CancellationToken.None);

        captured.ShouldNotBeNull();
        QueryAnalysisService.IsAdminPrincipal(captured).ShouldBeFalse();
        ClusterAdminPolicy.IsCanonicalClusterAdmin(captured).ShouldBeFalse();
        captured.IsInRole("Reader").ShouldBeTrue();
    }

    [Fact]
    public async Task Endpoint_NoTarget_CallsServiceWithoutTarget()
    {
        var service = Substitute.For<IQueryAnalysisService>();
        var request = new AnalyzeQueriesHttpRequest(Queries);

        await QueryAnalysisAndProfilingEndpoints.HandleAnalyzeQueriesAsync(
            request, HttpContextFor(CreatePrincipal("consumer-1", "Consumer")), service, CancellationToken.None);

        await service.Received(1).AnalyzeQueriesAsync(
            Arg.Any<IReadOnlyList<QueryAnalysisItem>>(), Arg.Any<ClaimsPrincipal>(), Arg.Is<ClaimsPrincipal?>(p => p == null), Arg.Any<CancellationToken>());
    }
}
