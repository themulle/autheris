using System.Security.Claims;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #111 WP-4 (A-7, BC-11): the manifest <c>Datasets</c> section is applied through the admin service with the caller principal.</summary>
public sealed class DeclarativeConsentApplyDatasetsTests
{
    private static readonly ClaimsPrincipal Caller = new(new ClaimsIdentity(
        [DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ADMIN"), new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")], "test"));

    private readonly IDatasetCatalogAdminService _datasets = Substitute.For<IDatasetCatalogAdminService>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();

    private DeclarativeConsentApplyService Create(IDatasetCatalogAdminService? datasets = null) =>
        new(Substitute.For<ITableMetadataRepository>(), _consents, Substitute.For<IAuditLogRepository>(), null, datasets);

    private static DeclarativeConsentManifest Manifest(params DeclarativeDatasetCatalog[] datasets) => new("1", [], datasets);

    private void Returns(DatasetCatalogOutcome outcome, bool? dryRun = null)
    {
        _datasets.ApplyAsync(default, default!, default!, default!, default, default).ReturnsForAnyArgs(new DatasetCatalogResult(outcome));
    }

    [Fact]
    public async Task DatasetsOnlyManifest_IsApplied_ThroughTheServiceWithTheCallerPrincipal()
    {
        Returns(DatasetCatalogOutcome.Applied);

        var result = await Create(_datasets).ApplyManifestAsync(Caller, Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "orders", ["pii"], "Summary")));

        result.Applied.ShouldBe(1);
        result.TotalEvaluated.ShouldBe(1);
        await _datasets.Received(1).ApplyAsync(Caller, "sales.dbo.orders",
            Arg.Is<DatasetCatalogEdit>(e => e.ReplaceTags!.Single() == "pii" && e.Visibility == "Summary" && e.AddTags == null),
            "manifest", Arg.Any<CancellationToken>(), false);
    }

    [Fact]
    public async Task NullFields_AreUnchanged_NeverAReset()
    {
        Returns(DatasetCatalogOutcome.Unchanged);

        var result = await Create(_datasets).ApplyManifestAsync(Caller, Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "orders")));

        result.Unchanged.ShouldBe(1);
        await _datasets.Received().ApplyAsync(Caller, "sales.dbo.orders",
            Arg.Is<DatasetCatalogEdit>(e => e.ReplaceTags == null && e.Visibility == null), "manifest", Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_11_DryRun_RunsTheServiceDryRun_AndWritesNothing()
    {
        Returns(DatasetCatalogOutcome.Applied);

        var result = await Create(_datasets).ApplyManifestAsync(Caller, Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "orders", null, "Full")), dryRun: true);

        result.DryRun.ShouldBeTrue();
        result.Applied.ShouldBe(1);
        await _datasets.DidNotReceive().ApplyAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string>(), Arg.Any<DatasetCatalogEdit>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), false);
        await _datasets.Received(1).ApplyAsync(Caller, "sales.dbo.orders", Arg.Any<DatasetCatalogEdit>(), "manifest", Arg.Any<CancellationToken>(), true);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(DatasetCatalogOutcome.InvalidTag, "INVALID_TAG")]
    [InlineData(DatasetCatalogOutcome.TagLimitExceeded, "TAG_LIMIT_EXCEEDED")]
    [InlineData(DatasetCatalogOutcome.InvalidVisibility, "INVALID_VISIBILITY")]
    [InlineData(DatasetCatalogOutcome.Forbidden, "FORBIDDEN")]
    [InlineData(DatasetCatalogOutcome.Unavailable, "UNAVAILABLE")]
    public async Task SEC_111_11_RejectedEntry_FailsTheWholeManifest_BeforeAnythingIsApplied(DatasetCatalogOutcome outcome, string code)
    {
        _datasets.ApplyAsync(default, default!, default!, default!, default, default).ReturnsForAnyArgs(new DatasetCatalogResult(outcome));

        var ex = await Should.ThrowAsync<DeclarativeDatasetCatalogException>(() => Create(_datasets).ApplyManifestAsync(Caller,
            Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "orders", ["x"], null))));

        ex.ErrorCode.ShouldBe(code);
        await _datasets.DidNotReceive().ApplyAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<string>(), Arg.Any<DatasetCatalogEdit>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), false);
    }

    [Fact]
    public async Task UnknownOrNotManageableTable_IsReportedAndSkipped_WithoutDistinguishing()
    {
        Returns(DatasetCatalogOutcome.NotFound);

        var result = await Create(_datasets).ApplyManifestAsync(Caller, Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "ghost", ["x"], null)));

        result.Applied.ShouldBe(0);
        result.Details.ShouldContain(d => d.Contains("'sales.dbo.ghost'") && d.Contains("not found"));
    }

    [Fact]
    public async Task OldManifestWithoutDatasets_NeverTouchesTheService()
    {
        var result = await Create(_datasets).ApplyManifestAsync(Caller, new DeclarativeConsentManifest("1", []));

        result.TotalEvaluated.ShouldBe(0);
        _datasets.ReceivedCalls().ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_11_NoServiceConfigured_DatasetsAreNotApplied_AndSaidSo()
    {
        var result = await Create(null).ApplyManifestAsync(Caller, Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "orders", null, "Summary")));

        result.Applied.ShouldBe(0);
        result.Details.ShouldContain(d => d.Contains("not available"));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_11_RealService_TenantGovernanceAdminInMulti_ForeignAndNonexistentDataset_AreIndistinguishable()
    {
        var tables = Substitute.For<ITableMetadataRepository>();
        var foreign = new TableIdentifier("sales", "dbo", "foreign_t");
        tables.GetActiveTableIdentifierAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<TableIdentifier>().Equals(foreign) ? foreign : (TableIdentifier?)null);
        var owners = Substitute.For<IDataOwnershipRepository>();
        var audit = Substitute.For<Autheris.Application.Audit.IAuditWriter>();
        var service = new DatasetCatalogAdminService(tables, owners, audit,
            new Autheris.Tests.Common.StaticOptionsMonitor<Autheris.Domain.Options.GatewayOptions>(new Autheris.Domain.Options.GatewayOptions()), TimeProvider.System);
        var tenantAdmin = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TA"), new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"), new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")
        ], "test"));
        var apply = new DeclarativeConsentApplyService(Substitute.For<ITableMetadataRepository>(), _consents, Substitute.For<IAuditLogRepository>(), null, service);

        var foreignResult = await apply.ApplyManifestAsync(tenantAdmin, Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "foreign_t", ["x"], "Summary")));
        var missingResult = await apply.ApplyManifestAsync(tenantAdmin, Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "ghost_t", ["x"], "Summary")));

        foreignResult.Applied.ShouldBe(missingResult.Applied);
        foreignResult.Unchanged.ShouldBe(missingResult.Unchanged);
        foreignResult.TotalEvaluated.ShouldBe(missingResult.TotalEvaluated);
        foreignResult.Details.Select(d => d.Replace("foreign_t", "N")).ShouldBe(missingResult.Details.Select(d => d.Replace("ghost_t", "N")));
        await tables.DidNotReceiveWithAnyArgs().SetDatasetVisibilityAsync(default, default, default, default);
        // The skipped entries are one aggregated probe audit per manifest.
        await audit.Received(2).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e => e.DetailsJson.Contains("\"source\":\"manifest\"")), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_DryRun_WritesNoProbeAudit_RealApplyDoes()
    {
        Returns(DatasetCatalogOutcome.NotFound);
        var manifest = Manifest(new DeclarativeDatasetCatalog("sales", "dbo", "ghost", ["x"], null));

        await Create(_datasets).ApplyManifestAsync(Caller, manifest, dryRun: true);
        await _datasets.DidNotReceiveWithAnyArgs().RecordManifestProbesAsync(default, default, default);

        await Create(_datasets).ApplyManifestAsync(Caller, manifest, dryRun: false);
        await _datasets.Received(1).RecordManifestProbesAsync(Caller, 1, Arg.Any<CancellationToken>());
    }
}
