using System.Security.Claims;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>NL-SC-10 / NL-SC-11 (O-6): a declarative apply needs the caller's SID and audits exactly that SID.</summary>
[Trait("Category", "Security")]
public sealed class DeclarativeConsentApplyActorTests
{
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

    private static DeclarativeConsentManifest Manifest() =>
        new("1", [new DeclarativeConsentPolicy("default", "sales", "orders", "Role", "DataScientist", "Allow")]);

    [Fact]
    public async Task Declarative_Apply_Without_Caller_Sid_Is_Rejected()
    {
        var repo = new FakeConsentRepository();
        var audit = Substitute.For<IAuditLogRepository>();
        var tables = Tables();
        var svc = new DeclarativeConsentApplyService(tables, repo, audit);
        var noSid = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")], "test"));

        var ex = await Should.ThrowAsync<System.Security.SecurityException>(() => svc.ApplyManifestAsync(noSid, Manifest()));

        ex.Message.ShouldNotContain("GOVERNANCE_ADMIN");
        repo.ReplaceCalls.ShouldBe(0);
        repo.DirectCreateCalls.ShouldBe(0);
        repo.ReplaceAuditEntries.ShouldBeEmpty();
        repo.Active(DateTimeOffset.UtcNow).ShouldBeEmpty();
        await tables.DidNotReceiveWithAnyArgs().GetTableMetadataAsync(default!, default);
        audit.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Declarative_Apply_Audit_Actor_Is_Supplied_Sid()
    {
        var repo = new FakeConsentRepository();
        var svc = new DeclarativeConsentApplyService(Tables(), repo, Substitute.For<IAuditLogRepository>());
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-SUPPLIED"), new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")], "test"));

        await svc.ApplyManifestAsync(caller, Manifest());

        repo.ReplaceAuditEntries.ShouldNotBeEmpty();
        repo.ReplaceAuditEntries.ShouldAllBe(e => e.ActorSid.Value == "S-1-5-21-SUPPLIED");
    }

    [Fact]
    public void No_Fixed_Gitops_Sid_Literal_Remains()
    {
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "Autheris.sln"))) root = Path.GetDirectoryName(root);
        root.ShouldNotBeNull("repository root not found; the source scan must not pass vacuously");

        var literal = "GITOPS" + "-SYNC";
        var hits = new[] { "src", "tests" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root!, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains(literal, StringComparison.Ordinal))
            .ToList();
        hits.ShouldBeEmpty();
    }
}
