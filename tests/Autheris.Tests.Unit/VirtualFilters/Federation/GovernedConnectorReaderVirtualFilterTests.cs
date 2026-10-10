namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.VirtualFilterEnforcementFixture;

/// <summary>
/// F-GOV-14 WI-4 (M3, C-6, SEC-14): the governed read path enforces Virtual Filters on raw upstream rows when the
/// caller passes the enforcement explicitly; only then is the Virtual Filter predicate removed from the in-memory row
/// filter, every other row filter, the column denies and the masking still apply. Nothing reaches the connector.
/// </summary>
public sealed class GovernedConnectorReaderVirtualFilterTests
{
    private static readonly TableMetadata Table = Customers("varchar");

    private static TableAccessDecision Decision(string? consentFilter, IReadOnlyDictionary<string, ColumnAccessLevel>? columns = null, params AppliedVirtualFilterBinding[] pairs) =>
        TableAccessDecision.Allowed(Table.Identifier, columns ?? new Dictionary<string, ColumnAccessLevel>(), consentFilter, hasUnconstrainedColumnAllow: true)
            .WithMandatoryPredicate("EXISTS (SELECT 1 FROM vf)", pairs.Select(p => p.Filter.Name).Distinct().ToList(), pairs);

    private static ConnectorSessionContext Session(TableAccessDecision decision) =>
        new(FederatedVfPipelineHarness.User(), FederatedVfPipelineHarness.Tenant, decision, Table.Columns.Select(c => c.ColumnName).ToList(),
            new Dictionary<string, object?>(), decision.CombinedRowFilterSql);

    private static GovernedRowPolicy Policy(VirtualFilterEnforcement? enforcement, IColumnMaskingProvider? masking = null) =>
        new(masking ?? Substitute.For<IColumnMaskingProvider>(), HmacKeyId: null, VirtualFilterEnforcement: enforcement);

    private static IReadOnlyDictionary<string, object?> Ticket(string id, string name) =>
        FederatedVfPipelineHarness.Row(("id", id), ("account_id", "A"), ("name", name), ("secret", "s-" + id));

    [Fact]
    public async Task ReadAsync_EnforcementAndConsentFilter_BothApplied()
    {
        var stub = new StubTableConnector("crm_api", [Ticket("1", "OPEN"), Ticket("2", "OPEN"), Ticket("3", "CLOSED")]);
        var enforcement = Enforcement(Table, (PairById(), Keys([VirtualFilterKeyType.Text], ["1"], ["3"])));
        var session = Session(Decision("name = 'OPEN'", null, PairById()));

        var result = await GovernedConnectorReader.ReadAsync(stub.Connector, session, Table, Policy(enforcement), CancellationToken.None);

        result.Rows.ShouldHaveSingleItem()["id"].ShouldBe("1");
        result.RowsBeforeVirtualFilter.ShouldBe(3);
        stub.UpstreamCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_EnforcementCoversSubset_ThrowsEnforcementIncomplete_BeforeUpstreamCall()
    {
        var other = new AppliedVirtualFilterBinding(KeyFilter("vf_account", "account_id"), new FilterBinding { FilterName = "vf_account", TargetPattern = "crm_api.public.customers" }, 1);
        var stub = new StubTableConnector("crm_api", [Ticket("1", "OPEN")]);
        var enforcement = Enforcement(Table, (PairById(), Keys([VirtualFilterKeyType.Text], ["1"])));
        var session = Session(Decision(null, null, PairById(), other));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            GovernedConnectorReader.ReadAsync(stub.Connector, session, Table, Policy(enforcement), CancellationToken.None));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
        stub.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAsync_EnforcementForAnotherTable_ThrowsEnforcementIncomplete()
    {
        var orders = Table with { Identifier = new TableIdentifier("crm_api", "public", "orders") };
        var stub = new StubTableConnector("crm_api", [Ticket("1", "OPEN")]);
        var enforcement = Enforcement(orders, (PairById(), Keys([VirtualFilterKeyType.Text], ["1"])));

        var ex = await Should.ThrowAsync<GatewaySecurityException>(() =>
            GovernedConnectorReader.ReadAsync(stub.Connector, Session(Decision(null, null, PairById())), Table, Policy(enforcement), CancellationToken.None));

        ex.ErrorCode.ShouldBe("VF_ENFORCEMENT_INCOMPLETE");
        stub.UpstreamCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAsync_ConnectorInjectsEnforcementItem_IsIgnored()
    {
        // C-6 / SEC-14: a connector cannot supply its own enforcement through session.Items.
        var injected = Enforcement(Table, (PairById(), Keys([VirtualFilterKeyType.Text], ["1"])));
        var stub = new StubTableConnector("crm_api", [Ticket("1", "OPEN"), Ticket("2", "OPEN")], session =>
        {
            session.Items["VirtualFilterEnforcement"] = injected;
            session.Items[typeof(VirtualFilterEnforcement).FullName!] = injected;
            session.Items["VirtualFilterFederationHandled"] = true;
            session.Items[GovernedConnectorReader.RlsPushdownExecutedKey] = true;
        });

        await Should.ThrowAsync<GatewayForbiddenException>(() =>
            GovernedConnectorReader.ReadAsync(stub.Connector, Session(Decision(null, null, PairById())), Table, Policy(null), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_WithEnforcement_SessionItemsHoldNoEnforcementOrKeys()
    {
        // ST-13: the authorized key set is never exposed to the connector or left behind in the session.
        const string authorizedKey = "authorized-key-4711";
        IReadOnlyList<object?>? seenByConnector = null;
        var stub = new StubTableConnector("crm_api", [Ticket(authorizedKey, "OPEN")], s => seenByConnector = s.Items.Values.ToList());
        var enforcement = Enforcement(Table, (PairById(), Keys([VirtualFilterKeyType.Text], [authorizedKey])));
        var session = Session(Decision(null, null, PairById()));

        await GovernedConnectorReader.ReadAsync(stub.Connector, session, Table, Policy(enforcement), CancellationToken.None);

        foreach (var values in new[] { seenByConnector.ShouldNotBeNull(), session.Items.Values.ToList() })
        {
            values.ShouldNotContain(v => v is VirtualFilterEnforcement || v is VirtualFilterKeySet);
            values.ShouldNotContain(v => v != null && v.ToString()!.Contains(authorizedKey));
        }
    }

    [Fact]
    public async Task ReadAsync_DeniedKeyColumn_StillUsedForFiltering_NotReturned()
    {
        // ST-38: the key column is denied to the caller and a column is masked; enforcement uses the raw key value,
        // the returned rows lack the denied column and the masked column is masked after enforcement.
        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");
        var columns = new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Deny, ["secret"] = ColumnAccessLevel.Mask };
        var stub = new StubTableConnector("crm_api", [Ticket("1", "a"), Ticket("2", "b")]);
        var enforcement = Enforcement(Table, (PairById(), Keys([VirtualFilterKeyType.Text], ["2"])));

        var result = await GovernedConnectorReader.ReadAsync(stub.Connector, Session(Decision(null, columns, PairById())), Table, Policy(enforcement, masking), CancellationToken.None);

        var row = result.Rows.ShouldHaveSingleItem();
        row.ContainsKey("id").ShouldBeFalse();
        row["name"].ShouldBe("b");
        row["secret"].ShouldBe("***");
    }

    [Fact]
    public async Task ReadAsync_WithoutVirtualFilters_ResultHasNoVirtualFilterCount()
    {
        var stub = new StubTableConnector("crm_api", [Ticket("1", "OPEN")]);
        var decision = TableAccessDecision.Allowed(Table.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var result = await GovernedConnectorReader.ReadAsync(stub.Connector, Session(decision), Table, Policy(null), CancellationToken.None);

        result.Rows.Count.ShouldBe(1);
        result.RowsBeforeVirtualFilter.ShouldBeNull();
    }
}
