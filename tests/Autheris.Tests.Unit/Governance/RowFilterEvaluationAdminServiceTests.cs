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
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #107 (SEC-RF107-14, SEC-RF107-06e): validation, tenant isolation, audit and epoch hand-off of the evaluation admin service.</summary>
public sealed class RowFilterEvaluationAdminServiceTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid Actor = new("S-1-5-21-admin");
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly Guid RuleId = Guid.NewGuid();

    private readonly IConsentApprovalRepository _consents = Substitute.For<IConsentApprovalRepository>();
    private readonly ITableMetadataRepository _tables = Substitute.For<ITableMetadataRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    private RowFilterEvaluationAdminService Create(RowFilterKeySetCacheOptions? caps = null)
        => new(_consents, _tables, _audit, new StaticOptionsMonitor<GatewayOptions>(
            new GatewayOptions { RowFilters = new RowFilterOptions { KeySetCache = caps ?? new RowFilterKeySetCacheOptions() } }));

    private static ConsentRowFilter Rule(RowFilterType type = RowFilterType.SubqueryCorrelated, string? temporal = null,
        RowFilterEvaluationSettings? stored = null) => new()
    {
        Id = RuleId,
        FilterType = type,
        TargetTemporalColumn = temporal,
        KeySetStrategy = stored?.Strategy,
        KeySetDurationSeconds = stored?.DurationSeconds,
        KeySetMaxKeys = stored?.MaxKeys
    };

    private void GivenRule(ConsentRowFilter filter, ConsentEffect effect = ConsentEffect.Allow, TenantId? tenant = null)
        => _consents.GetRowFilterAsync(RuleId, Arg.Any<CancellationToken>())
            .Returns((filter, effect, tenant ?? TenantA, Orders));

    private static RowFilterEvaluationChange Change(RowFilterEvaluationSettings before, RowFilterEvaluationSettings after, Guid? rule = null, TenantId? tenant = null)
        => new(Orders, tenant, rule, before, after, 7);

    private static readonly RowFilterEvaluationSettings KeySet = new(RowFilterEvaluationStrategy.KeySetCache, 120, 500);

    // ---- rule level ----

    [Trait("Category", "Security")]
    [Fact]
    public async Task WritesHmacChainedAuditWithBeforeAfter_SEC_RF107_14d()
    {
        var before = new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.Exists, 30, null);
        GivenRule(Rule(stored: before));
        _consents.UpdateRowFilterEvaluationAsync(RuleId, TenantA, KeySet, Arg.Any<CancellationToken>()).Returns(Change(before, KeySet, RuleId, TenantA));

        var change = await Create().SetRuleAsync(RuleId, TenantA, KeySet, Actor, CancellationToken.None);

        change.ShouldNotBeNull();
        // The HMAC-chained writer in its required (fail-closed) mode, with the dedicated event type and not ConsentApplied.
        await _audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.RowFilterEvaluationChanged
            && e.EventType != AuditEventTypes.ConsentApplied
            && e.ActorSid == Actor
            && e.TenantId == TenantA
            && e.TargetTable == Orders.ToString()
            && e.DetailsJson.Contains(RuleId.ToString())
            && e.DetailsJson.Contains("\"before\":{\"strategy\":\"Exists\",\"durationSeconds\":30,\"maxKeys\":null}")
            && e.DetailsJson.Contains("\"after\":{\"strategy\":\"KeySetCache\",\"durationSeconds\":120,\"maxKeys\":500}")), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().RecordBestEffortAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetRule_PassesTheSettingsToTheRepository_WhichOwnsTheEpochBump()
    {
        GivenRule(Rule());
        _consents.UpdateRowFilterEvaluationAsync(RuleId, TenantA, KeySet, Arg.Any<CancellationToken>())
            .Returns(Change(RowFilterEvaluationSettings.Inherit, KeySet, RuleId, TenantA));

        var change = await Create().SetRuleAsync(RuleId, TenantA, KeySet, Actor, CancellationToken.None);

        change!.NewEpoch.ShouldBe(7);
        await _consents.Received(1).UpdateRowFilterEvaluationAsync(RuleId, TenantA, KeySet, Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetRule_OtherTenant_ReturnsNull_WithoutWriteOrAudit_SEC_RF107_14a()
    {
        GivenRule(Rule(), tenant: TenantB);

        (await Create().SetRuleAsync(RuleId, TenantA, KeySet, Actor, CancellationToken.None)).ShouldBeNull();

        await _consents.DidNotReceive().UpdateRowFilterEvaluationAsync(Arg.Any<Guid>(), Arg.Any<TenantId>(), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Fact]
    public async Task SetRule_UnknownRule_ReturnsNull()
    {
        _consents.GetRowFilterAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ValueTuple<ConsentRowFilter, ConsentEffect, TenantId, TableIdentifier>?)null);

        (await Create().SetRuleAsync(Guid.NewGuid(), TenantA, KeySet, Actor, CancellationToken.None)).ShouldBeNull();
        (await Create().GetRuleAsync(Guid.NewGuid(), TenantA, CancellationToken.None)).ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetRule_DenyRule_IsRejected_SEC_RF107_06e()
    {
        GivenRule(Rule(), ConsentEffect.Deny);

        await Should.ThrowAsync<RowFilterEvaluationValidationException>(() => Create().SetRuleAsync(RuleId, TenantA, KeySet, Actor, CancellationToken.None));

        await _consents.DidNotReceive().UpdateRowFilterEvaluationAsync(Arg.Any<Guid>(), Arg.Any<TenantId>(), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>());
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetRule_NonCorrelatedRule_IsRejected_SEC_RF107_06e()
    {
        GivenRule(Rule(type: RowFilterType.SimpleColumnPredicate));

        await Should.ThrowAsync<RowFilterEvaluationValidationException>(() => Create().SetRuleAsync(RuleId, TenantA, KeySet, Actor, CancellationToken.None));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SetRule_TemporalRule_IsRejected_SEC_RF107_06e()
    {
        GivenRule(Rule(temporal: "order_date"));

        await Should.ThrowAsync<RowFilterEvaluationValidationException>(() => Create().SetRuleAsync(RuleId, TenantA, KeySet, Actor, CancellationToken.None));
    }

    [Fact]
    public async Task SetRule_ClearingOnADenyRule_IsAllowed()
    {
        GivenRule(Rule(stored: KeySet), ConsentEffect.Deny);
        _consents.UpdateRowFilterEvaluationAsync(RuleId, TenantA, RowFilterEvaluationSettings.Inherit, Arg.Any<CancellationToken>())
            .Returns(Change(KeySet, RowFilterEvaluationSettings.Inherit, RuleId, TenantA));

        (await Create().SetRuleAsync(RuleId, TenantA, RowFilterEvaluationSettings.Inherit, Actor, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task SetRule_AboveCaps_IsRejected_AndLoweredCapsApplyImmediately()
    {
        GivenRule(Rule());

        var lowered = Create(new RowFilterKeySetCacheOptions { MaxKeys = 100 });
        await Should.ThrowAsync<RowFilterEvaluationValidationException>(() => lowered.SetRuleAsync(RuleId, TenantA, KeySet, Actor, CancellationToken.None));
    }

    [Fact]
    public async Task GetRule_ReturnsStoredAndEffective_WithObjectLevelInFallbackOrder()
    {
        GivenRule(Rule(stored: new RowFilterEvaluationSettings(null, 30, null)));
        _tables.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Identifier = Orders,
            Table = new Table(),
            RowFilterEvaluation = new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 600, 50)
        });

        var view = await Create().GetRuleAsync(RuleId, TenantA, CancellationToken.None);

        view!.Stored.ShouldBe(new RowFilterEvaluationSettings(null, 30, null));
        view.Effective.Strategy.ShouldBe(RowFilterEvaluationStrategy.KeySetCache);
        view.Effective.StrategySource.ShouldBe(RowFilterEvaluationSource.Object);
        view.Effective.Duration.ShouldBe(TimeSpan.FromSeconds(30));
        view.Effective.MaxKeys.ShouldBe(50);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task GetRule_OtherTenant_ReturnsNull_SEC_RF107_14a()
    {
        GivenRule(Rule(), tenant: TenantB);

        (await Create().GetRuleAsync(RuleId, TenantA, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ValidateRule_ReturnsTheCurrentStoredSettings_WithoutWriting()
    {
        GivenRule(Rule(stored: KeySet));

        var preview = await Create().ValidateRuleAsync(RuleId, TenantA, RowFilterEvaluationSettings.Inherit, CancellationToken.None);

        preview!.Before.ShouldBe(KeySet);
        preview.Table.ShouldBe(Orders);
        await _consents.DidNotReceive().UpdateRowFilterEvaluationAsync(Arg.Any<Guid>(), Arg.Any<TenantId>(), Arg.Any<RowFilterEvaluationSettings>(), Arg.Any<CancellationToken>());
    }

    // ---- object level ----

    [Fact]
    public async Task SetObject_WritesAudit_WithTheObjectEventType()
    {
        _tables.SetRowFilterEvaluationAsync(Orders, KeySet, Actor, Arg.Any<CancellationToken>())
            .Returns(Change(RowFilterEvaluationSettings.Inherit, KeySet));

        var change = await Create().SetObjectAsync(Orders, KeySet, Actor, TenantA, CancellationToken.None);

        change.ShouldNotBeNull();
        await _audit.Received(1).RecordRequiredAsync(Arg.Is<AuditLogEntry>(e =>
            e.EventType == AuditEventTypes.TableRowFilterStrategyChanged
            && e.TenantId == TenantA
            && e.TargetTable == Orders.ToString()
            && e.DetailsJson.Contains("\"level\":\"object\"")
            && e.DetailsJson.Contains("\"ruleId\":null")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetObject_UnknownTable_ReturnsNull_WithoutAudit()
    {
        _tables.SetRowFilterEvaluationAsync(Orders, KeySet, Actor, Arg.Any<CancellationToken>()).Returns((RowFilterEvaluationChange?)null);

        (await Create().SetObjectAsync(Orders, KeySet, Actor, TenantA, CancellationToken.None)).ShouldBeNull();

        await _audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Fact]
    public async Task SetObject_NullClears_AndIsAudited()
    {
        _tables.SetRowFilterEvaluationAsync(Orders, null, Actor, Arg.Any<CancellationToken>()).Returns(Change(KeySet, RowFilterEvaluationSettings.Inherit));

        (await Create().SetObjectAsync(Orders, null, Actor, TenantA, CancellationToken.None)).ShouldNotBeNull();
        await _audit.Received(1).RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetObject_AboveCaps_IsRejected_BeforeAnyWrite()
    {
        await Should.ThrowAsync<RowFilterEvaluationValidationException>(() => Create().SetObjectAsync(
            Orders, new RowFilterEvaluationSettings(null, 999_999, null), Actor, TenantA, CancellationToken.None));

        await _tables.DidNotReceiveWithAnyArgs().SetRowFilterEvaluationAsync(default, default, default, default);
    }

    [Fact]
    public async Task SetObject_AuditUnavailable_Propagates_FailClosed()
    {
        _tables.SetRowFilterEvaluationAsync(Orders, KeySet, Actor, Arg.Any<CancellationToken>()).Returns(Change(RowFilterEvaluationSettings.Inherit, KeySet));
        _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new AuditUnavailableException("down")));

        await Should.ThrowAsync<AuditUnavailableException>(() => Create().SetObjectAsync(Orders, KeySet, Actor, TenantA, CancellationToken.None));
    }

    [Fact]
    public async Task GetObject_UnknownTable_ReturnsNull_AndKnownReturnsEffective()
    {
        _tables.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns((TableMetadata?)null);
        (await Create().GetObjectAsync(Orders, CancellationToken.None)).ShouldBeNull();

        _tables.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(new TableMetadata { Identifier = Orders, Table = new Table() });
        var view = await Create().GetObjectAsync(Orders, CancellationToken.None);

        view!.Stored.ShouldBe(RowFilterEvaluationSettings.Inherit);
        view.Effective.StrategySource.ShouldBe(RowFilterEvaluationSource.Global);
    }

    [Fact]
    public void AuditEventTypes_AreFailClosed()
    {
        AuditPolicyTable.All[AuditEventTypes.RowFilterEvaluationChanged].ShouldBe(AuditFailurePolicy.FailClosed);
        AuditPolicyTable.All[AuditEventTypes.TableRowFilterStrategyChanged].ShouldBe(AuditFailurePolicy.FailClosed);
    }
}
