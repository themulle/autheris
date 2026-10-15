namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Streaming;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Cdc;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>AC-5: activation checks against the DECLARED primary key with stable codes (PLAN-CDC-99 D-A3).</summary>
public sealed class CdcTableActivatorTests
{
    private static CdcTableSettings Settings(CdcProducerHarness h) => CdcTableSettings.Resolve(h.Options.MssqlChangeTracking).Single();

    private static async Task<(CdcActivationResult Result, CdcProducerHarness Harness)> ActivateAsync(Action<CdcProducerHarness>? arrange = null, TableMetadata? metadata = null)
    {
        var h = new CdcProducerHarness(metadata: metadata);
        arrange?.Invoke(h);
        return (await h.Activator.EnsureActiveAsync(Settings(h)), h);
    }

    [Fact]
    public async Task AllChecksPass_ActivatesAndRecordsActiveStatus()
    {
        var (result, h) = await ActivateAsync();

        result.IsActive.ShouldBeTrue();
        result.Activation!.PrimaryKey.ShouldBe(["Id"]);
        result.Activation.Columns.ShouldBe(["Id", "Name"]);
        h.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.Active);
        h.InnerStore.Only.ActivationCode.ShouldBeNull();
    }

    [Fact]
    public async Task Activation_NeverReadsASourceCatalogView()
    {
        var (_, h) = await ActivateAsync();

        h.Source.Statements.ShouldNotBeEmpty();
        h.Source.Statements.ShouldAllBe(s => !s.Contains("sys.", StringComparison.OrdinalIgnoreCase) && !s.Contains("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownTable_IsTableUnavailable()
    {
        var h = new CdcProducerHarness();
        h.Metadata.GetTableMetadataAsync(Arg.Any<Autheris.Domain.Common.TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(null));

        (await h.Activator.EnsureActiveAsync(Settings(h))).Code.ShouldBe(CdcActivationCodes.TableUnavailable);
    }

    [Fact]
    public async Task UnavailableMetadata_IsTableUnavailable()
    {
        var h = new CdcProducerHarness();
        h.Metadata.GetTableMetadataAsync(Arg.Any<Autheris.Domain.Common.TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns<Task<TableMetadata?>>(_ => throw new Autheris.Domain.Exceptions.TableMetadataUnavailableException(CdcProducerHarness.TableId));

        (await h.Activator.EnsureActiveAsync(Settings(h))).Code.ShouldBe(CdcActivationCodes.TableUnavailable);
    }

    [Theory]
    [InlineData("PostgreSQL", "erp", true)]
    [InlineData("SqlServer", "erp", false)]
    [InlineData("SqlServer", "missing", true)]
    [InlineData("SqlServer", "", true)]
    public async Task InactiveNonSqlServerOrUnresolvableSource_IsTableUnavailable(string sourceType, string sourceName, bool active)
    {
        var (result, _) = await ActivateAsync(metadata: CdcProducerHarness.Meta(sourceType: sourceType, sourceName: sourceName, active: active));

        result.Code.ShouldBe(CdcActivationCodes.TableUnavailable);
    }

    [Fact]
    public async Task SourceConnectionOfAnotherProvider_IsTableUnavailable()
    {
        var h = new CdcProducerHarness();
        h.Options.DataSources.Connections["erp"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "Host=x" };

        (await h.Activator.EnsureActiveAsync(Settings(h))).Code.ShouldBe(CdcActivationCodes.TableUnavailable);
        h.Source.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task ExplicitDataSourceOption_OverridesTheCatalogSource()
    {
        var h = new CdcProducerHarness(o => new MssqlChangeTrackingOptions { Enabled = true, Tables = [new CdcTrackedTableOptions { Table = "finance.dbo.invoices", DataSource = "other" }] });
        h.Options.DataSources.Connections["other"] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=other" };

        var result = await h.Activator.EnsureActiveAsync(Settings(h));

        result.Activation!.Source.ConnectionString.ShouldBe("Server=other");
    }

    [Fact]
    public async Task NoDeclaredPrimaryKey_IsNoDeclaredPk_WithoutTouchingTheSource()
    {
        var (result, h) = await ActivateAsync(metadata: CdcProducerHarness.Meta(pk: []));

        result.Code.ShouldBe(CdcActivationCodes.NoDeclaredPk);
        h.Source.Opened.ShouldBe(0);
        h.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.ActivationFailed);
        h.InnerStore.Only.ActivationCode.ShouldBe(CdcActivationCodes.NoDeclaredPk);
    }

    [Fact]
    public async Task InvalidIdentifierInKeyOrColumns_IsNoDeclaredPk()
    {
        var (result, _) = await ActivateAsync(metadata: CdcProducerHarness.Meta(columns: ["Id", "Na]me; DROP TABLE x"]));

        result.Code.ShouldBe(CdcActivationCodes.NoDeclaredPk);
    }

    [Fact]
    public async Task RequiredTenantColumnMissing_IsTenantColumnMissing_UnlessExempt()
    {
        var h = new CdcProducerHarness(requireTenantColumn: true);
        (await h.Activator.EnsureActiveAsync(Settings(h))).Code.ShouldBe(CdcActivationCodes.TenantColumnMissing);

        h.Options.DataSources.TenantColumnExemptTables.Add("dbo.invoices");
        h.Activator.Invalidate();
        (await h.Activator.EnsureActiveAsync(Settings(h))).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task TrackingDisabled_IsTrackingDisabled()
    {
        var (result, _) = await ActivateAsync(h => h.Source.MinValidVersion = null);

        result.Code.ShouldBe(CdcActivationCodes.TrackingDisabled);
    }

    [Theory]
    [InlineData(229)]
    [InlineData(297)]
    public async Task PermissionDenied_OnTheSource_IsTrackingDisabled_NotATransientError(int errorNumber)
    {
        // SEC-CDC99-22: the login lacks VIEW CHANGE TRACKING
        var (result, h) = await ActivateAsync(h => h.Source.OnStatement = _ => throw new FakeSqlException(errorNumber));

        result.Code.ShouldBe(CdcActivationCodes.TrackingDisabled);
        h.InnerStore.Only.ActivationCode.ShouldBe(CdcActivationCodes.TrackingDisabled);
    }

    [Theory]
    [InlineData("Id,Other")]
    [InlineData("Other")]
    [InlineData("")]
    public async Task ChangeTableKeyDifferentFromDeclaredPk_IsPkMismatch(string changeTableKey)
    {
        var (result, _) = await ActivateAsync(h => h.Source.ChangeTableKey = changeTableKey.Split(',', StringSplitOptions.RemoveEmptyEntries));

        result.Code.ShouldBe(CdcActivationCodes.PkMismatch);
    }

    [Fact]
    public async Task CompositeKeyInDifferentOrder_IsPkMismatch_SameOrderIgnoringCaseIsFine()
    {
        var meta = CdcProducerHarness.Meta(columns: ["Id", "Region", "Name"], pk: ["Id", "Region"]);
        (await ActivateAsync(h => h.Source.ChangeTableKey = ["Region", "Id"], meta)).Result.Code.ShouldBe(CdcActivationCodes.PkMismatch);
        (await ActivateAsync(h => h.Source.ChangeTableKey = ["id", "REGION"], meta)).Result.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task SnapshotIsolationOff_IsSnapshotIsolationOff()
    {
        var (result, h) = await ActivateAsync(h => h.Source.SnapshotOff = true);

        result.Code.ShouldBe(CdcActivationCodes.SnapshotIsolationOff);
        h.InnerStore.Only.ActivationCode.ShouldBe(CdcActivationCodes.SnapshotIsolationOff);
    }

    [Fact]
    public async Task SnapshotIsolationOff_IsAccepted_WhenNonSnapshotReadIsAllowed()
    {
        var h = new CdcProducerHarness(o => new MssqlChangeTrackingOptions { Enabled = true, TrackedTables = ["finance.dbo.invoices"], AllowNonSnapshotRead = true });
        h.Source.SnapshotOff = true;

        (await h.Activator.EnsureActiveAsync(Settings(h))).IsActive.ShouldBeTrue();
        h.Source.Isolations.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnavailableStreamKeys_IsKeyUnavailable()
    {
        var (result, _) = await ActivateAsync(h => h.Crypto.KeysUnavailable = true);

        result.Code.ShouldBe(CdcActivationCodes.KeyUnavailable);
    }

    [Fact]
    public async Task TransientSourceError_Propagates_AndIsNotAnActivationCode()
    {
        var h = new CdcProducerHarness();
        h.Source.OnStatement = _ => throw new InvalidOperationException("connection reset");

        await Should.ThrowAsync<InvalidOperationException>(() => h.Activator.EnsureActiveAsync(Settings(h)));

        h.InnerStore.Only.ActivationCode.ShouldBeNull();
    }

    [Fact]
    public async Task FirstFailureWins_ChecksRunInOrder()
    {
        // no declared PK AND tracking disabled: the PK check comes first
        var (result, _) = await ActivateAsync(h => h.Source.MinValidVersion = null, CdcProducerHarness.Meta(pk: []));

        result.Code.ShouldBe(CdcActivationCodes.NoDeclaredPk);
    }

    [Fact]
    public async Task HaltedStream_StaysHalted_AndNeverOpensTheSource()
    {
        var h = new CdcProducerHarness();
        var stream = await h.Store.EnsureStreamAsync(CdcTableActivator.StreamSource, CdcProducerHarness.TableId);
        await h.Store.SetStatusAsync(stream.Id, CdcStreamStatus.Halted, CdcActivationCodes.WatermarkExpired);

        var result = await h.Activator.EnsureActiveAsync(Settings(h));

        result.Code.ShouldBe(CdcActivationCodes.WatermarkExpired);
        h.Source.Opened.ShouldBe(0);
        h.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.Halted);
    }

    [Fact]
    public async Task SuccessfulActivation_IsCached_UntilInvalidatedOrSettingsChange()
    {
        var h = new CdcProducerHarness();
        var settings = Settings(h);

        await h.Activator.EnsureActiveAsync(settings);
        await h.Activator.EnsureActiveAsync(settings);
        h.Source.Opened.ShouldBe(1);

        await h.Activator.EnsureActiveAsync(settings with { BatchSize = 7 });
        h.Source.Opened.ShouldBe(2);

        h.Activator.Invalidate();
        await h.Activator.EnsureActiveAsync(settings with { BatchSize = 7 });
        h.Source.Opened.ShouldBe(3);
    }

    [Fact]
    public async Task FailedActivation_IsNotCached_SoTheNextAttemptRechecks()
    {
        var h = new CdcProducerHarness();
        h.Source.MinValidVersion = null;
        var settings = Settings(h);
        (await h.Activator.EnsureActiveAsync(settings)).Code.ShouldBe(CdcActivationCodes.TrackingDisabled);

        h.Source.MinValidVersion = 0;

        (await h.Activator.EnsureActiveAsync(settings)).IsActive.ShouldBeTrue();
        h.InnerStore.Only.Status.ShouldBe(CdcStreamStatus.Active);
        h.InnerStore.Only.ActivationCode.ShouldBeNull();
    }

    [Fact]
    public async Task CatalogVersionChange_Reactivates_AfterTheCheckInterval()
    {
        var version = Substitute.For<ICatalogVersionProvider>();
        var current = "v1";
        version.GetVersionAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(current));
        var h = new CdcProducerHarness(catalogVersion: version);
        var settings = Settings(h);
        await h.Activator.EnsureActiveAsync(settings);

        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.Activator.EnsureActiveAsync(settings);
        h.Source.Opened.ShouldBe(1); // same version: reused

        current = "v2";
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.Activator.EnsureActiveAsync(settings);
        h.Source.Opened.ShouldBe(2);
    }

    [Fact]
    public async Task CachedActivation_ReturnsAFreshStreamDescriptor()
    {
        var h = new CdcProducerHarness();
        var settings = Settings(h);
        var first = await h.Activator.EnsureActiveAsync(settings);
        await h.SeedWatermarkAsync(42);

        var second = await h.Activator.EnsureActiveAsync(settings);

        first.Activation!.Stream.SourceWatermark.ShouldBeNull();
        second.Activation!.Stream.SourceWatermark.ShouldBe(42);
    }
}
