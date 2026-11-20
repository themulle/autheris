namespace Autheris.Tests.Unit.Api;

using Autheris.Application.Policy.Pdp;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// O-5b step 2, WP-3/WP-4 (NL-SC-20/21, NL-T-20/21): per migrated site, the status and the governed code are kept, the
/// body is a problem document without the legacy <c>error</c> member, and neither exception text nor caller input is echoed.
/// </summary>
[Trait("Category", "Security")]
public sealed class ErrorBodyMigrationEndpointTests
{
    private const string Canary = "CANARY-7c1d-secret-name";

    private static IServiceProvider Services()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        return Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
    }

    internal static async Task<(int Status, string ContentType, JsonElement Json, string Body)> ExecAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = Services();
        await result.ExecuteAsync(ctx);
        return await ReadAsync(ctx);
    }

    internal static async Task<(int Status, string ContentType, JsonElement Json, string Body)> ReadAsync(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var json = body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
        return (ctx.Response.StatusCode, ctx.Response.ContentType ?? string.Empty, json, body);
    }

    internal static void AssertGovernedProblem((int Status, string ContentType, JsonElement Json, string Body) r, int status, string code)
    {
        r.Status.ShouldBe(status);
        r.ContentType.ShouldStartWith("application/problem+json");
        r.Json.GetProperty("code").GetString().ShouldBe(code);
        r.Json.TryGetProperty("error", out _).ShouldBeFalse();
        r.Body.ShouldNotContain(Canary);
    }

    private static DefaultHttpContext NewContext(string path = "/x")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = Services();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", "t1")], "Test"));
        return ctx;
    }

    private static readonly IGovernedTableReadPlanner UnusedPlanner = Substitute.For<IGovernedTableReadPlanner>();
    private static readonly IKeysetCursorEncoder UnusedCursor = Substitute.For<IKeysetCursorEncoder>();

    // ---------- WP-3 ----------

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_Catalog_SetDatasetState_StateRequired()
    {
        var ctx = NewContext();
        var r = await ExecAsync(await CatalogApiEndpoints.SetDatasetStateHandler("a.b.c", new AdminSetDatasetStateDto(" "), new StubAccessPlanningService(), ctx, default));

        AssertGovernedProblem(r, 400, "BAD_REQUEST");
    }

    [Fact]
    public async Task Migrated_Error_Bodies_Do_Not_Echo_Exception_Or_Input_Catalog_SetDatasetState_ServiceMessage()
    {
        var service = new StubAccessPlanningService
        {
            OnSetDatasetState = (_, _, _, _) => Task.FromResult(new AdminSetDatasetStateResult("a.b.c", "Active", "Active", false, Canary))
        };

        var r = await ExecAsync(await CatalogApiEndpoints.SetDatasetStateHandler("a.b.c", new AdminSetDatasetStateDto("Active"), service, NewContext(), default));

        AssertGovernedProblem(r, 404, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_Catalog_RegisterDatasource_NameRequired()
    {
        var r = await ExecAsync(await CatalogApiEndpoints.RegisterDatasourceHandler(new DatasourceRegistrationRequest(" ", Canary), new Autheris.Tests.Unit.TestDoubles.StubCatalogDiscoveryService(), NewContext(), default));

        AssertGovernedProblem(r, 400, "BAD_REQUEST");
    }

    private static async Task<(int Status, string ContentType, JsonElement Json, string Body)> BulkAsync(
        string domain, string schema, string table, string query, ITableMetadataRepository? repo = null)
    {
        var ctx = NewContext();
        ctx.Request.QueryString = new QueryString(query);
        await TableStreamEndpoints.HandleBulkStreamAsync(
            domain, schema, table, ctx,
            repo ?? new InMemoryTableMetadataRepository(),
            AccessDecisionPointStubs.Denying(),
            UnusedPlanner,
            StubGovernedSqlExecutionService.Empty,
            UnusedCursor,
            StubColumnMaskingProvider.Null,
            NullLoggerFactory.Instance,
            default);
        return await ReadAsync(ctx);
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_TableStream_InvalidIdentifier()
    {
        var r = await BulkAsync("d", "s", Canary + "/x", string.Empty);

        AssertGovernedProblem(r, 400, "BAD_REQUEST");
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_TableStream_OffsetNotSupported()
    {
        var r = await BulkAsync("d", "s", "t", "?offset=5");

        AssertGovernedProblem(r, 400, "OFFSET_NOT_SUPPORTED");
    }

    [Fact]
    public async Task Migrated_Error_Bodies_Do_Not_Echo_Exception_Or_Input_TableStream_UnknownTable_IsFixed404()
    {
        var repo = new InMemoryTableMetadataRepository();

        var r = await BulkAsync("d", "s", Canary, string.Empty, repo);

        AssertGovernedProblem(r, 404, "RESOURCE_NOT_FOUND");
    }

    // ---------- WP-4 ----------

    private static DefaultHttpContext AdminContext(string body = "")
    {
        var ctx = NewContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-ADMIN"), new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim("tenant_id", "t1"),
             new Claim(ClaimTypes.Role, "ClusterAdmin"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "Test"));
        ctx.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        return ctx;
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_BulkConsent_BodyRequired()
    {
        var r = await ExecAsync(await GovernanceEndpoints.CreateBulkConsentAsync(null!, AdminContext(), new Autheris.Infrastructure.Persistence.InMemoryAccessProfileRepository(), default));

        AssertGovernedProblem(r, 400, "BAD_REQUEST");
    }

    [Fact]
    public async Task Migrated_Error_Bodies_Do_Not_Echo_Exception_Or_Input_BulkConsent_InvalidMaskingMode()
    {
        var req = new BulkConsentRequest { Subject = "alice", MaskingMode = Canary, Tables = ["a.b"], ValidDays = 30, Justification = "A sufficiently long justification." };

        var r = await ExecAsync(await GovernanceEndpoints.CreateBulkConsentAsync(req, AdminContext(), new Autheris.Infrastructure.Persistence.InMemoryAccessProfileRepository(), default));

        AssertGovernedProblem(r, 400, "BAD_REQUEST");
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_BulkConsent_RowFilterSyntax()
    {
        var req = new BulkConsentRequest { Subject = "alice", MaskingMode = "Default", Tables = ["a.b"], ValidDays = 30, Justification = "A sufficiently long justification.", RowFilter = "x = 1; DROP " + Canary };

        var r = await ExecAsync(await GovernanceEndpoints.CreateBulkConsentAsync(req, AdminContext(), new Autheris.Infrastructure.Persistence.InMemoryAccessProfileRepository(), default));

        AssertGovernedProblem(r, 400, "ROW_FILTER_SYNTAX_INVALID");
    }

    [Fact]
    public async Task Migrated_Error_Bodies_Do_Not_Echo_Exception_Or_Input_ProfileById_NotFound()
    {
        var repo = new Autheris.Infrastructure.Persistence.InMemoryAccessProfileRepository();

        var r = await ExecAsync(await GovernanceEndpoints.GetProfileByIdAsync(Canary, AdminContext(), repo, default));

        AssertGovernedProblem(r, 404, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_ApproveAssignment_SubjectRequired()
    {
        var r = await ExecAsync(await GovernanceEndpoints.ApproveProfileAssignmentAsync(Canary, new ApproveProfileAssignmentRequest(), AdminContext(), new Autheris.Infrastructure.Persistence.InMemoryAccessProfileRepository(), default));

        AssertGovernedProblem(r, 400, "BAD_REQUEST");
    }

    [Theory]
    [InlineData("{ " + Canary)]
    [InlineData("null")]
    public async Task Migrated_Error_Bodies_Do_Not_Echo_Exception_Or_Input_ConsentManifest_Invalid(string body)
    {
        var r = await ExecAsync(await GovernanceEndpoints.ApplyConsentManifestAsync(AdminContext(body), StubDeclarativeConsentApplyService.Instance, default));

        AssertGovernedProblem(r, 400, body == "null" ? "BAD_REQUEST" : "INVALID_JSON");
    }

    [Fact]
    public async Task Bare_Error_Migration_Preserves_Status_And_Code_Per_Group_Plan_TotpRequired()
    {
        var r = await ExecAsync(await GovernancePlanEndpoints.HandleConfirmAsync(Canary, new AdminConfirmPlanRequest(Canary, string.Empty), AdminContext(), new StubAccessPlanningService(), default));

        AssertGovernedProblem(r, 400, "GOVERNANCE_PLAN_INVALID");
    }

    private sealed class StubDeclarativeConsentApplyService : IDeclarativeConsentApplyService
    {
        public static readonly StubDeclarativeConsentApplyService Instance = new();
        public Task<DeclarativeApplyResult> ApplyManifestAsync(
            ClaimsPrincipal caller,
            DeclarativeConsentManifest manifest,
            bool dryRun = false,
            Autheris.Domain.Security.SecurityPrincipalContext? callerContext = null,
            DeclarativePruneMode pruneMode = DeclarativePruneMode.False,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
