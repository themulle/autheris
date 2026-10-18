namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Identity.Attributes;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

/// <summary>
/// REQ-GOV-129 mandatory security tests that cut across the units (PLAN-GOV-129 section P3.6): ST-129-10 (one generic outcome), ST-129-18 (Casbin,
/// shared decisions), ST-129-19 (#107 interplay), ST-129-22 (value hygiene).
/// </summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeSecurityTests : IDisposable
{
    private static readonly TenantId Tenant = new("acme");
    private static readonly TableIdentifier Orders = new("d", "main", "orders");
    private const string Oid = "11111111-1111-1111-1111-111111111111";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private sealed class ScriptedProvider(Func<CallerAttributeResult> behavior) : ICallerAttributeProvider
    {
        public string Kind => "claims";

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct) => ValueTask.FromResult(behavior());
    }

    private static CallerAttributeResult Raw(params string[] values) =>
        CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String));

    private static TableMetadata Meta() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders", SourceType = "SQLite", DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "cost_center", DataType = "TEXT" }, new TableColumn { ColumnName = "id", DataType = "INTEGER" }]
    };

    private async Task<TableAccessDecision> Decide(Func<CallerAttributeResult> behavior, bool registered = true, bool withPrincipal = true, int max = 50, CallerAttributeCardinality cardinality = CallerAttributeCardinality.List)
    {
        var definitions = registered
            ? [CallerAttributeTestSupport.Definition("cost_center", "claims", CallerAttributeType.Text, cardinality, cardinality == CallerAttributeCardinality.Scalar ? 1 : max, 60) with { Tenant = Tenant }]
            : Array.Empty<CallerAttributeDefinition>();
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, definitions, []));
        var resolver = new CallerAttributeResolver(registry, [new ScriptedProvider(behavior)],
            Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = CallerAttributeTestSupport.Options() }));
        _disposables.Add(resolver);

        var consent = new Consent
        {
            TableIdentifier = Orders, TenantId = Tenant, GranteeType = GranteeType.Role, RoleName = "analyst", Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            RowFilters = [new ConsentRowFilter { ColumnName = "cost_center", Operator = "IN", ValueJson = "[]", ValueSource = "USER_ATTRIBUTE", UserAttribute = "cost_center" }]
        };
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([consent]));
        var policy = new TableAccessPolicy(repo, new ConsentResolutionService(), Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance, Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null, new GatewayOptions(), NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer()), callerAttributes: resolver);
        var principal = withPrincipal ? CallerAttributeTestSupport.Principal(CallerAttributeTestSupport.Bearer, [("oid", Oid)]) : null;
        return await policy.DecideAsync(new TableAccessQuery(new Sid(Oid), Tenant, new HashSet<Sid>(), new HashSet<string> { "analyst" }, Meta(), Principal: principal), CancellationToken.None);
    }

    // ---- ST-129-10 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ST_129_10_EveryUnresolvedCause_ProducesTheSameDecisionForTheCaller()
    {
        var causes = new Dictionary<string, Task<TableAccessDecision>>
        {
            ["missing"] = Decide(() => CallerAttributeResult.Missing()),
            ["failed-timeout-like"] = Decide(() => throw new TimeoutException("db secret")),
            ["failed-source"] = Decide(() => CallerAttributeResult.Failed("SourceUnavailable")),
            ["over-cardinality"] = Decide(() => Raw(Enumerable.Range(0, 60).Select(i => $"v{i}").ToArray())),
            ["unknown-attribute"] = Decide(() => Raw("x"), registered: false),
            ["no-subject"] = Decide(() => Raw("x"), withPrincipal: false),
            ["ambiguous"] = Decide(() => Raw("a", "b"), cardinality: CallerAttributeCardinality.Scalar),
            ["not-normalizable"] = Decide(() => Raw("x"))   // text passes; control: a resolved value must differ below
        };
        var results = new Dictionary<string, TableAccessDecision>();
        foreach (var (name, task) in causes)
        {
            results[name] = await task;
        }

        var reference = results["missing"];
        foreach (var name in new[] { "failed-timeout-like", "failed-source", "over-cardinality", "unknown-attribute", "no-subject", "ambiguous" })
        {
            var d = results[name];
            d.IsAllowed.ShouldBe(reference.IsAllowed, name);
            d.CombinedRowFilterSql.ShouldBe(reference.CombinedRowFilterSql, name);
            (d.RowFilterParameters ?? new Dictionary<string, object?>()).ShouldBeEmpty(name);
            d.DeniedReasons.ShouldBe(reference.DeniedReasons, name);
            d.ColumnAccess.Keys.Order().ShouldBe(reference.ColumnAccess.Keys.Order(), name);
        }

        results["not-normalizable"].RowFilterParameters.ShouldNotBeNull();   // the control resolved: the comparison above is not vacuous
    }

    [Fact]
    public async Task ST_129_10_TheCallerVisibleDecision_NeverNamesTheCause_ButTheAuditDoes()
    {
        var failed = await Decide(() => throw new InvalidOperationException("password=hunter2"));
        failed.DeniedReasons.ShouldBeEmpty();
        string.Join(' ', failed.DeniedReasons).ShouldNotContain("hunter2");
        failed.CallerAttributes!.Single().Outcome.ShouldBe("Failed");
        failed.CallerAttributes!.Single().Reason.ShouldBe("SourceUnavailable");
        failed.CallerAttributes!.Single().ToString().ShouldNotContain("hunter2");
    }

    // ---- ST-129-18 -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("region = {{caller.dept}}")]
    [InlineData("region = '{{caller.dept}}'")]
    [InlineData("region = '__autheris_caller__dept'")]
    public void ST_129_18_ACasbinRule_WithAPlaceholder_IsRefused_OnEveryEntryPoint(string rlsFilter)
    {
        var service = new CasbinEnforcementService();
        var tenant = new TenantId("tenant-corp");
        Should.Throw<ArgumentException>(() => service.AddPolicy(tenant, "role:analyst", "sales.public.orders", "read", "true", "allow", rlsFilter));
        Should.Throw<ArgumentException>(() => service.AddPolicy(tenant, "role:analyst", "sales.public.orders", "read", rlsFilter, "allow"));
        Should.Throw<ArgumentException>(() => service.AddPolicies(tenant, [new CasbinRuleInput("role:analyst", "sales.public.orders", "read", "true", "allow", rlsFilter)]));
    }

    [Fact]
    public void ST_129_18_ARuleThatMentionsAPlaceholder_IsNeverShareableBetweenUsers()
    {
        PolicyShapeAnalyzer.Analyze("true", "region = {{caller.dept}}").Shareable.ShouldBeFalse();
        PolicyShapeAnalyzer.Analyze("true", "region = 'EU'").Shareable.ShouldBeTrue();   // the control: a plain rule is still shareable
    }

    // ---- ST-129-19 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ST_129_19_ACallerSlot_AndABoundAttribute_AreNeverRegisteredAsACorrelatedKeySetSite()
    {
        var filter = new VirtualFilter { TenantId = Tenant, Name = "f", Source = "lwetem_prod", Sql = "from conf.client client where target.client_id = client.client_id and client.dept = {{caller.dept}}" };
        var typed = SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.Sqlite, new FastSqlEngine());
        typed.Expression.ShouldBeOfType<ExistsExpression>();
        CorrelatedFilterSiteRegistry.TryGet((ExistsExpression)typed.Expression, out _).ShouldBeFalse();

        var bound = CallerAttributeBinder.Bind(typed, new CallerAttributeSet(new Dictionary<string, CallerAttributeResult> { ["dept"] = Raw("D1") }), CallerAttributePadding.Default);
        bound.Expression.ShouldBeOfType<ExistsExpression>();
        CorrelatedFilterSiteRegistry.TryGet((ExistsExpression)bound.Expression, out _).ShouldBeFalse();

        var consent = new TypedRowFilterBuilder().BuildCombined(
            [new Consent { Effect = ConsentEffect.Allow, RowFilters = [new ConsentRowFilter { ColumnName = "c", Operator = "IN", ValueSource = "USER_ATTRIBUTE", UserAttribute = "dept" }] }], [],
            new TableIdentity("main", "orders"), null, new CallerAttributeSet(new Dictionary<string, CallerAttributeResult> { ["dept"] = Raw("D1") }));
        consent!.Expression.ShouldNotBeOfType<ExistsExpression>();
    }

    // ---- ST-129-22 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ST_129_22_MetricsCarryTheProviderAndTheOutcome_Only()
    {
        var recorded = new List<(string Instrument, string[] TagKeys, string?[] TagValues)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name.StartsWith("autheris.callerattr.", StringComparison.Ordinal)) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((i, _, tags, _) => { lock (recorded) recorded.Add((i.Name, tags.ToArray().Select(t => t.Key).ToArray(), tags.ToArray().Select(t => t.Value?.ToString()).ToArray())); });
        listener.SetMeasurementEventCallback<double>((i, _, tags, _) => { lock (recorded) recorded.Add((i.Name, tags.ToArray().Select(t => t.Key).ToArray(), tags.ToArray().Select(t => t.Value?.ToString()).ToArray())); });
        listener.Start();

        await Decide(() => Raw("top-secret-value-4711"));
        await Decide(() => throw new InvalidOperationException("boom"));

        lock (recorded)
        {
            recorded.ShouldNotBeEmpty();
            recorded.SelectMany(r => r.TagKeys).Distinct().Order().ShouldBeSubsetOf(["outcome", "provider"]);
            recorded.SelectMany(r => r.TagValues).ShouldAllBe(v => v == null || (!v.Contains("top-secret") && !v.Contains("cost_center") && !v.Contains(Oid)));
            recorded.Select(r => r.Instrument).ShouldContain("autheris.callerattr.resolutions");
        }
    }

    [Fact]
    public async Task ST_129_22_ExceptionsFromTheResolver_CarryNoAttributeData()
    {
        var decision = await Decide(() => throw new InvalidOperationException("sensitive"));
        decision.CallerAttributes!.Single().Reason.ShouldBe("SourceUnavailable");
        // The provider exception never leaves the resolver: no exception, no Data, only a stable reason code.
        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void ST_129_22_HostileValues_NeverReachTheRenderedText_OrTheToStringOfAnyType()
    {
        var hostile = new[] { "x'; DROP TABLE t;--", "/* c */", "{{caller.x}}", new string('A', 10_240) };
        var value = new CallerAttributeValue(hostile.Cast<object>().ToList(), SqlParameterType.String);
        var set = new CallerAttributeSet(new Dictionary<string, CallerAttributeResult> { ["cost_center"] = CallerAttributeResult.Resolved(value) });

        var predicate = new TypedRowFilterBuilder().BuildCombined(
            [new Consent { Effect = ConsentEffect.Allow, RowFilters = [new ConsentRowFilter { ColumnName = "c", Operator = "IN", ValueSource = "USER_ATTRIBUTE", UserAttribute = "cost_center" }] }], [],
            new TableIdentity("main", "orders"), null, set)!;
        var rendered = new PolicyPredicateRenderer().Render(predicate, TargetSqlDialect.Sqlite, RowFilterAliases.Target);

        foreach (var text in new[] { rendered.Sql, value.ToString()!, set.ToString(), predicate.Parameters.First().Value.ToString() })
        {
            text.ShouldNotContain("DROP TABLE");
            text.ShouldNotContain("/* c */");
            text.ShouldNotContain("AAAAAAAAAA");
        }

        rendered.Parameters.Values.ShouldContain(hostile[0]);   // bound, never inlined
    }
}
