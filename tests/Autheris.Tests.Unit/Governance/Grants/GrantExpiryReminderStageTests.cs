#pragma warning disable CA2012
namespace Autheris.Tests.Unit.Governance.Grants;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Autheris.Tests.Unit.Security;
using Shouldly;
using Xunit;

public sealed class GrantExpiryReminderStageTests
{
    private static readonly TenantId Tenant = new("tenant-stage");
    private static readonly TableIdentifier Table = new("warehouse", "dbo", "customers");
    private static readonly DateTimeOffset ValidTo = new(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly int[] Days = [30, 7];

    [Theory]
    [InlineData(-31, 0, 0)]  // before the earliest reminder window
    [InlineData(-30, 0, 1)]  // first reminder due exactly at 30 days
    [InlineData(-29, 1, 0)]  // already sent
    [InlineData(-8, 1, 0)]
    [InlineData(-7, 1, 2)]   // second reminder due at 7 days
    [InlineData(-6, 2, 0)]
    [InlineData(-3, 0, 2)]   // late sweep sends only the most imminent stage
    public void NextReminderStage_follows_tenant_days(int daysFromValidTo, int sentStage, int expected)
    {
        var now = ValidTo.AddDays(daysFromValidTo);
        GrantExpiryHostedService.NextReminderStage(now, ValidTo, sentStage, Days).ShouldBe(expected);
    }

    [Fact]
    public void Expired_grant_gets_no_reminder()
    {
        GrantExpiryHostedService.NextReminderStage(ValidTo, ValidTo, 0, Days).ShouldBe(0);
        GrantExpiryHostedService.NextReminderStage(ValidTo.AddMinutes(1), ValidTo, 0, Days).ShouldBe(0);
    }

    [Fact]
    public void Empty_or_invalid_lists_send_nothing()
    {
        GrantExpiryHostedService.NextReminderStage(ValidTo.AddDays(-1), ValidTo, 0, []).ShouldBe(0);
        GrantExpiryHostedService.NextReminderStage(ValidTo.AddDays(-1), ValidTo, 0, [0, -5, 4000]).ShouldBe(0);
    }

    [Fact]
    public void Duplicate_days_collapse_and_order_does_not_matter()
    {
        GrantExpiryHostedService.NextReminderStage(ValidTo.AddDays(-7), ValidTo, 0, [7, 30, 7, 30]).ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void List_bounded()
    {
        // More than ten entries are truncated to the ten largest lead times; smaller ones never fire.
        var many = Enumerable.Range(1, 15).ToArray();
        var stage = GrantExpiryHostedService.NextReminderStage(ValidTo.AddDays(-1), ValidTo, 0, many);
        stage.ShouldBe(GrantExpiryHostedService.MaxReminderStages);
        GrantExpiryHostedService.NextReminderStage(ValidTo.AddDays(-1), ValidTo, GrantExpiryHostedService.MaxReminderStages, many).ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Reminder_without_values()
    {
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = Table,
            TenantId = Tenant,
            GranteeSid = new Sid("S-1-5-21-1001"),
            ValidFrom = ValidTo.AddDays(-60),
            ValidTo = ValidTo,
            TargetColumns = ["ssn"],
            Description = "customer secrets"
        };

        var payload = ConsentEventPayloads.ForGrant(consent, ConsentWorkflowMode.Internal, 1, "EXPIRING", ValidTo.AddDays(-7), reminderStage: 2);
        var json = ConsentEventPayloads.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("reminderStage", StringComparison.OrdinalIgnoreCase) && p.Value.GetInt32() == 2).ShouldBeTrue();
        json.ShouldNotContain("ssn");
        json.ShouldNotContain("customer secrets");
    }

    [Fact]
    public async Task Sweep_sends_each_stage_once_using_tenant_days()
    {
        var clock = new FakeTimeProvider(ValidTo.AddDays(-30));
        var sentStage = 0;
        var store = NullFake.Create<IGrantExpiryStore>((m, args) => m.Name switch
        {
            nameof(IGrantExpiryStore.GetGrantsWithPendingReminderAsync) => Task.FromResult<IReadOnlyList<ExpiringGrantCandidate>>(
            [
                new(new Consent { Id = Guid.NewGuid(), TableIdentifier = Table, TenantId = Tenant, ValidFrom = ValidTo.AddDays(-90), ValidTo = ValidTo, ExpiryNoticeStage = sentStage },
                    ConsentWorkflowMode.Internal, null)
            ]),
            nameof(IGrantExpiryStore.MarkReminderStageSentAsync) => Task.FromResult(MarkStage(args!)),
            nameof(IGrantExpiryStore.GetExpiredGrantsWithoutRecordAsync) => Task.FromResult<IReadOnlyList<ExpiringGrantCandidate>>([]),
            nameof(IGrantExpiryStore.GetOpenPendingRequestsAsync) => Task.FromResult<IReadOnlyList<ConsentRequest>>([]),
            _ => NullFake.UseDefault
        });
        bool MarkStage(object?[] args)
        {
            sentStage = (int)args[1]!;
            return true;
        }

        var service = new GrantExpiryHostedService(store, NullFake.Create<IConsentWorkflowRouter>(), NullLogger<GrantExpiryHostedService>.Instance, timeProvider: clock);

        (await service.RunSweepAsync()).NoticesSent.ShouldBe(1);
        sentStage.ShouldBe(1);
        (await service.RunSweepAsync()).NoticesSent.ShouldBe(0);

        clock.SetUtcNow(ValidTo.AddDays(-7));
        (await service.RunSweepAsync()).NoticesSent.ShouldBe(1);
        sentStage.ShouldBe(2);
        (await service.RunSweepAsync()).NoticesSent.ShouldBe(0);
    }
}
