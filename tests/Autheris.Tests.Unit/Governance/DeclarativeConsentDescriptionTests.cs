using System.Security.Claims;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>PLAN-GOV-158-INC1 WP-158-2: manifest policy descriptions (decision 6, SEC-GOV158I1-06/07/10/14).</summary>
public sealed class DeclarativeConsentDescriptionTests
{
    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")], "test"));

    private static ITableMetadataRepository Tables()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Autheris.Domain.Model.Table { Id = Guid.NewGuid() },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }]
            });
        return repo;
    }

    private static DeclarativeConsentPolicy Policy(string? description, string table = "orders") =>
        new("default", "sales", table, "Role", "Analyst", "Allow", Description: description);

    private static (DeclarativeConsentApplyService Svc, FakeConsentRepository Repo) Create()
    {
        var repo = new FakeConsentRepository();
        return (new DeclarativeConsentApplyService(Tables(), repo, Substitute.For<IAuditLogRepository>()), repo);
    }

    [Fact]
    public async Task Apply_RuleChange_NewConsentCarriesDescription()
    {
        var (svc, repo) = Create();

        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [Policy("  Sales analysts  ")]));

        repo.Active(DateTimeOffset.UtcNow).Single().Description.ShouldBe("Sales analysts");
    }

    [Fact]
    public async Task Apply_DescriptionOnlyChange_UpdatesInPlace_SameIdNoRevokeNoEpoch()
    {
        var (svc, repo) = Create();
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [Policy("old")]));
        var id = repo.Active(DateTimeOffset.UtcNow).Single().Id;

        var result = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("2", [Policy("new text")]));

        var after = repo.Active(DateTimeOffset.UtcNow).Single();
        after.Id.ShouldBe(id);
        after.Description.ShouldBe("new text");
        result.Updated.ShouldBe(1);
        result.Applied.ShouldBe(0);
        result.Revoked.ShouldBe(0);
        repo.ReplaceCalls.ShouldBe(2);
        repo.LastCreateCount.ShouldBe(0);
        repo.LastRevokeCount.ShouldBe(0);
    }

    [Fact]
    public async Task Apply_LoadsStoredDescriptionOnlyForTheMatchingConsent_OptIn()
    {
        var (svc, repo) = Create();
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [Policy("old")]));
        repo.DescriptionLoadCalls.ShouldBe(0); // the create path compares nothing

        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("2", [Policy("old")]));

        repo.DescriptionLoadCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Apply_SameDescription_IsUnchangedAndNoReplaceCall()
    {
        var (svc, repo) = Create();
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [Policy("same")]));

        var result = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("2", [Policy(" same ")]));

        result.Unchanged.ShouldBe(1);
        result.Updated.ShouldBe(0);
        repo.ReplaceCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Apply_RemovedDescription_ClearsInPlace()
    {
        var (svc, repo) = Create();
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [Policy("old")]));

        var result = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("2", [Policy(null)]));

        result.Updated.ShouldBe(1);
        repo.Active(DateTimeOffset.UtcNow).Single().Description.ShouldBeNull();
    }

    [Fact]
    public async Task Apply_DryRun_DescriptionChange_NoWrite()
    {
        var (svc, repo) = Create();
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [Policy("old")]));

        var result = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("2", [Policy("new")]), dryRun: true);

        result.Updated.ShouldBe(1);
        repo.ReplaceCalls.ShouldBe(1);
        repo.Active(DateTimeOffset.UtcNow).Single().Description.ShouldBe("old");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("evil‮text")]
    [InlineData("line\nbreak")]
    public async Task Apply_InvalidDescription_400_NothingWritten(string description)
    {
        var (svc, repo) = Create();

        var ex = await Should.ThrowAsync<DeclarativeManifestValidationException>(() => svc.ApplyManifestAsync(Admin(),
            new DeclarativeConsentManifest("1", [Policy(null, "items"), Policy(description)])));

        ex.PolicyIndex.ShouldBe(1);
        ex.Message.ShouldNotContain("evil");
        ex.Message.ShouldNotContain("break");
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Apply_OverCapDescription_Rejected_NoEcho()
    {
        var (svc, repo) = Create();
        var text = new string('q', 501);

        var ex = await Should.ThrowAsync<DeclarativeManifestValidationException>(() => svc.ApplyManifestAsync(Admin(),
            new DeclarativeConsentManifest("1", [Policy(text)])));

        ex.Message.ShouldNotContain("qqqq");
        repo.ReplaceCalls.ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Apply_AuditDetail_NeverContainsRawDescription()
    {
        // T-GOV158I1-10: create and update paths.
        var (svc, repo) = Create();
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [Policy("SECRET-ALPHA text")]));
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("2", [Policy("SECRET-BETA text")]));
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("3", [Policy("SECRET-BETA text") with { ColumnRules = [new DeclarativeColumnRule("id", "Mask")] }]));

        repo.ReplaceAuditEntries.Count.ShouldBeGreaterThanOrEqualTo(3);
        foreach (var entry in repo.ReplaceAuditEntries)
        {
            entry.DetailsJson.ShouldNotBeNull();
            entry.DetailsJson!.ShouldNotContain("SECRET");
            entry.DetailsJson.ShouldContain("DescriptionLength");
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Apply_SemanticChange_WithDescriptionChange_UsesRevokeCreatePath()
    {
        // T-GOV158I1-09 (SEC-GOV158I1-07): a rule change plus a new description never takes the in-place path.
        var (svc, repo) = Create();
        await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1", [Policy("old")]));
        var oldId = repo.Active(DateTimeOffset.UtcNow).Single().Id;

        var changed = Policy("new") with { ColumnRules = [new DeclarativeColumnRule("id", "Mask")] };
        var result = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("2", [changed]));

        result.Applied.ShouldBe(1);
        result.Revoked.ShouldBe(1);
        result.Updated.ShouldBe(0);
        var now = repo.Active(DateTimeOffset.UtcNow).Single();
        now.Id.ShouldNotBe(oldId);
        now.Description.ShouldBe("new");
        repo.DescriptionUpdateCount.ShouldBe(0);
    }
}
