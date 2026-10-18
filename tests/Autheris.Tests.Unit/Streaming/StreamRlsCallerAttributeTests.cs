#pragma warning disable CA2012

namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Identity.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>PLAN-GOV-129 test 28, ST-129-17 (A-8, BC-129-14): CDC delivery evaluates caller attribute consent filters typed, per event; every other state delivers nothing.</summary>
[Trait("Category", "Security")]
public sealed class StreamRlsCallerAttributeTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant_a");
    private static readonly TableIdentifier Orders = new("erp", "main", "orders");
    private const string OidA = "11111111-1111-1111-1111-111111111111";
    private const string OidB = "22222222-2222-2222-2222-222222222222";
    private const string OidC = "33333333-3333-3333-3333-333333333333";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _disposables = [];
    private readonly Dictionary<string, string[]> _values = new() { [OidA] = ["CC100"], [OidB] = ["CC200"] };

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private sealed class MapProvider(Dictionary<string, string[]> values) : ICallerAttributeProvider
    {
        public string Kind => "claims";

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
        {
            var oid = subject.KeyFor(SubjectKeyKind.EntraObjectId);
            return ValueTask.FromResult(oid is not null && values.TryGetValue(oid, out var v)
                ? CallerAttributeResult.Resolved(new CallerAttributeValue(v.Cast<object>().ToList(), SqlParameterType.String))
                : CallerAttributeResult.Missing());
        }
    }

    private static ConsentRowFilter Attr(string op = "EQ") => new()
    {
        ColumnName = "cost_center", Operator = op, ValueJson = "[]", ValueSource = "USER_ATTRIBUTE", UserAttribute = "cost_center"
    };

    private StreamRlsPolicyEnforcer Enforcer(ConsentRowFilterRecipe recipe, string legacyText, bool resolverEnabled = true, bool withResolver = true)
    {
        var def = CallerAttributeTestSupport.Definition("cost_center", "claims", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60) with { Tenant = Tenant };
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));
        var resolver = new CallerAttributeResolver(registry, [new MapProvider(_values)],
            Options.Create(new GatewayOptions { CallerAttributes = CallerAttributeTestSupport.Options(resolverEnabled) }), _time);
        _disposables.Add(resolver);

        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { SourceName = "erp", SchemaName = "main", TableName = "orders", SourceType = "SQLite", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "cost_center", DataType = "varchar" }]
        }));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));
        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["cost_center"] = ColumnAccessLevel.Clear }, rowFilterSql: legacyText, hasUnconstrainedColumnAllow: true)
                with { ConsentFilterRecipe = recipe });
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.HasPolicies(Arg.Any<TenantId>()).Returns(false);
        return new StreamRlsPolicyEnforcer(
            policy, metadataRepo, Substitute.For<IColumnMaskingProvider>(), Substitute.For<IEpochValidationService>(),
            NullLogger<StreamRlsPolicyEnforcer>.Instance, consents, resolution, Substitute.For<IConsentCacheService>(),
            callerAttributes: withResolver ? resolver : null, time: _time);
    }

    private static CdcEvent Event(string costCenter) => new(
        "evt", Orders, CdcOperation.Insert, Tenant.Value, null,
        new Dictionary<string, object?> { ["id"] = 1, ["cost_center"] = costCenter }, DateTimeOffset.UtcNow);

    private ClaimsPrincipal Subscriber(string oid, DateTimeOffset? expires = null, params (string, string)[] extra)
    {
        var claims = new List<(string, string)> { ("oid", oid), ("tenant_id", Tenant.Value) };
        claims.Add(("exp", (expires ?? _time.GetUtcNow().AddHours(1)).ToUnixTimeSeconds().ToString()));
        claims.AddRange(extra);
        return CallerAttributeTestSupport.Principal(CallerAttributeTestSupport.Bearer, claims);
    }

    private static ConsentRowFilterRecipe Allow(params ConsentRowFilter[] filters) => new([filters], []);

    private static ConsentRowFilterRecipe Deny(params ConsentRowFilter[] filters) => new([[]], [filters]);

    [Fact]
    public async Task ST_129_17_EachSubscriber_ReceivesOnlyTheEventsOfTheirOwnAttribute()
    {
        var enforcer = Enforcer(Allow(Attr()), "1 = 0");

        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), Subscriber(OidA))).IsAllowed.ShouldBeTrue();
        (await enforcer.EvaluateAndMaskAsync(Event("CC200"), Subscriber(OidA))).IsAllowed.ShouldBeFalse();
        (await enforcer.EvaluateAndMaskAsync(Event("CC200"), Subscriber(OidB))).IsAllowed.ShouldBeTrue();
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), Subscriber(OidB))).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ST_129_17_ASubscriberWithoutTheAttribute_ReceivesNothing()
    {
        var enforcer = Enforcer(Allow(Attr()), "1 = 0");
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), Subscriber(OidC))).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ST_129_17_ASubscriberPrincipalPastExp_ReceivesNothing()
    {
        var enforcer = Enforcer(Allow(Attr()), "1 = 0");
        var principal = Subscriber(OidA, expires: _time.GetUtcNow().AddMinutes(5));
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), principal)).IsAllowed.ShouldBeTrue();
        _time.Advance(TimeSpan.FromMinutes(6));
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), principal)).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ST_129_17_ARevokedAttribute_StopsDeliveryAfterTheTtl()
    {
        var enforcer = Enforcer(Allow(Attr()), "1 = 0");
        var principal = Subscriber(OidA);
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), principal)).IsAllowed.ShouldBeTrue();
        _values.Remove(OidA);
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), principal)).IsAllowed.ShouldBeTrue();   // inside the TTL (60 s)
        _time.Advance(TimeSpan.FromSeconds(61));
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), principal)).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ST_129_17_AnApplicationOnlySubscriber_WithoutAServiceSet_ReceivesNothing()
    {
        var enforcer = Enforcer(Allow(Attr()), "1 = 0");
        var app = CallerAttributeTestSupport.Principal(CallerAttributeTestSupport.Bearer,
            [("idtyp", "app"), ("appid", "aaaaaaaa-0000-0000-0000-000000000001"), ("tenant_id", Tenant.Value), ("oid", OidA)]);
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), app)).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Deny_Consent_WithAnAttribute_ExcludesTheirRows()
    {
        var enforcer = Enforcer(Deny(Attr()), "NOT (1 = 1)");
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), Subscriber(OidA))).IsAllowed.ShouldBeFalse();
        (await enforcer.EvaluateAndMaskAsync(Event("CC200"), Subscriber(OidA))).IsAllowed.ShouldBeTrue();
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), Subscriber(OidC))).IsAllowed.ShouldBeFalse();   // missing blocks (polarity)
    }

    [Fact]
    public async Task FeatureOff_AndNoResolver_KeepTheTextConstant_NothingIsDelivered()
    {
        (await Enforcer(Allow(Attr()), "1 = 0", resolverEnabled: false).EvaluateAndMaskAsync(Event("CC100"), Subscriber(OidA))).IsAllowed.ShouldBeFalse();
        (await Enforcer(Allow(Attr()), "1 = 0", withResolver: false).EvaluateAndMaskAsync(Event("CC100"), Subscriber(OidA))).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task ARecipeWithoutAttributes_UsesTheTextEvaluatorAsBefore()
    {
        var literal = new ConsentRowFilter { ColumnName = "cost_center", Operator = "EQ", ValueJson = "\"CC100\"" };
        var enforcer = Enforcer(Allow(literal), "[cost_center] = 'CC100'");
        (await enforcer.EvaluateAndMaskAsync(Event("CC100"), Subscriber(OidC))).IsAllowed.ShouldBeTrue();
        (await enforcer.EvaluateAndMaskAsync(Event("CC200"), Subscriber(OidC))).IsAllowed.ShouldBeFalse();
    }
}
