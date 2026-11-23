namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Policy.Protection;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Bounded work of the container visibility rule: short-circuit, candidate cap and snapshot read bounds (fail-closed).</summary>
[Trait("Category", "Security")]
public sealed class CatalogContainerVisibilityBoundsTests
{
    private const string UserSid = "S-1-5-21-1001";
    private static readonly TenantId Tenant = Wp0.TenantA;

    private sealed class CountingProtection(ContainerRef container) : IProtectionSnapshotProvider
    {
        public int Calls { get; private set; }

        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(new EffectiveProtection(
                container, OwnerGroupSid: null, ProtectionClass.Confidential, ProtectionClass.Confidential, TableMaskCategory: null,
                new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase), CatalogVisibilityMode.Open));
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    private static CatalogCaller Caller() => CatalogVisibilityTestFactory.Caller(Wp0.User(UserSid, Tenant.Value));

    private static TableMetadata[] Tables(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Wp0.Table("sales", $"t{i}", "d", ["id"]))];

    private static GatewayOptions Options(int maxCandidates)
    {
        var options = Wp0.Options();
        return new GatewayOptions
        {
            Tenancy = options.Tenancy,
            Catalog = options.Catalog,
            Governance = new GovernanceOptions { AccessPolicy = new AccessPolicyOptions { MaxTablesPerAssignment = maxCandidates } },
        };
    }

    [Fact]
    public async Task Nothing_open_or_granted_performs_no_per_row_lookup()
    {
        var domains = new FakeContainerDomainRepository();
        using var epochs = new GovernanceEpochProvider(domains, Options_(60));
        domains.Add(Tenant, "closed", CatalogVisibilityMode.OnRequest);
        using var provider = new ContainerVisibilitySnapshotProvider(
            domains, new FakeCatalogGrantRepository(), new FakeConsentRepository(), new FakeGovernanceCapabilities(false), epochs);
        var protection = new CountingProtection(new ContainerRef(ContainerKind.Domain, Guid.NewGuid()));
        var service = new CatalogVisibilityService(
            Wp0.Tables(Tables(50)), Wp0.Consents(), Microsoft.Extensions.Options.Options.Create(Wp0.Options()),
            protectionProvider: protection, containerVisibility: provider);

        var visible = await service.GetVisibleAsync(Caller());

        visible.Tables.ShouldBeEmpty();
        protection.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Candidate_cap_bounds_lookups_and_fails_closed()
    {
        var domains = new FakeContainerDomainRepository();
        using var epochs = new GovernanceEpochProvider(domains, Options_(60));
        var open = domains.Add(Tenant, "open", CatalogVisibilityMode.Open);
        using var provider = new ContainerVisibilitySnapshotProvider(
            domains, new FakeCatalogGrantRepository(), new FakeConsentRepository(), new FakeGovernanceCapabilities(false), epochs);
        var protection = new CountingProtection(new ContainerRef(ContainerKind.Domain, open.Id));
        var logger = new ListLogger<CatalogVisibilityService>();
        var service = new CatalogVisibilityService(
            Wp0.Tables(Tables(50)), Wp0.Consents(), Microsoft.Extensions.Options.Options.Create(Options(maxCandidates: 7)),
            logger, protectionProvider: protection, containerVisibility: provider);

        var visible = await service.GetVisibleAsync(Caller());

        protection.Calls.ShouldBe(7);
        visible.Tables.Count.ShouldBe(7);
        logger.Levels.Count(l => l == LogLevel.Warning).ShouldBe(1);
    }

    [Fact]
    public async Task Container_read_at_the_bound_logs_warning_and_truncated_container_stays_invisible()
    {
        var domains = new FakeContainerDomainRepository();
        using var epochs = new GovernanceEpochProvider(domains, Options_(60));
        for (var i = 0; i < 3; i++)
        {
            domains.Add(Tenant, $"d{i}", CatalogVisibilityMode.Open);
        }

        var logger = new ListLogger<ContainerVisibilitySnapshotProvider>();
        using var provider = new ContainerVisibilitySnapshotProvider(
            domains, new FakeCatalogGrantRepository(), new FakeConsentRepository(), new FakeGovernanceCapabilities(false), epochs,
            accessPolicy: Microsoft.Extensions.Options.Options.Create(new AccessPolicyOptions { MaxPageSize = 2 }),
            logger: logger);

        var snapshot = await provider.GetAsync(Tenant, Wp0.User(UserSid, Tenant.Value));

        snapshot.Containers.Count.ShouldBe(2);
        logger.Levels.ShouldContain(LogLevel.Warning);
    }

    [Fact]
    public async Task Data_grants_beyond_the_bound_are_not_recorded()
    {
        var domains = new FakeContainerDomainRepository();
        using var epochs = new GovernanceEpochProvider(domains, Options_(60));
        var consents = new FakeConsentRepository();
        for (var i = 0; i < 4; i++)
        {
            await consents.CreateConsentAsync(Wp0.Allow(new TableIdentifier("sales", "dbo", $"g{i}"), UserSid, Tenant, "id"));
        }

        var logger = new ListLogger<ContainerVisibilitySnapshotProvider>();
        using var provider = new ContainerVisibilitySnapshotProvider(
            domains, new FakeCatalogGrantRepository(), consents, new FakeGovernanceCapabilities(false), epochs,
            accessPolicy: Microsoft.Extensions.Options.Options.Create(new AccessPolicyOptions { MaxTablesPerAssignment = 2 }),
            logger: logger);

        var snapshot = await provider.GetAsync(Tenant, Wp0.User(UserSid, Tenant.Value));

        snapshot.DataGrantedTables.Count.ShouldBe(2);
        logger.Levels.ShouldContain(LogLevel.Warning);
    }

    [Theory]
    [InlineData(CatalogView.Granted, "GRANTED")]
    [InlineData(CatalogView.Unmarked, "GRANTED")]
    [InlineData(CatalogView.ContainerVisible, "NONE")]
    public void Container_visible_rows_report_metadata_only_access_status(CatalogView view, string expected)
    {
        var row = Wp0.Table("sales", "t", "d", ["id"]) with { CatalogView = view };

        CatalogDiscoveryService.LegacyAccessStatusOf(row).ShouldBe(expected);
    }

    private static IOptions<GovernanceCacheSettings> Options_(int poll) =>
        Microsoft.Extensions.Options.Options.Create(new GovernanceCacheSettings { EpochPollSeconds = poll });
}
