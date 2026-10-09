using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

public sealed class AuthFailureAggregatorTests
{
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly List<AuditLogEntry> _recorded = [];

    public AuthFailureAggregatorTests()
    {
        _auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => _recorded.Add(e)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task RecordFailureAsync_FirstFailureInWindow_RecordsImmediately()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());
        var aggregator = new AuthFailureAggregator(_auditRepo, options, NullLogger<AuthFailureAggregator>.Instance);

        await aggregator.RecordFailureAsync("192.168.1.50", "INVALID_CREDENTIALS", "user1", "tenant1");

        _recorded.Count.ShouldBe(1);
        _recorded[0].EventType.ShouldBe(AuditEventTypes.AuthFailed);
        _recorded[0].Decision.ShouldBe("DENY");
        _recorded[0].DetailsJson.ShouldContain("192.168.1.50");
    }

    [Fact]
    public async Task RecordFailureAsync_MultipleFailuresInSameWindow_AggregatesAndDoesNotSpam()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());
        var aggregator = new AuthFailureAggregator(_auditRepo, options, NullLogger<AuthFailureAggregator>.Instance);

        // 100 failures in rapid succession
        for (int i = 0; i < 100; i++)
        {
            await aggregator.RecordFailureAsync("192.168.1.50", "INVALID_CREDENTIALS", "user1", "tenant1");
        }

        // First one was recorded immediately, subsequent ones buffered
        _recorded.Count.ShouldBeLessThan(10);

        // Flush window
        await aggregator.FlushAsync();

        // Check aggregated event or count
        _recorded.ShouldContain(e => e.DetailsJson.Contains("\"count\":") || e.EventType == AuditEventTypes.AuthBruteForceDetected);
    }

    [Fact]
    public async Task RecordFailureAsync_ExcessiveFailures_EmitsBruteForceDetectedOncePerWindow()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());
        var aggregator = new AuthFailureAggregator(_auditRepo, options, NullLogger<AuthFailureAggregator>.Instance, bruteForceThreshold: 10);

        for (int i = 0; i < 25; i++)
        {
            await aggregator.RecordFailureAsync("10.0.0.99", "BAD_PASSWORD", "admin", "tenant1");
        }

        await aggregator.FlushAsync();

        var bruteForceEvents = _recorded.FindAll(e => e.EventType == AuditEventTypes.AuthBruteForceDetected);
        bruteForceEvents.Count.ShouldBe(1);
        bruteForceEvents[0].DetailsJson.ShouldContain("10.0.0.99");
    }
}
