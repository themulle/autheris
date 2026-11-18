using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Tests.Unit.TestSupport;
using Autheris.Application.Governance.Services;
using Autheris.Application.Governance.Validation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Issue #806 review: the store keeps a rule's scopes comma-separated and drops empty entries on read, so a blank scope entry would turn a
/// scope rule into a base rule for every scope. Manifests with a blank scope entry or a comma in an entry are rejected before any read or write.
/// </summary>
[Trait("Category", "Security")]
public sealed class DeclarativeConsentScopeEntryValidationTests
{
    private const string Marker = "Report.Read-MARKER-806";

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "test"));

    private static ITableMetadataRepository Tables()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Autheris.Domain.Model.Table { Id = Guid.NewGuid() },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "email" }]
            });
        return repo;
    }

    private static DeclarativeConsentPolicy Policy(string table, params DeclarativeColumnRule[] rules) =>
        new("default", "sales", table, "Group", "S-1-5-21-GROUP-DS", "Allow", rules, ValidUntil: DateTimeOffset.UtcNow.AddDays(30));

    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("\t", true)]
    [InlineData(null, false)]
    [InlineData(",", false)]
    [InlineData("Agent.Read," + Marker, true)]
    public async Task BlankOrCommaScopeEntry_RejectsTheWholeManifest_BeforeAnyReadOrWrite(string? scope, bool dryRun)
    {
        var tables = Tables();
        var repo = new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(tables, repo, Substitute.For<IAuditLogRepository>());
        var manifest = new DeclarativeConsentManifest("1",
        [
            Policy("orders", new DeclarativeColumnRule("id", "Clear")),
            Policy("items", new DeclarativeColumnRule("email", "Mask"), new DeclarativeColumnRule("email", "Clear", ["Agent.Read", scope!])),
        ]);

        var ex = await Should.ThrowAsync<DeclarativeManifestValidationException>(() => svc.ApplyManifestAsync(Admin(), manifest, dryRun));

        ex.PolicyIndex.ShouldBe(1);
        ex.Violation.ShouldBe(DeclarativeManifestViolation.ScopeEntry);
        ex.Message.ShouldNotContain(Marker);
        repo.ReplaceCalls.ShouldBe(0);
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
        await tables.DidNotReceiveWithAnyArgs().GetTableMetadataAsync(default, default);
    }

    [Fact]
    public async Task EmptyScopeListAndPaddedScopeName_AreAccepted()
    {
        var repo = new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, Substitute.For<IAuditLogRepository>());

        var result = await svc.ApplyManifestAsync(Admin(), new DeclarativeConsentManifest("1",
            [Policy("orders", new DeclarativeColumnRule("email", "Clear", []), new DeclarativeColumnRule("email", "Mask", [" Agent.Read "]))]));

        result.Applied.ShouldBe(1);
        repo.Active(DateTimeOffset.UtcNow).Single().ColumnRules.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(true, "Agent.Read", " Report.Read ")]
    [InlineData(false, "Agent.Read", "")]
    [InlineData(false, "   ")]
    [InlineData(false, "Agent.Read,Report.Read")]
    public void Validator_AcceptsOnlyScopeNames(bool valid, params string[] scopes)
    {
        ConsentRuleScopeValidator.AreValid(scopes).ShouldBe(valid);
        ConsentRuleScopeValidator.AreValid(null).ShouldBeTrue();
        ConsentRuleScopeValidator.AreValid([]).ShouldBeTrue();
    }
}
