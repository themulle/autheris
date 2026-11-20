namespace Autheris.Tests.Unit.Api;

using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>The profile table entries are stored one per row (512 characters): a blank, over-long or surplus entry is refused with a 400 before any write, without an echo.</summary>
[Trait("Category", "Security")]
public sealed class GovernanceEndpointsProfileValidationTests
{
    private const string Canary = "CANARY-9d2e-secret-table";

    [Fact]
    public async Task Profile_TablesEntryOver512_Returns400()
    {
        var repo = Substitute.For<IAccessProfileRepository>();

        var r = await PostAsync(["ok.table", Canary + new string('x', 520)], repo);

        ErrorBodyMigrationEndpointTests.AssertGovernedProblem(r, 400, "BAD_REQUEST");
        await repo.DidNotReceiveWithAnyArgs().UpsertProfileAsync(default!, default);
    }

    [Fact]
    public async Task Profile_BlankTablesEntry_Returns400()
    {
        var repo = Substitute.For<IAccessProfileRepository>();

        var r = await PostAsync(["ok.table", "   "], repo);

        ErrorBodyMigrationEndpointTests.AssertGovernedProblem(r, 400, "BAD_REQUEST");
        await repo.DidNotReceiveWithAnyArgs().UpsertProfileAsync(default!, default);
    }

    [Fact]
    public async Task Profile_TooManyTablesEntries_Returns400()
    {
        var repo = Substitute.For<IAccessProfileRepository>();

        var r = await PostAsync(Enumerable.Range(0, 1001).Select(i => "s.t" + i).ToList(), repo);

        ErrorBodyMigrationEndpointTests.AssertGovernedProblem(r, 400, "BAD_REQUEST");
        await repo.DidNotReceiveWithAnyArgs().UpsertProfileAsync(default!, default);
    }

    [Fact]
    public async Task Profile_TablesError_DoesNotEchoEntry()
    {
        var repo = Substitute.For<IAccessProfileRepository>();

        var over = await PostAsync([Canary + new string('x', 520)], repo);
        var blank = await PostAsync([Canary, " "], repo);

        over.Body.ShouldNotContain("CANARY");
        blank.Body.ShouldNotContain("CANARY");
    }

    private static DefaultHttpContext AdminContext()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-ADMIN"), new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim("tenant_id", "t1"),
             new Claim(ClaimTypes.Role, "ClusterAdmin"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "Test"));
        return ctx;
    }

    private static async Task<(int Status, string ContentType, System.Text.Json.JsonElement Json, string Body)> PostAsync(List<string> tables, IAccessProfileRepository repo)
    {
        var req = new BulkConsentRequest { Subject = "alice", MaskingMode = "Default", Tables = tables, ValidDays = 30, Justification = "A sufficiently long justification." };
        return await ErrorBodyMigrationEndpointTests.ExecAsync(await GovernanceEndpoints.CreateBulkConsentAsync(req, AdminContext(), repo, default));
    }
}
