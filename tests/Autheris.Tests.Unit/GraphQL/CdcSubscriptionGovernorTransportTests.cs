namespace Autheris.Tests.Unit.GraphQL;

using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.GraphQL.Subscriptions;
using HotChocolate;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using Autheris.Tests.Common.Fakes;

/// <summary>Transport admission of CDC subscriptions is mandatory: a missing gate and the all-tables topic fail closed.</summary>
[Trait("Category", "Security")]
public sealed class CdcSubscriptionGovernorTransportTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static ClaimsPrincipal User(bool admin) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-USER"),
            new Claim("tenant_id", Tenant.Value),
            new Claim(ClaimTypes.Role, admin ? "GovernanceAdmin" : "Analyst")
        ],
        "Bearer"));

    private static CdcSubscriptionGovernor Governor() =>
        new(Options.Create(new GatewayOptions()), NullLogger<CdcSubscriptionGovernor>.Instance);

    private static ITableAccessResolver AllowAllResolver()
    {
        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var table = ci.ArgAt<TableIdentifier>(1);
                var decision = TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
                return Task.FromResult(new ResolvedTableAccess(new TableMetadata { Identifier = table }, decision, Tenant, new Sid("S-1-USER"), ci.ArgAt<ClaimsPrincipal?>(0)!));
            });
        return resolver;
    }

    /// <summary>No gate supplied (nothing registered): admission to a table topic is denied.</summary>
    internal static async Task AssertNoGateDeniesTableSubscriptionAsync()
    {
        var table = new TableIdentifier("default", "public", "orders");
        await Should.ThrowAsync<TransportNotAllowedException>(() =>
            Governor().AdmitAsync(User(admin: false), "cdc_orders", table, AllowAllResolver(), new InMemoryAuditLogRepository(), CancellationToken.None, transportGate: null));
    }

    /// <summary>All-tables topic: an admin without a usable ingress transport is denied, with one with a transport it is admitted.</summary>
    internal static async Task AssertAllTablesAdmissionAsync()
    {
        IAuditLogRepository audit = new InMemoryAuditLogRepository();
        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            Governor().AdmitAsync(User(admin: true), "cdc_all", null, null, audit, CancellationToken.None, transportGate: null));
        ex.Errors[0].Code.ShouldBe("FORBIDDEN");

        var gate = Substitute.For<IDataSourceTransportGate>();
        gate.Ingress.Returns(DataSourceTransportFlags.GraphQL);
        await using var lease = await Governor().AdmitAsync(User(admin: true), "cdc_all", null, null, audit, CancellationToken.None, transportGate: gate);
        lease.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(DataSourceTransportFlags.None)]
    [InlineData(DataSourceTransportFlags.GraphQL | DataSourceTransportFlags.Rest)]
    public async Task All_tables_subscription_denied_for_unusable_ingress(DataSourceTransportFlags ingress)
    {
        var gate = Substitute.For<IDataSourceTransportGate>();
        gate.Ingress.Returns(ingress);
        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            Governor().AdmitAsync(User(admin: true), "cdc_all", null, null, new InMemoryAuditLogRepository(), CancellationToken.None, transportGate: gate));
        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
    }

    [Fact]
    public Task Subscription_without_registered_gate_is_denied() => AssertNoGateDeniesTableSubscriptionAsync();

    [Fact]
    public Task All_tables_subscription_requires_transport_admission() => AssertAllTablesAdmissionAsync();
}
