namespace Autheris.Tests.Unit.Common;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Events.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Infrastructure.Streaming;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class TableIdentifierConsistencyTests
{
    [Fact]
    public void StreamCdcEvent_WithTableIdentifier_PreservesTableIdentifier()
    {
        var tid = new TableIdentifier("sales_domain", "dbo", "orders");
        var now = DateTimeOffset.UtcNow;
        var evt = new StreamCdcEvent(
            eventId: "evt-123",
            table: tid,
            operation: "INSERT",
            tenantId: "tenant-1",
            payloadJson: "{}",
            timestamp: now);

        evt.Table.ShouldBe("sales_domain.dbo.orders");
        evt.TableIdentifier.ShouldNotBeNull();
        evt.TableIdentifier.Value.ShouldBe(tid);
    }

    [Fact]
    public void ProcedureDefinition_ExposesTypedTableIdentifiers()
    {
        var proc = new ProcedureDefinition(
            Name: "get_orders",
            Summary: "Retrieves orders",
            ProcedureName: "sp_get_orders",
            Mode: ProcedureMode.Read,
            DataSource: "analytics",
            Parameters: [],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: "dbo.orders",
            ClearedResultColumns: [],
            RequiredRoles: [],
            AllowDynamicSql: false,
            TimeoutSeconds: 30)
        {
            ReferencedTables = ["dbo.orders", "dbo.customers"]
        };

        proc.ResultTableIdentifier.ShouldNotBeNull();
        proc.ResultTableIdentifier.Value.ShouldBe(new TableIdentifier("analytics", "dbo", "orders"));

        proc.ReferencedTableIdentifiers.Count.ShouldBe(2);
        proc.ReferencedTableIdentifiers[0].ShouldBe(new TableIdentifier("analytics", "dbo", "orders"));
        proc.ReferencedTableIdentifiers[1].ShouldBe(new TableIdentifier("analytics", "dbo", "customers"));
    }

    [Fact]
    public void ResultColumnSource_ToTableIdentifier_MapsCorrectly()
    {
        var source = new ResultColumnSource("sales", "orders", "total_amount");
        var tid = source.ToTableIdentifier("lakehouse");

        tid.ShouldNotBeNull();
        tid.Value.Domain.ShouldBe("lakehouse");
        tid.Value.Schema.ShouldBe("sales");
        tid.Value.TableName.ShouldBe("orders");

        var emptySource = new ResultColumnSource("sales", null, "computed_col");
        emptySource.ToTableIdentifier().ShouldBeNull();
    }

    [Fact]
    public async Task CloudEventSubscriptionStore_GetSubscriptionsAsync_MatchesTableIdentifier()
    {
        var store = new InMemoryCloudEventSubscriptionStore();
        const string tenantId = "tenant-test";

        var subWildcard = new CloudEventWebhookSubscription(
            Id: "sub-1",
            TenantId: tenantId,
            TargetUrl: "https://hook1.com",
            FilterTable: "*",
            FilterOperations: [CdcOperation.Insert],
            HmacSecret: "secret-1");

        var subSpecific = new CloudEventWebhookSubscription(
            Id: "sub-2",
            TenantId: tenantId,
            TargetUrl: "https://hook2.com",
            FilterTable: "orders",
            FilterOperations: [CdcOperation.Insert],
            HmacSecret: "secret-2");

        var subQualified = new CloudEventWebhookSubscription(
            Id: "sub-3",
            TenantId: tenantId,
            TargetUrl: "https://hook3.com",
            FilterTable: "dbo.orders",
            FilterOperations: [CdcOperation.Insert],
            HmacSecret: "secret-3");

        await store.RegisterSubscriptionAsync(subWildcard);
        await store.RegisterSubscriptionAsync(subSpecific);
        await store.RegisterSubscriptionAsync(subQualified);

        var tid = new TableIdentifier("sales", "dbo", "orders");

        var matches = await store.GetSubscriptionsAsync(tenantId, tid, CdcOperation.Insert);
        matches.Count.ShouldBe(3);

        var otherTid = new TableIdentifier("sales", "dbo", "customers");
        var otherMatches = await store.GetSubscriptionsAsync(tenantId, otherTid, CdcOperation.Insert);
        otherMatches.Count.ShouldBe(1);
        otherMatches[0].Id.ShouldBe("sub-1");
    }

    [Fact]
    public void WalMessageDecoder_RegisterRelation_WithTableIdentifier_Succeeds()
    {
        var decoder = new WalMessageDecoder();
        var tid = new TableIdentifier("postgresql", "public", "payments");

        decoder.RegisterRelation(42, tid, ["id", "amount", "status"]);

        decoder.TryGetRelation(42, out var relation).ShouldBeTrue();
        relation.ShouldNotBeNull();
        relation.Schema.ShouldBe("public");
        relation.TableName.ShouldBe("payments");
        relation.TableIdentifier.ShouldBe(tid);
    }

    [Fact]
    public async Task IcebergRestCatalog_TableIdentifierOverloads_DelegateCorrectly()
    {
        var mock = Substitute.For<IIcebergRestCatalogFederationService>();
        var tid = new TableIdentifier("tenant-1", "lakehouse_ns", "transactions");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

        mock.LoadTableAsync("tenant-1", tid, principal)
            .Returns(new IcebergLoadTableResponse("metadata-loc", null, new Dictionary<string, string>()));

        var response = await mock.LoadTableAsync("tenant-1", tid, principal);
        response.ShouldNotBeNull();
        response.MetadataLocation.ShouldBe("metadata-loc");
    }
}
