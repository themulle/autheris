namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Messaging;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

public sealed class Pol8ScheduledDenyConsentTests
{
    [Fact]
    public void ComputeDecisionCacheTtl_WhenUpcomingScheduledDenyExists_BoundsTtlByDenyStartTime()
    {
        // POL-8: Vorab terminierte DENY-Consents greifen bis zu 10 min verspätet.
        // Cache TTL of an active ALLOW must not outlive the start of an upcoming scheduled DENY.
        var now = DateTimeOffset.UtcNow;
        var table = new TableIdentifier("finance", "dbo", "invoices");

        var activeAllow = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            ValidFrom = now.AddHours(-1),
            ValidTo = now.AddHours(2)
        };

        var upcomingDeny = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = activeAllow.TableId,
            TableIdentifier = table,
            Effect = ConsentEffect.Deny,
            ValidFrom = now.AddMinutes(2),
            ValidTo = now.AddHours(1)
        };

        var consents = new List<Consent> { activeAllow, upcomingDeny };

        var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(isHighlySensitive: false, consents, now);

        // RED PHASE: Currently ComputeDecisionCacheTtl ignores upcomingDeny because it filters ValidFrom <= now or ignores Effect == Deny,
        // returning 10 minutes instead of 2 minutes.
        ttl.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task GetActiveConsentsForSubjectsAsync_IncludesUpcomingDenyConsentsForTtlCalculation()
    {
        // POL-8: Repository must include upcoming scheduled DENY consents so that TableAccessPolicy
        // can compute a TTL bounded by the DENY start time while still resolving active access correctly.
        var eventBus = new InProcessChannelEventBus();
        var epochService = new EpochValidationService(eventBus: eventBus);
        var repo = new SqliteGovernanceRepository(epochService);

        var table = new TableIdentifier("finance", "dbo", "finance_table_1");
        var meta = await repo.GetTableMetadataAsync(table);
        meta.ShouldNotBeNull();

        var userSid = new Sid("S-1-5-21-USER-POL8");
        var tenant = new TenantId("tenant-a");
        var now = DateTimeOffset.UtcNow;

        // Active ALLOW consent
        await repo.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = table,
            TenantId = tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = now.AddHours(-1),
            ValidTo = now.AddHours(4),
            CreatedBySid = new Sid("SYSTEM")
        });

        // Upcoming scheduled DENY consent (starts in 2 minutes)
        await repo.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = table,
            TenantId = tenant,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = now.AddMinutes(2),
            ValidTo = now.AddHours(1),
            CreatedBySid = new Sid("SYSTEM")
        });

        var loadedConsents = await repo.GetActiveConsentsForSubjectsAsync([userSid], table, now, tenant);

        // RED PHASE: Currently GetActiveConsentsForSubjectsAsync filters atTime < validFrom and discards the upcoming DENY consent.
        loadedConsents.ShouldContain(c => c.Effect == ConsentEffect.Deny && c.ValidFrom > now);

        var resolution = new ConsentResolutionService();
        var decision = resolution.ResolveAccess(userSid, new HashSet<Sid>(), new HashSet<string>(), table, loadedConsents);

        // Access must currently still be Allowed because the DENY has not started yet
        decision.IsAllowed.ShouldBeTrue();

        // But TTL must be capped to 2 minutes
        var ttl = ConsentResolutionService.ComputeDecisionCacheTtl(false, loadedConsents, now);
        ttl.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(2));
    }
}
