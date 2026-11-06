namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Workflows;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConsentRecertificationScanTests
{
    [Fact]
    public async Task Expiring_consents_produce_recertification_audit_event_without_itsm()
    {
        var consentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var expiringConsent = new Consent
        {
            Id = consentId,
            TenantId = new TenantId("tenant-a"),
            TableIdentifier = new TableIdentifier("finance", "public", "ledgers"),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-ALICE"),
            ValidFrom = now.AddDays(-10),
            ValidTo = now.AddHours(24)
        };

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetExpiringConsentsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([expiringConsent]));

        var recordedEntries = new List<AuditLogEntry>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(e => recordedEntries.Add(e)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = new ConsentRecertificationWorkflowService(
            consentRepo,
            NullLogger<ConsentRecertificationWorkflowService>.Instance,
            auditRepo);

        var firstCount = await sut.ScanAndTriggerExpiringConsentRecertificationsAsync();
        var secondCount = await sut.ScanAndTriggerExpiringConsentRecertificationsAsync();

        firstCount.ShouldBe(1);
        secondCount.ShouldBe(0, "24-hour in-memory de-duplication must prevent duplicate recertification audit events within the same window");

        recordedEntries.Count.ShouldBe(1);
        var entry = recordedEntries[0];
        entry.EventType.ShouldBe(AuditEventTypes.ConsentRecertificationRequired);
        entry.TenantId.Value.ShouldBe("tenant-a");
        entry.ActorSid.Value.ShouldBe("S-1-5-21-ALICE");
        entry.TargetTable.ShouldBe("finance.public.ledgers");
        entry.DetailsJson.ShouldContain(consentId.ToString());
        entry.DetailsJson.ShouldContain("tenant-a");
        entry.DetailsJson.ShouldContain("finance.public.ledgers");
    }

    [Fact]
    public void Scan_runs_without_itsm_configuration()
    {
        var options = new GatewayOptions();
        var services = new ServiceCollection();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);
        services.AddSingleton(env);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton(Options.Create(options));

        services.AddGatewayInfrastructure(options);

        services.Any(d => d.ServiceType == typeof(IConsentRecertificationService) &&
                          d.ImplementationType == typeof(ConsentRecertificationWorkflowService))
            .ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(IHostedService) &&
                          d.ImplementationType == typeof(ConsentRecertificationHostedService))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Scan_lock_name_is_governance_scoped()
    {
        ConsentRecertificationHostedService.ScanLockKey.ShouldBe("governance:recertification:scan");

        var lockRequested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recertService = Substitute.For<IConsentRecertificationService>();
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        clusterState.TryAcquireLockAsync(
                Arg.Do<string>(k => lockRequested.TrySetResult(k)),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IAsyncDisposable?>((IAsyncDisposable?)null));

        var services = new ServiceCollection()
            .AddSingleton(recertService)
            .AddSingleton(clusterState)
            .BuildServiceProvider();

        var hosted = new ConsentRecertificationHostedService(
            services,
            NullLogger<ConsentRecertificationHostedService>.Instance,
            TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await hosted.StartAsync(cts.Token);
        var requestedLockKey = await lockRequested.Task.WaitAsync(cts.Token);
        await hosted.StopAsync(CancellationToken.None);

        requestedLockKey.ShouldBe("governance:recertification:scan");
    }
}
