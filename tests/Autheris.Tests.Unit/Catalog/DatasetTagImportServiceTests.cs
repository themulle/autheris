namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Audit;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>Issue #111 WP-6 (A-2, A-3): dbt tag import with the total cap from the strict before-state.</summary>
public sealed class DatasetTagImportServiceTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private DatasetCatalogSettings _before = new([], DatasetVisibility.Hidden);
    private IReadOnlyList<string>? _written;

    public DatasetTagImportServiceTests()
    {
        _tables.GetDatasetCatalogSettingsAsync(Orders, true, Arg.Any<CancellationToken>()).Returns(_ => _before);
        _tables.ReplaceDatasetTagsAsync(Orders, Arg.Any<IReadOnlyList<string>>(), DatasetTagSource.Dbt, Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _written = ci.Arg<IReadOnlyList<string>>();
                var after = _before with
                {
                    Tags = _before.Tags.Where(t => t.Source == DatasetTagSource.Admin)
                        .Concat(_written.Where(w => _before.Tags.All(t => t.Tag != w || t.Source != DatasetTagSource.Admin)).Select(w => new DatasetTag(w, DatasetTagSource.Dbt))).ToList()
                };
                return new DatasetCatalogChange(Orders, _before, after);
            });
    }

    private DatasetTagImportService Create(int max = 32) => new(_tables, _audit,
        new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions { Catalog = new DataCatalogOptions { MaxTagsPerDataset = max } }),
        NullLogger<DatasetTagImportService>.Instance);

    [Fact]
    public async Task NormalizesLowerCases_AndDeduplicates_ThenWritesAsDbtSource()
    {
        var result = await Create().ImportDbtTagsAsync(Orders, ["PII", "pii", " Finance "], default);

        result.Changed.ShouldBeTrue();
        _written!.Order(StringComparer.Ordinal).ShouldBe(["finance", "pii"]);
        await _tables.Received(1).ReplaceDatasetTagsAsync(Orders, Arg.Any<IReadOnlyList<string>>(), DatasetTagSource.Dbt, Arg.Any<Sid>(), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_Dbt_InvalidTagsAreDroppedOneByOne_NeverFailingTheImport_ValuesAreNotInTheResult()
    {
        var result = await Create().ImportDbtTagsAsync(Orders, ["ok", "has space", "<script>", new string('a', 65), "", null], default);

        result.InvalidDropped.ShouldBe(5);
        _written.ShouldBe(["ok"]);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task A3_Overflow_DropsTheExcessInSortedOrder_AdminTagsCountAgainstTheCap()
    {
        _before = new DatasetCatalogSettings([new DatasetTag("a1", DatasetTagSource.Admin), new DatasetTag("a2", DatasetTagSource.Admin)], DatasetVisibility.Hidden);

        var result = await Create(max: 4).ImportDbtTagsAsync(Orders, ["z", "m", "b", "c"], default);

        result.OverflowDropped.ShouldBe(2);
        _written!.Order(StringComparer.Ordinal).ShouldBe(["b", "c"]);
    }

    [Fact]
    public async Task A3_TagAlreadyPresentAsAdmin_DoesNotCountTwice()
    {
        _before = new DatasetCatalogSettings([new DatasetTag("pii", DatasetTagSource.Admin)], DatasetVisibility.Hidden);

        var result = await Create(max: 2).ImportDbtTagsAsync(Orders, ["pii", "x"], default);

        result.OverflowDropped.ShouldBe(0);
        _written!.Order(StringComparer.Ordinal).ShouldBe(["pii", "x"]);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task A2_Changed_WritesExactlyOneTagsAuditEntry_WithSourceDbt_Unchanged_WritesNone()
    {
        await Create().ImportDbtTagsAsync(Orders, ["pii"], default);

        await _audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogTagsChanged && e.TargetTable == Orders.ToString() && e.DetailsJson.Contains("\"source\":\"dbt\"")
            && e.DetailsJson.Contains("\"after\":{\"visibility\":\"Hidden\",\"tags\":[\"pii\"]}")), CancellationToken.None);

        _audit.ClearReceivedCalls();
        _tables.ReplaceDatasetTagsAsync(Orders, Arg.Any<IReadOnlyList<string>>(), DatasetTagSource.Dbt, Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetCatalogChange(Orders, _before, _before));

        var result = await Create().ImportDbtTagsAsync(Orders, ["pii"], default);

        result.Changed.ShouldBeFalse();
        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_09_StrictReadFails_NothingIsWritten_ResultIsFailed_NoThrow()
    {
        _tables.GetDatasetCatalogSettingsAsync(Orders, true, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        var result = await Create().ImportDbtTagsAsync(Orders, ["pii"], default);

        result.Failed.ShouldBeTrue();
        await _tables.DidNotReceiveWithAnyArgs().ReplaceDatasetTagsAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task UnknownTable_IsNoOp()
    {
        var result = await Create().ImportDbtTagsAsync(new TableIdentifier("x", "y", "z"), ["pii"], default);

        result.ShouldBe(DatasetTagImportResult.Unchanged);
    }

    [Fact]
    public async Task EmptyDbtTags_ReplacesWithNone_SoRemovedModelTagsAreRemoved()
    {
        _before = new DatasetCatalogSettings([new DatasetTag("old", DatasetTagSource.Dbt)], DatasetVisibility.Hidden);

        var result = await Create().ImportDbtTagsAsync(Orders, [], default);

        _written.ShouldBeEmpty();
        result.Changed.ShouldBeTrue();
    }
}
