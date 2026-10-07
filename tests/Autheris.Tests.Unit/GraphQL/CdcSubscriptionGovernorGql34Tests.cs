namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Subscriptions;
using HotChocolate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// GQL-3: subscribe and delivery are audited. GQL-4: topics are checked against the catalog and the access decision,
/// cdc_all is reserved for governance admins, and open subscriptions are limited per subject.
/// </summary>
public sealed class CdcSubscriptionGovernorGql34Tests
{
    private sealed class OneEventChannel : ICdcEventChannel
    {
        public List<string> Topics { get; } = [];

        public async IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Topics.Add(topic);
            yield return new CdcEvent("e1", new TableIdentifier("default", "public", "orders"), CdcOperation.Insert, "tenant-a", null, new Dictionary<string, object?> { ["id"] = 1 }, DateTimeOffset.UtcNow);
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private static ClaimsPrincipal User(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-SUB"),
            new("tenant_id", "tenant-a"),
            new("exp", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static (IServiceProvider Services, IAuditLogRepository Audit, CdcSubscriptionGovernor Governor) Create(
        Func<TableIdentifier, TableAccessDecision?> decide,
        int maxPerSubject = 10)
    {
        var resolver = Substitute.For<ITableAccessResolver>();
        resolver.ResolveTableAccessAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var table = ci.ArgAt<TableIdentifier>(1);
                var decision = decide(table) ?? throw new TableNotFoundException(table);
                return Task.FromResult(new ResolvedTableAccess(new TableMetadata { Identifier = table }, decision, new TenantId("tenant-a"), new Sid("S-1-5-21-SUB"), ci.ArgAt<ClaimsPrincipal?>(0)!));
            });

        var audit = Substitute.For<IAuditLogRepository>();
        var options = Options.Create(new GatewayOptions { GraphQL = new GraphQLOptions { MaxSubscriptionsPerSubject = maxPerSubject } });
        var governor = new CdcSubscriptionGovernor(options, NullLogger<CdcSubscriptionGovernor>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(options)
            .AddSingleton(governor)
            .AddSingleton(resolver)
            .AddSingleton(audit)
            .BuildServiceProvider();
        return (services, audit, governor);
    }

    private static IStreamRlsPolicyEnforcer AllowAll()
    {
        var enforcer = Substitute.For<IStreamRlsPolicyEnforcer>();
        enforcer.EvaluateAndMaskAsync(Arg.Any<CdcEvent>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<StreamSecurityDecision>(StreamSecurityDecision.Allowed(new Dictionary<string, object?> { ["id"] = 1 })));
        return enforcer;
    }

    private static TableAccessDecision Allowed(TableIdentifier t) =>
        TableAccessDecision.Allowed(t, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

    [Fact]
    public async Task CdcAll_ForNonAdmin_IsDenied_AndAudited()
    {
        var (services, audit, _) = Create(Allowed);

        var enumerator = new Subscription().SubscribeToTableEventsAsync("cdc_all", null, new OneEventChannel(), AllowAll(), services, User(), CancellationToken.None).GetAsyncEnumerator();

        var ex = await Should.ThrowAsync<GraphQLException>(async () => await enumerator.MoveNextAsync());
        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        await audit.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == "STREAM_SUBSCRIBE" && e.Decision == "DENY"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CdcAll_ForGovernanceAdmin_IsAllowed()
    {
        var (services, _, _) = Create(Allowed);
        var channel = new OneEventChannel();

        await using var enumerator = new Subscription().SubscribeToTableEventsAsync("cdc_all", null, channel, AllowAll(), services, User("GovernanceAdmin"), CancellationToken.None).GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        channel.Topics.ShouldBe(["cdc_all"]);
    }

    [Theory]
    [InlineData("unknown_table")]
    [InlineData("salaries")]
    public async Task UnknownOrDeniedTable_IsRejected_WithSameError(string table)
    {
        var (services, _, _) = Create(t => t.TableName switch
        {
            "salaries" => TableAccessDecision.Denied(t, "no consent"),
            "orders" => Allowed(t),
            _ => null
        });
        var channel = new OneEventChannel();

        var enumerator = new Subscription().SubscribeToTableEventsAsync(table, null, channel, AllowAll(), services, User(), CancellationToken.None).GetAsyncEnumerator();

        var ex = await Should.ThrowAsync<GraphQLException>(async () => await enumerator.MoveNextAsync());
        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        ex.Errors[0].Message.ShouldNotContain(table);
        channel.Topics.ShouldBeEmpty();
    }

    [Fact]
    public async Task AllowedTable_IsSubscribed_AndDeliveryIsAuditedOnEnd()
    {
        var (services, audit, governor) = Create(Allowed);
        var channel = new OneEventChannel();
        using var cts = new CancellationTokenSource();

        var enumerator = new Subscription().SubscribeToTableEventsAsync("orders", null, channel, AllowAll(), services, User(), cts.Token).GetAsyncEnumerator(cts.Token);
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        channel.Topics.ShouldBe(["cdc_orders"]);
        governor.OpenSubscriptions(new TenantId("tenant-a"), new Sid("S-1-5-21-SUB")).ShouldBe(1);

        await enumerator.DisposeAsync();

        governor.OpenSubscriptions(new TenantId("tenant-a"), new Sid("S-1-5-21-SUB")).ShouldBe(0);
        await audit.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == "STREAM_SUBSCRIBE" && e.Decision == "ALLOW"), Arg.Any<CancellationToken>());
        await audit.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == "STREAM_DELIVERY" && e.DetailsJson.Contains("\"delivered\":1")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubscriptionsPerSubject_AreLimited()
    {
        var (services, _, _) = Create(Allowed, maxPerSubject: 2);
        var open = new List<IAsyncEnumerator<StreamCdcEvent>>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var e = new Subscription().SubscribeToTableEventsAsync("orders", null, new OneEventChannel(), AllowAll(), services, User(), CancellationToken.None).GetAsyncEnumerator();
                (await e.MoveNextAsync()).ShouldBeTrue();
                open.Add(e);
            }

            var third = new Subscription().SubscribeToTableEventsAsync("orders", null, new OneEventChannel(), AllowAll(), services, User(), CancellationToken.None).GetAsyncEnumerator();
            var ex = await Should.ThrowAsync<GraphQLException>(async () => await third.MoveNextAsync());
            ex.Errors[0].Code.ShouldBe("TOO_MANY_REQUESTS");
        }
        finally
        {
            foreach (var e in open)
            {
                await e.DisposeAsync();
            }
        }
    }
}
