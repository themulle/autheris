using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Identity.Attributes;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>PLAN-GOV-129 WP-5 (A-8, F-5): the in-memory consumers of a decision evaluate a typed attribute policy first; text only without one; whatever cannot be evaluated denies the row.</summary>
[Trait("Category", "Security")]
public sealed class InMemoryRowPassesDecisionTests : IDisposable
{
    private static readonly TenantId Tenant = new("acme");
    private static readonly TableIdentifier Orders = new("d", "main", "orders");
    private const string OidA = "11111111-1111-1111-1111-111111111111";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private static TableMetadata Meta() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders", SourceType = "SQLite", DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "cost_center", DataType = "TEXT" }, new TableColumn { ColumnName = "id", DataType = "INTEGER" }]
    };

    private sealed class FixedProvider(string[] values) : ICallerAttributeProvider
    {
        public string Kind => "claims";

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct) =>
            ValueTask.FromResult(values.Length == 0 ? CallerAttributeResult.Missing() : CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String)));
    }

    private async Task<TableAccessDecision> Decide(string[] attributeValues, ConsentEffect effect = ConsentEffect.Allow)
    {
        var def = CallerAttributeTestSupport.Definition("cost_center", "claims", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60) with { Tenant = Tenant };
        var registry = Substitute.For<ICallerAttributeRegistry>();
        registry.GetAsync(Arg.Any<CancellationToken>()).Returns(new CallerAttributeSnapshot(1, [def], []));
        var resolver = new CallerAttributeResolver(registry, [new FixedProvider(attributeValues)], Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = CallerAttributeTestSupport.Options() }));
        _disposables.Add(resolver);
        var consent = new Consent
        {
            TableIdentifier = Orders, TenantId = Tenant, GranteeType = GranteeType.Role, RoleName = "analyst", Effect = effect,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            RowFilters = [new ConsentRowFilter { ColumnName = "cost_center", Operator = "IN", ValueJson = "[]", ValueSource = "USER_ATTRIBUTE", UserAttribute = "cost_center" }]
        };
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(effect == ConsentEffect.Allow ? [consent] : [new Consent { TableIdentifier = Orders, TenantId = Tenant, GranteeType = GranteeType.Role, RoleName = "analyst", Effect = ConsentEffect.Allow, ValidFrom = DateTimeOffset.UtcNow.AddDays(-1), ValidTo = DateTimeOffset.UtcNow.AddDays(1) }, consent]));
        var policy = new TableAccessPolicy(repo, new ConsentResolutionService(), Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullPolicyEnforcementService.Instance, Autheris.Application.Security.Rebac.Services.NullRebacEvaluator.Instance,
            clientIpResolver: null, new GatewayOptions(), NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer()), callerAttributes: resolver);
        var principal = CallerAttributeTestSupport.Principal(CallerAttributeTestSupport.Bearer, [("oid", OidA)]);
        return await policy.DecideAsync(new TableAccessQuery(new Sid(OidA), Tenant, new HashSet<Sid>(), new HashSet<string> { "analyst" }, Meta(), Principal: principal), CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, object?> Row(string costCenter) => new Dictionary<string, object?> { ["id"] = 1, ["cost_center"] = costCenter };

    [Fact]
    public async Task TypedAttributePolicy_IsEvaluatedPerRow()
    {
        var decision = await Decide(["CC100", "CC300"]);
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC100"), decision, Meta()).ShouldBeTrue();
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC300"), decision, Meta()).ShouldBeTrue();
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC200"), decision, Meta()).ShouldBeFalse();
    }

    [Fact]
    public async Task MissingAttribute_PassesNoRow_ForAllow_AndBlocksAllRows_ForDeny()
    {
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC100"), await Decide([]), Meta()).ShouldBeFalse();
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC100"), await Decide([], ConsentEffect.Deny), Meta()).ShouldBeFalse();
    }

    [Fact]
    public async Task Deny_WithAnAttribute_ExcludesTheirRowsOnly()
    {
        var decision = await Decide(["CC100"], ConsentEffect.Deny);
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC100"), decision, Meta()).ShouldBeFalse();
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC200"), decision, Meta()).ShouldBeTrue();
    }

    [Fact]
    public async Task ADriftedTextProjection_DeniesTheRow()
    {
        var decision = await Decide(["CC100"]);
        var drifted = decision with { CombinedRowFilterSql = "1 = 1" };
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC100"), drifted, Meta()).ShouldBeFalse();
    }

    [Fact]
    public void ADecisionWithoutATypedPolicy_UsesTheTextPath()
    {
        var text = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), "cost_center = 'CC100'", hasUnconstrainedColumnAllow: true);
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC100"), text, Meta()).ShouldBeTrue();
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC200"), text, Meta()).ShouldBeFalse();
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC200"), TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true), Meta()).ShouldBeTrue();
    }

    [Fact]
    public void ATextFilterWithMarkers_DeniesTheRow_NeverMatches()
    {
        var text = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), "cost_center = @__gql_caller_cost_center__0", hasUnconstrainedColumnAllow: true);
        InMemoryRowFilterEvaluator.RowPassesDecision(Row("CC100"), text, Meta()).ShouldBeFalse();
    }
}
