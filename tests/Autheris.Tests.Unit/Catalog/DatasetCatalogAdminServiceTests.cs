namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>Issue #111 WP-4 (F-C5, SEC-111-08/09/12, A-5, A-9): the single writer of dataset tags and tiers.</summary>
public sealed class DatasetCatalogAdminServiceTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly IDataOwnershipRepository _owners = Substitute.For<IDataOwnershipRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly TestTime _time = new();
    private DatasetCatalogSettings _state = new([], DatasetVisibility.Hidden);

    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public DatasetCatalogAdminServiceTests()
    {
        _tables.GetActiveTableIdentifierAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<TableIdentifier>().Equals(Orders) ? Orders : (TableIdentifier?)null);
        _tables.GetDatasetCatalogSettingsAsync(Orders, true, Arg.Any<CancellationToken>()).Returns(_ => _state);
        _tables.ReplaceDatasetTagsAsync(Orders, Arg.Any<IReadOnlyList<string>>(), DatasetTagSource.Admin, Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var before = _state;
                var tags = ci.Arg<IReadOnlyList<string>>().Select(t => new DatasetTag(t, DatasetTagSource.Admin))
                    .Concat(before.Tags.Where(t => t.Source == DatasetTagSource.Dbt)).ToList();
                _state = before with { Tags = tags };
                return new DatasetCatalogChange(Orders, before, _state);
            });
        _tables.ChangeDatasetTagsAsync(Orders, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var before = _state;
                var remove = ci.ArgAt<IReadOnlyList<string>>(2).ToHashSet();
                var tags = before.Tags.Where(t => !(t.Source == DatasetTagSource.Admin && remove.Contains(t.Tag))).ToList();
                foreach (var a in ci.ArgAt<IReadOnlyList<string>>(1))
                {
                    if (tags.All(t => t.Tag != a)) tags.Add(new DatasetTag(a, DatasetTagSource.Admin));
                }

                _state = before with { Tags = tags };
                return new DatasetCatalogChange(Orders, before, _state);
            });
        _tables.SetDatasetVisibilityAsync(Orders, Arg.Any<DatasetVisibility>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var before = _state;
                _state = before with { Visibility = ci.Arg<DatasetVisibility>() };
                return new DatasetCatalogChange(Orders, before, _state);
            });
    }

    private DatasetCatalogAdminService Create(string? mode = null, int maxTags = 32, string[]? shared = null, IDataOwnershipRepository? owners = null)
        => new(_tables, owners ?? _owners, _audit,
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions
            {
                Tenancy = new TenancyOptions { Mode = mode },
                Catalog = new DataCatalogOptions { MaxTagsPerDataset = maxTags, SharedTables = shared ?? [] }
            }),
            _time);

    private static ClaimsPrincipal Admin() => Wp0.User("S-1-5-21-root", null, ["ClusterAdmin"]);
    private static ClaimsPrincipal Owner() => Wp0.User("S-1-5-21-owner", "tenant-a");
    private static ClaimsPrincipal Stranger(string sid = "S-1-5-21-stranger") => Wp0.User(sid, "tenant-a");

    private void OwnerIs(string sid) => _owners.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
        .Returns(ci => ci.Arg<Sid>().Value == sid && ci.Arg<TableIdentifier>().Equals(Orders));

    private static DatasetCatalogEdit Vis(string v) => new(null, null, null, v);
    private static DatasetCatalogEdit Replace(params string[] t) => new(t, null, null, null);

    [Fact]
    public async Task Admin_SetsFull_AuditsVisibilityWithBeforeAfterSourceTenantAndAuthType()
    {
        var result = await Create().ApplyAsync(Admin(), "sales.dbo.orders", Vis("Full"), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
        result.Settings!.Visibility.ShouldBe(DatasetVisibility.Full);
        await _audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogVisibilityChanged && e.Decision == "ALLOW" && e.TargetTable == Orders.ToString()
            && e.DetailsJson.Contains("\"before\":{\"visibility\":\"Hidden\"")
            && e.DetailsJson.Contains("\"after\":{\"visibility\":\"Full\"")
            && e.DetailsJson.Contains("\"source\":\"admin\"")
            && e.DetailsJson.Contains("\"authType\":\"Bearer\"")), CancellationToken.None);
    }

    [Fact]
    public async Task TagReplace_AuditsTagsChanged_NotVisibility()
    {
        var result = await Create().ApplyAsync(Admin(), "sales.dbo.orders", Replace("Pii", "finance"), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
        await _audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogTagsChanged && e.DetailsJson.Contains("\"pii\"") && e.DetailsJson.Contains("\"finance\"")), CancellationToken.None);
        await _audit.DidNotReceive().RecordRequiredAsync(Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.CatalogVisibilityChanged), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Idempotent_Edit_IsUnchanged_AndWritesNoAudit()
    {
        _state = new DatasetCatalogSettings([new DatasetTag("pii", DatasetTagSource.Admin)], DatasetVisibility.Summary);

        var result = await Create().ApplyAsync(Admin(), "sales.dbo.orders", new DatasetCatalogEdit(["pii"], null, null, "Summary"), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Unchanged);
        await _audit.DidNotReceive().RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditStoreUnavailable_Propagates_FailClosed()
    {
        _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AuditUnavailableException());

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            Create().ApplyAsync(Admin(), "sales.dbo.orders", Vis("Summary"), "admin", default));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_05_Owner_SetsSummaryAndTags_ButNotFull()
    {
        OwnerIs("S-1-5-21-owner");
        var svc = Create();

        (await svc.ApplyAsync(Owner(), "sales.dbo.orders", new DatasetCatalogEdit(["pii"], null, null, "Summary"), "admin", default))
            .Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
        (await svc.ApplyAsync(Owner(), "sales.dbo.orders", Vis("Hidden"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Applied);

        _state.Visibility.ShouldBe(DatasetVisibility.Hidden);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_05_Owner_AsksFull_IsForbidden_DenyAudited_AndNothingIsWritten_EvenTags()
    {
        OwnerIs("S-1-5-21-owner");

        var result = await Create().ApplyAsync(Owner(), "sales.dbo.orders", new DatasetCatalogEdit(["pii"], null, null, "full"), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Forbidden);
        _state.Tags.ShouldBeEmpty();
        _state.Visibility.ShouldBe(DatasetVisibility.Hidden);
        await _tables.DidNotReceiveWithAnyArgs().ReplaceDatasetTagsAsync(default, default!, default, default, default);
        await _tables.DidNotReceiveWithAnyArgs().SetDatasetVisibilityAsync(default, default, default, default);
        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.Decision == "DENY" && e.EventType == AuditEventTypes.CatalogVisibilityChanged && e.DetailsJson.Contains("\"requested\":\"Full\"")), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_05_Owner_SettingFullOnAnAlreadyFullDataset_IsStillForbidden()
    {
        OwnerIs("S-1-5-21-owner");
        _state = new DatasetCatalogSettings([], DatasetVisibility.Full);

        (await Create().ApplyAsync(Owner(), "sales.dbo.orders", Vis("Full"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Forbidden);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_08_Stranger_KnownAndUnknownDataset_AreTheSameNotFound_AndBothRunTheOwnershipLookup()
    {
        OwnerIs("S-1-5-21-owner");
        var svc = Create();

        var known = await svc.ApplyAsync(Stranger(), "sales.dbo.orders", Vis("Summary"), "admin", default);
        var unknown = await svc.ApplyAsync(Stranger(), "sales.dbo.missing", Vis("Summary"), "admin", default);
        var garbage = await svc.ApplyAsync(Stranger(), "not a dataset!!", Vis("Summary"), "admin", default);

        known.ShouldBe(new DatasetCatalogResult(DatasetCatalogOutcome.NotFound));
        unknown.ShouldBe(known);
        garbage.ShouldBe(known);
        await _owners.Received(3).IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>());
        await _tables.DidNotReceiveWithAnyArgs().SetDatasetVisibilityAsync(default, default, default, default);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_08_Stranger_WithInvalidBody_GetsNotFound_NotAValidationHint()
    {
        var result = await Create().ApplyAsync(Stranger(), "sales.dbo.orders", new DatasetCatalogEdit(["bad tag!"], null, null, "7"), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_08_OwnershipStoreDown_NonAdmin_IsUnavailable_BeforeAnyDatasetIsResolved()
    {
        _owners.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var known = await Create().ApplyAsync(Stranger(), "sales.dbo.orders", Vis("Summary"), "admin", default);
        var unknown = await Create().ApplyAsync(Stranger(), "sales.dbo.missing", Vis("Summary"), "admin", default);

        known.Outcome.ShouldBe(DatasetCatalogOutcome.Unavailable);
        unknown.Outcome.ShouldBe(DatasetCatalogOutcome.Unavailable);
        await _tables.DidNotReceiveWithAnyArgs().GetActiveTableIdentifierAsync(default, default);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_08_NoOwnershipRepository_NonAdmin_IsUnavailable_AdminStillWorks()
    {
        var svc = new DatasetCatalogAdminService(_tables, null, _audit,
            new StaticOptionsMonitor<GatewayOptions>(new GatewayOptions()), _time);

        (await svc.ApplyAsync(Stranger(), "sales.dbo.orders", Vis("Summary"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Unavailable);
        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", Vis("Summary"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_ProbeDenyAudit_IsThrottledPerPrincipal_ThenOneAggregatedEntryPerWindow()
    {
        var svc = Create();
        for (var i = 0; i < 25; i++)
        {
            (await svc.ApplyAsync(Stranger(), "sales.dbo.orders", Vis("Summary"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
        }

        await _audit.Received(20).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e => e.Decision == "DENY" && e.EventType == AuditEventTypes.CatalogVisibilityChanged), Arg.Any<CancellationToken>());

        // Another principal has its own budget.
        await svc.ApplyAsync(Stranger("S-1-5-21-other"), "sales.dbo.orders", Vis("Summary"), "admin", default);
        await _audit.Received(21).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e => e.Decision == "DENY" && e.EventType == AuditEventTypes.CatalogVisibilityChanged), Arg.Any<CancellationToken>());

        // The next window flushes the aggregated count of the suppressed ones.
        _time.Now += TimeSpan.FromMinutes(6);
        await svc.ApplyAsync(Stranger(), "sales.dbo.orders", Vis("Summary"), "admin", default);
        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogProbeSuppressed && e.DetailsJson.Contains("\"count\":5")), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_03_Multi_TenantGovernanceAdmin_IsNotAnAdminByRole_AndGetsNotFound()
    {
        var tenantAdmin = Wp0.User("S-1-5-21-ta", "tenant-a", ["GovernanceAdmin"]);

        (await Create("Multi").ApplyAsync(tenantAdmin, "sales.dbo.orders", Vis("Summary"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
        (await Create().ApplyAsync(Wp0.User("S-1-5-21-ca", null, ["GovernanceAdmin"]), "sales.dbo.orders", Vis("Summary"), "admin", default))
            .Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_03_Single_GovernanceAdminWithoutTenantClaim_IsUnrestricted_AndMayWriteFull()
    {
        var governanceAdmin = Wp0.User("S-1-5-21-ga", null, ["GovernanceAdmin"]);

        (await Create("Single").ApplyAsync(governanceAdmin, "sales.dbo.orders", Vis("Full"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_03_NoPrincipal_UnauthenticatedOrForwardAuthAdmin_NeverWrites()
    {
        var svc = Create();
        var forwardAuth = Wp0.User("S-1-5-21-fa", null, ["ClusterAdmin"], authType: "ForwardAuth");

        (await svc.ApplyAsync(null, "sales.dbo.orders", Vis("Full"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
        (await svc.ApplyAsync(new ClaimsPrincipal(new ClaimsIdentity()), "sales.dbo.orders", Vis("Full"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
        (await svc.ApplyAsync(forwardAuth, "sales.dbo.orders", Vis("Full"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
        await _tables.DidNotReceiveWithAnyArgs().SetDatasetVisibilityAsync(default, default, default, default);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("2")]
    [InlineData("Hidden,Full")]
    [InlineData(" Full")]
    [InlineData("Everything")]
    [InlineData("")]
    public async Task SEC_111_05_UnrecognisedVisibility_IsInvalid_AndNeverFull(string raw)
    {
        var result = await Create().ApplyAsync(Admin(), "sales.dbo.orders", Vis(raw), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.InvalidVisibility);
        _state.Visibility.ShouldBe(DatasetVisibility.Hidden);
    }

    [Fact]
    public async Task InvalidTag_RejectsTheWholeRequest_VisibilityIncluded()
    {
        var result = await Create().ApplyAsync(Admin(), "sales.dbo.orders", new DatasetCatalogEdit(["ok", "no spaces"], null, null, "Summary"), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.InvalidTag);
        _state.Visibility.ShouldBe(DatasetVisibility.Hidden);
        await _tables.DidNotReceiveWithAnyArgs().ReplaceDatasetTagsAsync(default, default!, default, default, default);
    }

    [Fact]
    public async Task TagCap_CountsTagsOfAllSources_FromTheStrictBeforeState()
    {
        _state = new DatasetCatalogSettings([new DatasetTag("d1", DatasetTagSource.Dbt), new DatasetTag("d2", DatasetTagSource.Dbt)], DatasetVisibility.Hidden);
        var svc = Create(maxTags: 3);

        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", Replace("a", "b", "c"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.TagLimitExceeded);
        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", Replace("a"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", new DatasetCatalogEdit(null, ["x", "y"], null, null), "admin", default))
            .Outcome.ShouldBe(DatasetCatalogOutcome.TagLimitExceeded);
        // A tag that exists as dbt tag and is replaced as admin tag does not count twice.
        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", Replace("d1", "a"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_09_StrictReadFails_IsUnavailable_NothingWritten_NoAudit()
    {
        _tables.GetDatasetCatalogSettingsAsync(Orders, true, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        var result = await Create().ApplyAsync(Admin(), "sales.dbo.orders", Vis("Summary"), "admin", default);

        result.Outcome.ShouldBe(DatasetCatalogOutcome.Unavailable);
        await _tables.DidNotReceiveWithAnyArgs().SetDatasetVisibilityAsync(default, default, default, default);
        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Theory]
    [InlineData("Multi", new string[0], false)]
    [InlineData("Multi", new[] { "sales.dbo.orders" }, true)]
    [InlineData("Single", new string[0], true)]
    public async Task TierEffective_IsFalseForNonSharedTablesInMulti(string mode, string[] shared, bool expected)
    {
        var result = await Create(mode, shared: shared).ApplyAsync(Admin(), "sales.dbo.orders", Vis("Summary"), "admin", default);

        result.TierEffective.ShouldBe(expected);
    }

    [Fact]
    public async Task Get_Admin_ReturnsSettings_Stranger_NotFound_StoreFailure_Unavailable()
    {
        _state = new DatasetCatalogSettings([new DatasetTag("pii", DatasetTagSource.Admin)], DatasetVisibility.Summary);
        var svc = Create();

        var ok = await svc.GetAsync(Admin(), "sales.dbo.orders", default);
        ok.Outcome.ShouldBe(DatasetCatalogOutcome.Unchanged);
        ok.Settings!.Tags.Single().Tag.ShouldBe("pii");
        (await svc.GetAsync(Stranger(), "sales.dbo.orders", default)).Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);

        _tables.GetDatasetCatalogSettingsAsync(Orders, true, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));
        (await svc.GetAsync(Admin(), "sales.dbo.orders", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Unavailable);
    }

    [Fact]
    public async Task GuidDatasetId_IsResolvedThroughTheRepository()
    {
        var id = Guid.NewGuid();
        _tables.GetActiveTableIdentifierByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Orders);

        (await Create().ApplyAsync(Admin(), id.ToString("D"), Vis("Summary"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_11_DryRun_WritesNothing_AuditsNothing_ButReportsTheSameOutcomeClasses()
    {
        OwnerIs("S-1-5-21-owner");
        var svc = Create(maxTags: 2);

        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", new DatasetCatalogEdit(["a"], null, null, "Summary"), "manifest", default, dryRun: true))
            .Outcome.ShouldBe(DatasetCatalogOutcome.Applied);
        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", Vis("Hidden"), "manifest", default, dryRun: true)).Outcome.ShouldBe(DatasetCatalogOutcome.Unchanged);
        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", Replace("a", "b", "c"), "manifest", default, dryRun: true)).Outcome.ShouldBe(DatasetCatalogOutcome.TagLimitExceeded);
        (await svc.ApplyAsync(Owner(), "sales.dbo.orders", Vis("Full"), "manifest", default, dryRun: true)).Outcome.ShouldBe(DatasetCatalogOutcome.Forbidden);
        (await svc.ApplyAsync(Stranger(), "sales.dbo.orders", Vis("Summary"), "manifest", default, dryRun: true)).Outcome.ShouldBe(DatasetCatalogOutcome.NotFound);
        (await svc.ApplyAsync(Admin(), "sales.dbo.orders", Vis("7"), "manifest", default, dryRun: true)).Outcome.ShouldBe(DatasetCatalogOutcome.InvalidVisibility);

        _state.Visibility.ShouldBe(DatasetVisibility.Hidden);
        _state.Tags.ShouldBeEmpty();
        await _tables.DidNotReceiveWithAnyArgs().ReplaceDatasetTagsAsync(default, default!, default, default, default);
        await _tables.DidNotReceiveWithAnyArgs().ChangeDatasetTagsAsync(default, default!, default!, default, default);
        await _tables.DidNotReceiveWithAnyArgs().SetDatasetVisibilityAsync(default, default, default, default);
        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_ProbeTracking_IsBounded_AndSuppressedCountsAreAggregatedNotLost()
    {
        var svc = Create();
        // One principal exhausts its budget (5 suppressed) and then never returns.
        for (var i = 0; i < 25; i++)
        {
            await svc.ApplyAsync(Stranger("S-1-5-21-gone"), "sales.dbo.orders", Vis("Summary"), "admin", default);
        }

        // More distinct principals than the hard cap in the same window.
        for (var i = 0; i < DatasetCatalogAdminService.MaxTrackedPrincipals + 99; i++)
        {
            await svc.ApplyAsync(Stranger("S-1-5-21-p" + i), "sales.dbo.orders", Vis("Summary"), "admin", default);
        }

        svc.TrackedProbePrincipalsForTesting.ShouldBeLessThanOrEqualTo(DatasetCatalogAdminService.MaxTrackedPrincipals);
        _audit.ClearReceivedCalls();

        // The next window sweeps once: the departed principal's suppressed count and the overflow bucket are flushed.
        _time.Now += TimeSpan.FromMinutes(6);
        await svc.ApplyAsync(Stranger("S-1-5-21-late"), "sales.dbo.orders", Vis("Summary"), "admin", default);

        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogProbeSuppressed && e.ActorSid.Value == "S-1-5-21-gone" && e.DetailsJson.Contains("\"count\":5")), Arg.Any<CancellationToken>());
        // 100 principals (one slot is taken by the departed one) fell into the shared overflow bucket (budget 20): 80 suppressed, aggregated in one entry.
        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.CatalogProbeSuppressed && e.DetailsJson.Contains("\"overflow\":true") && e.DetailsJson.Contains("\"count\":80")), Arg.Any<CancellationToken>());
        svc.TrackedProbePrincipalsForTesting.ShouldBeLessThanOrEqualTo(2);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_TagChangeAudit_SurvivesAFailingVisibilityWrite()
    {
        _tables.SetDatasetVisibilityAsync(Orders, Arg.Any<DatasetVisibility>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Create().ApplyAsync(Admin(), "sales.dbo.orders", new DatasetCatalogEdit(["pii"], null, null, "Summary"), "admin", default));

        _state.Tags.Single().Tag.ShouldBe("pii");
        await _audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.CatalogTagsChanged), CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_08_GuidPath_StoreFault_IsUnavailable_AndOwnershipStoreDown_Is503Too()
    {
        var id = Guid.NewGuid().ToString("D");
        _tables.GetActiveTableIdentifierByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db"));

        (await Create().ApplyAsync(Admin(), id, Vis("Summary"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Unavailable);
        (await Create().ApplyAsync(Stranger(), id, Vis("Summary"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Unavailable);

        _tables.GetActiveTableIdentifierByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TableIdentifier?)null);
        _owners.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db"));
        (await Create().ApplyAsync(Stranger(), id, Vis("Summary"), "admin", default)).Outcome.ShouldBe(DatasetCatalogOutcome.Unavailable);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_111_12_ManifestProbes_AreAuditedAsOneAggregatedEntryPerManifest_ForNonAdminsOnly()
    {
        var svc = Create();

        await svc.RecordManifestProbesAsync(Stranger(), 3, default);
        await svc.RecordManifestProbesAsync(Admin(), 3, default);
        await svc.RecordManifestProbesAsync(Stranger(), 0, default);

        await _audit.Received(1).RecordBestEffortAsync(Arg.Is<AuditLogEntry>(e =>
            e.Decision == "DENY" && e.DetailsJson.Contains("\"attempts\":3") && e.DetailsJson.Contains("\"source\":\"manifest\"")), Arg.Any<CancellationToken>());
        await _audit.ReceivedWithAnyArgs(1).RecordBestEffortAsync(default!, default);
    }
}
