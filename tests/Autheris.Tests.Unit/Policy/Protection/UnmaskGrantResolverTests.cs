using System.Collections.Concurrent;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Policy.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Governance;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy.Protection;

[Trait("Category", "Security")]
public sealed class UnmaskGrantResolverTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly TableIdentifier Table = new("hr", "public", "employees");
    private static readonly Sid User = new("S-1-5-21-1001");
    private static readonly Sid Group = new("S-1-5-21-2001");
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static Consent Unmask(Sid grantee, GranteeType type, IReadOnlyList<string>? columns = null, DateTimeOffset? validTo = null, TenantId? tenant = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant ?? Tenant,
        TableIdentifier = Table,
        GranteeType = type,
        GranteeSid = grantee,
        Effect = ConsentEffect.Allow,
        GrantKind = PermissionGrantKind.Unmask,
        TargetColumns = columns,
        ValidFrom = T0.AddDays(-1),
        ValidTo = validTo ?? T0.AddDays(30)
    };

    private sealed class Harness : IDisposable
    {
        public FakeTimeProvider Time { get; } = new(T0);
        public FakeConsentRepository Consents { get; } = new();
        public EpochStore Epochs { get; } = new();
        public Notifier ChangeNotifier { get; } = new();
        public GovernanceEpochProvider EpochProvider { get; }
        public UnmaskGrantResolver Sut { get; }

        public Harness()
        {
            EpochProvider = new GovernanceEpochProvider(
                Epochs, Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }), ChangeNotifier, Time);
            Sut = new UnmaskGrantResolver(
                Consents,
                EpochProvider,
                Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, NegativeTtlSeconds = 60, EpochPollSeconds = 60 }),
                ChangeNotifier,
                Time);
        }

        public ValueTask<UnmaskCoverage> Resolve(bool groupsComplete = true, params Sid[] subjects) =>
            Sut.ResolveAsync(Tenant, subjects.Length == 0 ? [User] : subjects, groupsComplete, Table);

        public void Dispose()
        {
            Sut.Dispose();
            EpochProvider.Dispose();
        }
    }

    [Fact]
    public async Task Table_grant_covers_all_columns_and_column_grant_only_its_columns()
    {
        using var h = new Harness();
        await h.Consents.CreateConsentAsync(Unmask(Group, GranteeType.Group));
        (await h.Resolve(true, User, Group)).Covers("anything").ShouldBeTrue();

        using var h2 = new Harness();
        await h2.Consents.CreateConsentAsync(Unmask(User, GranteeType.User, ["email"]));
        var coverage = await h2.Resolve();
        coverage.Covers("EMAIL").ShouldBeTrue();
        coverage.Covers("salary").ShouldBeFalse();
    }

    [Fact]
    public async Task Failure_resolves_to_masked()
    {
        using var h = new Harness();
        h.Consents.UnmaskReadFailure = new InvalidOperationException("store down");

        (await h.Resolve()).IsEmpty.ShouldBeTrue();

        h.Consents.UnmaskReadFailure = null;
        await h.Consents.CreateConsentAsync(Unmask(User, GranteeType.User));
        h.Epochs.Failure = new InvalidOperationException("epoch store down");
        h.Time.Advance(TimeSpan.FromSeconds(61));
        (await h.Resolve()).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task Degraded_groups_resolve_to_masked()
    {
        using var h = new Harness();
        await h.Consents.CreateConsentAsync(Unmask(Group, GranteeType.Group));

        (await h.Resolve(false, User, Group)).IsEmpty.ShouldBeTrue();
        h.Consents.UnmaskReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task Group_exit_resolves_to_masked()
    {
        using var h = new Harness();
        await h.Consents.CreateConsentAsync(Unmask(Group, GranteeType.Group));

        (await h.Resolve(true, User, Group)).IsEmpty.ShouldBeFalse();
        (await h.Resolve(true, User)).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task Revoke_invalidates()
    {
        using var h = new Harness();
        var grant = Unmask(User, GranteeType.User);
        await h.Consents.CreateConsentAsync(grant);

        (await h.Resolve()).IsEmpty.ShouldBeFalse();
        (await h.Resolve()).IsEmpty.ShouldBeFalse();
        h.Consents.UnmaskReadCount.ShouldBe(1);

        h.Consents.Remove(grant.Id);
        await h.ChangeNotifier.NotifyAsync(new GovernanceChangeEvent(Tenant.Value, 1, null, T0));

        (await h.Resolve()).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task Negative_result_ttl_bounded()
    {
        using var h = new Harness();

        (await h.Resolve()).IsEmpty.ShouldBeTrue();
        await h.Consents.CreateConsentAsync(Unmask(User, GranteeType.User));
        (await h.Resolve()).IsEmpty.ShouldBeTrue();
        h.Consents.UnmaskReadCount.ShouldBe(1);

        h.Time.Advance(TimeSpan.FromSeconds(61));

        (await h.Resolve()).IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task Expired_grant_never_unmasks_even_when_cached()
    {
        using var h = new Harness();
        await h.Consents.CreateConsentAsync(Unmask(User, GranteeType.User, validTo: T0.AddSeconds(30)));

        (await h.Resolve()).IsEmpty.ShouldBeFalse();
        h.Time.Advance(TimeSpan.FromSeconds(31));

        (await h.Resolve()).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task Other_tenant_grant_is_ignored()
    {
        using var h = new Harness();
        await h.Consents.CreateConsentAsync(Unmask(User, GranteeType.User, tenant: new TenantId("tenant-b")));

        (await h.Resolve()).IsEmpty.ShouldBeTrue();
    }

    private sealed class EpochStore : IGovernanceEpochStore
    {
        public Exception? Failure { get; set; }

        public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) =>
            Failure is { } ex ? Task.FromException<long>(ex) : Task.FromResult(0L);

        public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(1L);
    }

    private sealed class Notifier : IGovernanceChangeNotifier
    {
        private readonly ConcurrentDictionary<Guid, Action<GovernanceChangeEvent>> _handlers = new();

        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default)
        {
            foreach (var h in _handlers.Values)
            {
                h(change);
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler)
        {
            var id = Guid.NewGuid();
            _handlers.TryAdd(id, handler);
            return new Token(() => _handlers.TryRemove(id, out _));
        }

        private sealed class Token(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
