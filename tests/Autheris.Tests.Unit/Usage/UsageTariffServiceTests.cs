namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Usage;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class UsageTariffServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);

    private static (UsageTariffService Service, InMemoryTariffRepo Repo, RecordingAuditWriter Audit, ListLogger<UsageTariffService> Logger) CreateService(
        UsageAccountingOptions? options = null,
        FakeTimeProvider? clock = null,
        Func<string, bool>? isKnownSource = null,
        Func<string, string?>? costUnitResolver = null)
    {
        var repo = new InMemoryTariffRepo();
        var audit = new RecordingAuditWriter();
        var logger = new ListLogger<UsageTariffService>();
        var svc = new UsageTariffService(
            repo,
            audit,
            Options.Create(options ?? new UsageAccountingOptions()),
            logger,
            clock ?? new FakeTimeProvider(Now),
            isKnownSource ?? (s => s is "pg-main" or "dbx-prod"),
            costUnitResolver ?? (s => s == "dbx-prod" ? "DBU" : null));
        return (svc, repo, audit, logger);
    }

    [Fact]
    public async Task New_version_closes_previous()
    {
        var (svc, repo, audit, _) = CreateService();

        var v1 = await svc.AddVersionAsync(new CreateUsageTariffVersionRequest(
            "t1", "pg-main", Tomorrow, 1.0m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "S-1-5-21-1", "initial"));

        var v2 = await svc.AddVersionAsync(new CreateUsageTariffVersionRequest(
            "t1", "pg-main", Tomorrow.AddDays(5), 2.0m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "S-1-5-21-1", "price update"));

        v1.Version.ShouldBe(1);
        v2.Version.ShouldBe(2);
        var stored = await repo.GetTariffsAsync(new TenantId("t1"), DateOnly.MinValue, DateOnly.MaxValue);
        stored.Single(v => v.Version == 1).ValidUntil.ShouldBe(Tomorrow.AddDays(5));
        stored.Single(v => v.Version == 2).ValidUntil.ShouldBeNull();
        audit.Entries.Count.ShouldBe(2);
        audit.Entries.All(e => e.EventType == AuditEventTypes.UsageTariffVersionAdded).ShouldBeTrue();
    }

    [Fact]
    public async Task Retroactive_valid_from_rejected()
    {
        var (svc, repo, audit, _) = CreateService();

        var exToday = await Should.ThrowAsync<UsageTariffValidationException>(() => svc.AddVersionAsync(
            new CreateUsageTariffVersionRequest("t1", "pg-main", Today, 1m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "S-1-5-21-1", "today")));
        exToday.Message.ShouldContain("ValidFrom");

        var exPast = await Should.ThrowAsync<UsageTariffValidationException>(() => svc.AddVersionAsync(
            new CreateUsageTariffVersionRequest("t1", "pg-main", Today.AddDays(-2), 1m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "S-1-5-21-1", "past")));
        exPast.Message.ShouldContain("ValidFrom");

        repo.Versions.ShouldBeEmpty();
        audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("invalid tenant!", "pg-main", -1, 0, 0, 0, 0, 0, null, "credits", 0, "reason", "TenantId")]
    [InlineData("t1", "unknown-src", 1, 0, 0, 0, 0, 0, null, "credits", 0, "reason", "configured data source")]
    [InlineData("t1", "pg-main", 1000000001, 0, 0, 0, 0, 0, null, "credits", 0, "reason", "BasePerQuery")]
    [InlineData("t1", "pg-main", 0, 0, 0, 0, 0, 0, null, "credits", 0, "reason", "greater than 0")]
    [InlineData("t1", "dbx-prod", 0, 0, 0, 0, 0, 1, null, "credits", 0, "reason", "SourceCostUnit")]
    [InlineData("t1", "pg-main", 1, 0, 0, 0, 0, 0, "DBU", "credits", 0, "reason", "SourceCostUnit")]
    [InlineData("t1", "dbx-prod", 0, 0, 0, 0, 0, 1, "USD", "credits", 0, "reason", "collector's unit")]
    [InlineData("t1", "pg-main", 1, 0, 0, 0, 0, 0, null, "bad/label!", 0, "reason", "UnitLabel")]
    [InlineData("t1", "pg-main", 1, 0, 0, 0, 0, 0, null, "   ", 0, "reason", "UnitLabel")]
    [InlineData("t1", "pg-main", 1, 0, 0, 0, 0, 0, null, "credits", 2, "reason", "ComputeBasis")]
    [InlineData("t1", "pg-main", 1, 0, 0, 0, 0, 0, null, "credits", 0, "  ", "Reason")]
    public async Task Validation_rules(
        string tenantId,
        string dataSource,
        decimal basePerQuery,
        decimal perRow,
        decimal perMib,
        decimal perComputeSecond,
        decimal perAnalysisCost,
        decimal sourceCostFactor,
        string? sourceCostUnit,
        string unitLabel,
        short computeBasis,
        string reason,
        string expectedErrorFragment)
    {
        var (svc, _, _, _) = CreateService();

        var ex = await Should.ThrowAsync<UsageTariffValidationException>(() => svc.AddVersionAsync(
            new CreateUsageTariffVersionRequest(
                tenantId, dataSource, Tomorrow,
                basePerQuery, perRow, perMib, perComputeSecond, perAnalysisCost,
                sourceCostFactor, sourceCostUnit, unitLabel, computeBasis,
                "S-1-5-21-1", reason)));

        ex.Message.ShouldContain(expectedErrorFragment);
    }

    [Fact]
    public async Task Initial_tariffs_seeded_only_when_empty()
    {
        var options = new UsageAccountingOptions
        {
            InitialTariffs =
            [
                new InitialTariffConfig
                {
                    TenantId = "t1",
                    DataSource = "pg-main",
                    ValidFrom = Today.AddDays(-10),
                    BasePerQuery = 0.5m,
                    UnitLabel = "credits",
                    Reason = "seed v1"
                },
                new InitialTariffConfig
                {
                    TenantId = "*",
                    DataSource = "*",
                    ValidFrom = Today.AddDays(-5),
                    BasePerQuery = 0.1m,
                    UnitLabel = "credits",
                    Reason = "seed wildcard"
                }
            ]
        };

        var (svc, repo, _, logger) = CreateService(options);

        // Pre-populate ("*", "*") so it is NOT empty -> must be skipped with warning, while ("t1", "pg-main") IS seeded.
        await repo.AddTariffVersionAsync(new UsageTariffVersion(
            "*", "*", 0, Today.AddDays(-30), null, 0.2m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "admin", Now, "existing"));

        int firstSeed = await svc.SeedInitialTariffsAsync();
        firstSeed.ShouldBe(1);
        repo.Versions.Count.ShouldBe(2);
        logger.Messages.Count(m => m.Level == LogLevel.Warning).ShouldBe(1);

        // Running again should seed 0 and warn for both entries.
        int secondSeed = await svc.SeedInitialTariffsAsync();
        secondSeed.ShouldBe(0);
        repo.Versions.Count.ShouldBe(2);
        logger.Messages.Count(m => m.Level == LogLevel.Warning).ShouldBe(3);
    }

    private sealed class InMemoryTariffRepo : IUsageRepository
    {
        private int _nextVersion;
        public List<UsageTariffVersion> Versions { get; } = [];

        public Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default)
        {
            var result = Versions
                .Where(v => (string.Equals(v.TenantId, tenant.Value, StringComparison.OrdinalIgnoreCase) || v.TenantId == "*")
                            && v.ValidFrom <= toUtc
                            && (!v.ValidUntil.HasValue || v.ValidUntil.Value > fromUtc))
                .OrderBy(v => v.Version)
                .ToList();
            return Task.FromResult<IReadOnlyList<UsageTariffVersion>>(result);
        }

        public Task<UsageTariffVersion> AddTariffVersionAsync(UsageTariffVersion tariff, CancellationToken ct = default)
        {
            var existingIdx = Versions.FindLastIndex(v =>
                string.Equals(v.TenantId, tariff.TenantId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.DataSource, tariff.DataSource, StringComparison.OrdinalIgnoreCase));

            int ver = existingIdx >= 0 ? Versions[existingIdx].Version + 1 : 1;
            if (existingIdx >= 0)
            {
                Versions[existingIdx] = Versions[existingIdx] with { ValidUntil = tariff.ValidFrom };
            }

            _nextVersion = Math.Max(_nextVersion, ver);
            var created = tariff with { Version = ver };
            Versions.Add(created);
            return Task.FromResult(created);
        }

        public Task AccumulateAsync(UsageGenerationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default) => Task.FromResult(new UsageReportPage([], null));
        public Task<int> PurgeAsync(DateOnly cutoffExclusive, int batchSize = 10_000, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> EraseUserAsync(TenantId tenant, string userKey, CancellationToken ct = default) => Task.FromResult(0);
        public Task<UsageGrantRefRecord?> GetGrantRefAsync(TenantId tenant, string grantId, CancellationToken ct = default) => Task.FromResult<UsageGrantRefRecord?>(null);
        public Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConsumerAppDailySummary>>([]);
        public Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task<UsageQuotaTenantSnapshot> GetQuotaSnapshotAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) => Task.FromResult(UsageQuotaTenantSnapshot.Empty);
        public Task<IReadOnlyList<TenantId>> GetActiveUsageTenantsAsync(DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TenantId>>([]);
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) { Entries.Add(entry); return Task.CompletedTask; }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
