namespace Autheris.Tests.Integration.Governance;

using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-12 on PostgreSQL: a governance store used before migration and initialization completed fails closed (replaces the former SQLite cases).</summary>
[Trait("Category", "Security")]
public sealed class PostgreSqlGovernanceStoreReadinessTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static IOptions<GatewayOptions> Gateway(string connectionString) => Options.Create(new GatewayOptions
    {
        GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = connectionString, SeedDemoData = false }
    });

    public static TheoryData<string> Operations() => new()
    {
        "GetTableMetadata",
        "GetConsentRequest",
        "ApproveConsentRequestStep",
        "LoadSnapshot",
        "TenantTryGet",
        "RecordAuditTierA",
        "RecordAuditTierB",
        "WebhookOutboxClaim",
        "WebhookOutboxCount",
        "CdcWatermarkGet",
        "CdcWatermarkAdvance",
    };

    private static Task Invoke(PostgreSqlGovernanceRepository repository, string operation) => operation switch
    {
        "GetTableMetadata" => repository.GetTableMetadataAsync(new TableIdentifier("default", "public", "orders")),
        "GetConsentRequest" => repository.GetConsentRequestAsync(Guid.NewGuid()),
        "ApproveConsentRequestStep" => repository.ApproveConsentRequestStepAsync(Guid.NewGuid(), new Sid("S-1-5-21-APPROVER")),
        "LoadSnapshot" => repository.LoadSnapshotAsync(),
        "TenantTryGet" => repository.TryGetAsync(Autheris.Tests.Common.TestTenants.Default, CancellationToken.None).AsTask(),
        "RecordAuditTierA" => repository.RecordAuditEventAsync(new AuditLogEntry { TenantId = Autheris.Tests.Common.TestTenants.Default, EventType = "ADMIN_ACTION", ActorSid = new Sid("S-1-5-21-A"), TargetTable = "t" }),
        "RecordAuditTierB" => repository.RecordAuditEventAsync(new AuditLogEntry { TenantId = Autheris.Tests.Common.TestTenants.Default, EventType = AuditEventTypes.TableQuery, ActorSid = new Sid("S-1-5-21-A"), TargetTable = "t" }),
        "WebhookOutboxClaim" => ((Autheris.Application.Webhooks.IWebhookOutbox)repository).ClaimBatchAsync(5, FixedNow, TimeSpan.FromMinutes(1), "worker"),
        "WebhookOutboxCount" => ((Autheris.Application.Webhooks.IWebhookOutbox)repository).CountPendingAsync(),
        "CdcWatermarkGet" => ((ICdcWatermarkRepository)repository).GetAsync("cdc", "d", "s", "t"),
        "CdcWatermarkAdvance" => ((ICdcWatermarkRepository)repository).TryAdvanceAsync("cdc", "d", "s", "t", 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task PostgreSql_Repository_UsedBeforeInitialize_ThrowsNotReady(string operation)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(Gateway(db.ConnectionString));

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke(repository, operation));
    }

    [Fact]
    public async Task PostgreSql_TierBAuditBeforeInitialize_IsRejectedNotQueued()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(Gateway(db.ConnectionString));

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke(repository, "RecordAuditTierB"));
    }

    [Fact]
    public async Task PostgreSql_PingBeforeInitialize_ReturnsFalse_AndDoesNotInitialize()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(Gateway(db.ConnectionString));

        (await repository.PingAsync()).ShouldBeFalse();

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke(repository, "GetTableMetadata"));
    }

    [Fact]
    public async Task PostgreSql_AfterMigrationAndInitialize_IsReady()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var options = Gateway(db.ConnectionString);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);
        await new PostgreSqlGovernanceMigrationRunner(repository, options).RunAsync();
        await repository.InitializeAsync();

        (await repository.PingAsync()).ShouldBeTrue();
        (await repository.GetConsentRequestAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task PostgreSql_MigrationRunner_WorksBeforeInitialize()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var options = Gateway(db.ConnectionString);
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(options);

        await Should.NotThrowAsync(() => new PostgreSqlGovernanceMigrationRunner(repository, options).RunAsync());
    }

    [Fact]
    public async Task PostgreSql_FailedInitialize_StaysNotReady()
    {
        // The schema was never migrated: initialization fails and the store must stay closed.
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        using var repository = GovernanceSchemaTestSupport.UnmigratedPostgreSql(Gateway(db.ConnectionString));

        await Should.ThrowAsync<Exception>(() => repository.InitializeAsync());

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke(repository, "GetTableMetadata"));
        (await repository.PingAsync()).ShouldBeFalse();
    }
}
