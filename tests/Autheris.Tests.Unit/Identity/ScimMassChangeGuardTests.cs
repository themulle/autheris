namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Identity.Services;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

/// <summary>06-FU-02 / WAVE3-SCIM-SC-01..06 and T-01..T-09: SCIM mass-deprovisioning guard.</summary>
public sealed class ScimMassChangeGuardTests
{
    private static readonly TenantId Tenant = new("tenant-1");
    private const string Realm = "realm-1";
    private const string User = "user-secret-42";

    private readonly IIdentityStoreRepository _repo = Substitute.For<IIdentityStoreRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-26T10:00:00Z"));
    private readonly ScimMassChangeProtectionOptions _settings = new() { MaxDestructiveOperations = 3, WindowMinutes = 60 };
    private readonly ILogger<ScimMassChangeGuard> _logger = Substitute.For<ILogger<ScimMassChangeGuard>>();

    private ScimMassChangeGuard Create()
    {
        var monitor = Substitute.For<IOptionsMonitor<IdentityRealmsOptions>>();
        monitor.CurrentValue.Returns(new IdentityRealmsOptions { Scim = new ScimOptions { MassChangeProtection = _settings } });
        return new ScimMassChangeGuard(_repo, monitor, _audit, _time, _logger);
    }

    private void Count(int count, DateTimeOffset? oldest = null) =>
        _repo.CountLocksSinceAsync(default, default!, default!, default, default, default)
            .ReturnsForAnyArgs(new ScimLockWindow(count, oldest ?? _time.GetUtcNow().AddMinutes(-10)));

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_BlockedLog_StripsLineBreaksFromRealmId()
    {
        Count(3);
        await Should.ThrowAsync<ScimMassChangeBlockedException>(
            () => Create().EnsureAllowedAsync(Tenant, "realm\r\nFORGED ENTRY", User));

        var logged = _logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Log")
            .Select(c => c.GetArguments()[2]?.ToString() ?? string.Empty)
            .ToList();
        logged.ShouldNotBeEmpty();
        logged.ShouldAllBe(m => !m.Contains('\r') && !m.Contains('\n'));
        logged.ShouldContain(m => m.Contains("realmFORGED ENTRY", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T01_UnderLimit_Allows()
    {
        Count(2);
        await Create().EnsureAllowedAsync(Tenant, Realm, User);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T01_AtLimit_Blocks429()
    {
        Count(3);
        var ex = await Should.ThrowAsync<ScimMassChangeBlockedException>(() => Create().EnsureAllowedAsync(Tenant, Realm, User));
        ex.RetryAfterSeconds.ShouldBe(50 * 60);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T01_WindowElapsed_AllowsAgain_AndQueryUsesOneUtcTimeProviderTimestamp()
    {
        Count(3);
        var guard = Create();
        await Should.ThrowAsync<ScimMassChangeBlockedException>(() => guard.EnsureAllowedAsync(Tenant, Realm, User));
        _time.Advance(TimeSpan.FromMinutes(61));
        Count(0);
        await guard.EnsureAllowedAsync(Tenant, Realm, User);

        await _repo.Received(1).CountLocksSinceAsync(
            Tenant, Realm, Arg.Is<IReadOnlyList<string>>(s => s.SequenceEqual(ScimMassChangeGuard.ScimSources)),
            DateTimeOffset.Parse("2026-10-26T09:00:00Z"), 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T01_Disabled_Allows_WithoutCounting()
    {
        _settings.Enabled = false;
        await Create().EnsureAllowedAsync(Tenant, Realm, User);
        await _repo.DidNotReceiveWithAnyArgs().CountLocksSinceAsync(default, default!, default!, default, default, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T01_CountFails_NoDeprovision()
    {
        _repo.CountLocksSinceAsync(default, default!, default!, default, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("db down"));
        var deprov = Substitute.For<IIdentityDeprovisioningService>();
        var service = ServiceWith(deprov, Create());
        await Should.ThrowAsync<InvalidOperationException>(() => service.DeleteUserAsync(Tenant, Realm, User));
        await deprov.DidNotReceiveWithAnyArgs().DeprovisionAndLockSubjectAsync(default, default!, default!, default!, default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T01_OtherRealm_NotCounted_QueryScopedToTenantRealmAndScimSources()
    {
        Count(0);
        await Create().EnsureAllowedAsync(Tenant, "other-realm", User);
        await _repo.Received(1).CountLocksSinceAsync(
            Tenant, "other-realm", Arg.Is<IReadOnlyList<string>>(s => s.All(x => x.StartsWith("SCIM_", StringComparison.Ordinal))),
            Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T07_OtherTenantSameRealm_NotCounted_QueryScopedToTenant()
    {
        Count(0);
        await Create().EnsureAllowedAsync(new TenantId("tenant-2"), Realm, User);
        await _repo.Received(1).CountLocksSinceAsync(
            new TenantId("tenant-2"), Realm, Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().CountLocksSinceAsync(
            Tenant, Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T06_RepeatDeleteSameUser_AlreadyLocked_NotBlockedNotCounted()
    {
        _repo.IsSubjectLockedAsync(Tenant, Realm, User, Arg.Any<CancellationToken>()).Returns(true);
        Count(3);
        await Create().EnsureAllowedAsync(Tenant, Realm, User);
        await _repo.DidNotReceiveWithAnyArgs().CountLocksSinceAsync(default, default!, default!, default, default, default);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(-3600, 1)]          // oldest lock expires now: clamped up to 1 s
    [InlineData(-10, 3590)]
    [InlineData(-3000, 600)]
    public async Task WAVE3_SCIM_T08_RetryAfter_FromOldestLock_Clamped(int oldestOffsetSeconds, int expected)
    {
        Count(3, _time.GetUtcNow().AddSeconds(oldestOffsetSeconds));
        var ex = await Should.ThrowAsync<ScimMassChangeBlockedException>(() => Create().EnsureAllowedAsync(Tenant, Realm, User));
        ex.RetryAfterSeconds.ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T08_RetryAfter_NeverAboveWindow()
    {
        Count(3, _time.GetUtcNow().AddHours(2)); // clock skew: oldest in the future
        var ex = await Should.ThrowAsync<ScimMassChangeBlockedException>(() => Create().EnsureAllowedAsync(Tenant, Realm, User));
        ex.RetryAfterSeconds.ShouldBe(3600);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T04_Block_WritesRequiredAudit_WithoutUserKeys_AndIsRateLimited()
    {
        Count(3);
        var guard = Create();
        for (var i = 0; i < 3; i++)
        {
            await Should.ThrowAsync<ScimMassChangeBlockedException>(() => guard.EnsureAllowedAsync(Tenant, Realm, User));
        }

        await _audit.Received(1).RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
        var entry = (AuditLogEntry)_audit.ReceivedCalls().Single().GetArguments()[0]!;
        entry.EventType.ShouldBe("SCIM_MASS_CHANGE_BLOCKED");
        entry.TenantId.ShouldBe(Tenant);
        entry.DetailsJson.ShouldNotContain(User);
        using var doc = JsonDocument.Parse(entry.DetailsJson);
        doc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ShouldBe(["count", "limit", "realm_id", "tenant_id", "window_minutes"]);
        _logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log").ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(61));
        await Should.ThrowAsync<ScimMassChangeBlockedException>(() => guard.EnsureAllowedAsync(Tenant, Realm, User));
        await _audit.Received(2).RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T04_AuditFailure_DoesNotConsumeTheWindow_NextBlockWritesAudit()
    {
        Count(3);
        _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("audit down")), Task.CompletedTask);
        var guard = Create();
        await Should.ThrowAsync<InvalidOperationException>(() => guard.EnsureAllowedAsync(Tenant, Realm, User));
        await Should.ThrowAsync<ScimMassChangeBlockedException>(() => guard.EnsureAllowedAsync(Tenant, Realm, User));
        await _audit.Received(2).RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T05_Block_NoLockNoDeactivationNoRevocation_AllThreeCallSites()
    {
        Count(3);
        var deprov = Substitute.For<IIdentityDeprovisioningService>();
        var service = ServiceWith(deprov, Create());
        _repo.GetUserAsync(Tenant, Realm, User, Arg.Any<CancellationToken>()).Returns(new ScimUser(User, null, "u", null, null, true, null, null, null));

        await Should.ThrowAsync<ScimMassChangeBlockedException>(() => service.DeleteUserAsync(Tenant, Realm, User));
        await Should.ThrowAsync<ScimMassChangeBlockedException>(() => service.UpdateUserAsync(
            Tenant, Realm, User, new ScimUser(User, null, "u", null, null, false, null, null, null)));
        await Should.ThrowAsync<ScimMassChangeBlockedException>(() => service.PatchUserAsync(
            Tenant, Realm, User, new ScimPatchRequest(["urn:ietf:params:scim:api:messages:2.0:PatchOp"], [new ScimPatchOperation("replace", "active", false)])));

        await deprov.DidNotReceiveWithAnyArgs().DeprovisionAndLockSubjectAsync(default, default!, default!, default!, default!, default);
        await _repo.DidNotReceiveWithAnyArgs().AddLockAsync(default!, default);
        await _repo.DidNotReceiveWithAnyArgs().DeactivateUserAsync(default, default!, default!, default);
        await _repo.DidNotReceiveWithAnyArgs().DeleteUserAsync(default, default!, default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task WAVE3_SCIM_T03_AdminDeprovision_NotCountedNorBlocked()
    {
        IIdentityDeprovisioningService service = new IdentityDeprovisioningService(_repo);
        await service.DeprovisionAsync(Tenant, Realm, User, "admin", new Sid("admin-sid"));
        await _repo.Received(1).AddLockAsync(Arg.Is<ScimLock>(l => l.Source == "admin-sid"), Arg.Any<CancellationToken>());
        await _repo.DidNotReceiveWithAnyArgs().CountLocksSinceAsync(default, default!, default!, default, default, default);
        ScimMassChangeGuard.ScimSources.ShouldNotContain("admin-sid");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(0, 60)]
    [InlineData(100_001, 60)]
    [InlineData(100, 0)]
    [InlineData(100, 1441)]
    public void WAVE3_SCIM_T09_Options_OutOfRange_FailsStartup(int max, int window)
    {
        var options = new IdentityRealmsOptions
        {
            Scim = new ScimOptions { MassChangeProtection = new ScimMassChangeProtectionOptions { MaxDestructiveOperations = max, WindowMinutes = window } }
        };
        new IdentityRealmsScimOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WAVE3_SCIM_Defaults_OnWith100Per60_AndDisabledLogsStartupWarning()
    {
        var defaults = new ScimMassChangeProtectionOptions();
        (defaults.Enabled, defaults.MaxDestructiveOperations, defaults.WindowMinutes).ShouldBe((true, 100, 60));

        var logger = Substitute.For<ILogger<IdentityRealmsScimOptionsValidator>>();
        var disabled = new IdentityRealmsOptions { Scim = new ScimOptions { MassChangeProtection = new ScimMassChangeProtectionOptions { Enabled = false } } };
        new IdentityRealmsScimOptionsValidator(null, logger).Validate(null, disabled).Succeeded.ShouldBeTrue();
        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log"
            && c.GetArguments()[0] is LogLevel.Warning).ShouldBe(1);
    }

    private ScimService ServiceWith(IIdentityDeprovisioningService deprov, ScimMassChangeGuard guard)
    {
        _repo.GetUserAsync(Tenant, Realm, User, Arg.Any<CancellationToken>())
            .Returns(new ScimUser(User, null, "u", null, null, true, null, null, null));
        return new ScimService(_repo, deprov, massChangeGuard: guard);
    }
}
