using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.Streaming;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Events.Services;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Application.Webhooks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class CdcWebhookDeliveryServiceTests
{
    /// <summary>Regression helper: an event dropped by the transport gate is counted under its own reason, audited, and never enqueued.</summary>
    internal static async Task AssertTransportDropIsRecordedAsync()
    {
        var reasons = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Name == "autheris_cdc_webhook_dropped_total")
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var t in tags)
            {
                if (t.Key == "reason")
                {
                    lock (reasons) { reasons.Add((string)t.Value!); }
                }
            }
        });
        listener.Start();

        var f = new Fixture();
        await f.AddSubscription();
        var enforcer = Substitute.For<IStreamRlsPolicyEnforcer>();
        enforcer.EvaluateAndMaskAsync(Arg.Any<CdcEvent>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(StreamSecurityDecision.Denied("Transport not allowed")));
        f.EnforcerOverride = enforcer;
        var service = f.Create();

        await service.ProcessEventAsync(Event(), default);
        await service.FlushAuditAsync(default);

        lock (reasons)
        {
            reasons.ShouldContain("transport_denied");
        }

        f.Outbox.Rows.ShouldBeEmpty();
        f.AuditEntries.ShouldHaveSingleItem().Decision.ShouldBe("DENY");
    }

    [Fact]
    [Trait("Category", "Security")]
    public Task Service_TransportDeniedEvent_UsesDistinctDropReason() => AssertTransportDropIsRecordedAsync();
}
