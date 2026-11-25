using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Declarative;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Declarative;

/// <summary>
/// Issue #1121: a declarative grant has one decider (the Data Governance apply or the one confirmation of an IT proposal), so it cannot carry the
/// two distinct approvals a grant on secret data needs. An Allow on a secret target is refused on every declarative path; it is requested through
/// the permission request engine instead.
/// </summary>
[Trait("Category", "Security")]
public sealed class SecretFourEyesDeclarativeTests
{
    private const string ItSid = "S-1-5-21-IT-ADMIN";
    private const string DgSid = "S-1-5-21-DG-ADMIN";
    private const string Justification = "quarterly analytics access";
    private static readonly DateTimeOffset ValidUntil = DateTimeOffset.UtcNow.AddDays(30);

    private readonly FakeConsentRepository _consents = new();
    private readonly InMemoryManifestProposalRepository _proposals = new();
    private readonly InMemoryAuditWriter _audit = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly HashSet<string> _secretTables = new(StringComparer.OrdinalIgnoreCase) { "payroll" };
    private readonly HashSet<string> _secretColumnTables = new(StringComparer.OrdinalIgnoreCase);

    private static ClaimsPrincipal Caller(string sid, string role) => new(new ClaimsIdentity(
        [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, sid), new Claim(ClaimTypes.Role, role)], "test"));

    private static ClaimsPrincipal It() => Caller(ItSid, "TenantAdmin");

    private static ClaimsPrincipal Dg() => Caller(DgSid, "GovernanceAdmin");

    private static ITableMetadataRepository Tables()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid() },
                Identifier = ci.Arg<TableIdentifier>(),
                Columns = [new TableColumn { ColumnName = "id" }]
            });
        return repo;
    }

    private IProtectionSnapshotProvider Protection() => NullFake.Create<IProtectionSnapshotProvider>((_, args) =>
    {
        var table = (TableIdentifier)args![1]!;
        var tableClass = _secretTables.Contains(table.TableName) ? ProtectionClass.Secret : ProtectionClass.Internal;
        var columns = new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase);
        if (_secretColumnTables.Contains(table.TableName))
        {
            columns["salary"] = new ColumnProtection(ProtectionClass.Secret, null);
        }

        return new ValueTask<EffectiveProtection>(new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), null, tableClass, tableClass, null, columns, default));
    });

    private DeclarativeConsentApplyService Planner() =>
        new(Tables(), _consents, Substitute.For<IAuditLogRepository>(), protectionProvider: Protection());

    private ManifestProposalService Proposals(DeclarativeConsentApplyService planner) => new(planner, _proposals, _audit, timeProvider: _time);

    private static DeclarativeConsentPolicy Policy(string table, string effect = "Allow") =>
        new("default", "sales", table, "Group", "S-1-5-21-GROUP-A", effect, Description: Justification, ValidUntil: ValidUntil);

    private static DeclarativeConsentManifest Manifest(params DeclarativeConsentPolicy[] policies) => new("1", policies, Name: "analytics");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Data_governance_apply_of_a_secret_grant_is_refused(bool dryRun)
    {
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() =>
            Planner().ApplyManifestAsync(Dg(), Manifest(Policy("orders"), Policy("payroll")), dryRun));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.SecretRequiresFourEyes);
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_secret_column_makes_the_table_secret_for_the_manifest()
    {
        _secretColumnTables.Add("customers");

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() => Planner().ApplyManifestAsync(Dg(), Manifest(Policy("customers"))));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.SecretRequiresFourEyes);
        _consents.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_deny_on_secret_data_only_restricts_and_stays_allowed()
    {
        var result = await Planner().ApplyManifestAsync(Dg(), Manifest(Policy("payroll", effect: "Deny")));

        result.Applied.ShouldBe(1);
    }

    [Fact]
    public async Task Non_secret_manifest_still_applies_with_one_decision()
    {
        var result = await Planner().ApplyManifestAsync(Dg(), Manifest(Policy("orders")));

        result.Applied.ShouldBe(1);
        _consents.ReplaceCalls.ShouldBe(1);
    }

    [Fact]
    public async Task It_proposal_with_a_secret_grant_is_refused_at_submit()
    {
        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() => Proposals(Planner()).SubmitAsync(It(), Manifest(Policy("payroll"))));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.SecretRequiresFourEyes);
        _proposals.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Confirming_a_proposal_whose_table_became_secret_is_refused_and_applies_nothing()
    {
        var planner = Planner();
        var sut = Proposals(planner);
        var proposal = await sut.SubmitAsync(It(), Manifest(Policy("ledger")));
        _secretTables.Add("ledger");

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(() => sut.ConfirmAsync(Dg(), proposal.Id));

        ex.ErrorCode.ShouldBe(GrantErrorCodes.SecretRequiresFourEyes);
        _consents.ReplaceCalls.ShouldBe(0);
        _proposals.Single(proposal.Id).Status.ShouldBe(ManifestProposalStatus.Pending);
    }
}
