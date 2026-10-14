using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>WP-ARC-B-12: a governance store used before migration and initialization completed fails closed.</summary>
[Trait("Category", "Security")]
public sealed class GovernanceStoreReadinessTests
{
    private static GatewayOptions Gateway(string connectionString) => new()
    {
        GovernanceDb = new GovernanceDbOptions { ConnectionString = connectionString, SeedDemoData = false }
    };

    private static string NewMemoryConnectionString() => $"Data Source=ready_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    private static SqliteGovernanceRepository NewRawSqlite(string? connectionString = null) =>
        GovernanceStoreFactory.CreateUnmigratedSqlite(Substitute.For<IEpochValidationService>(), Options.Create(Gateway(connectionString ?? NewMemoryConnectionString())));

    private static IHostEnvironment TestEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return env;
    }

    public static TheoryData<string> SqliteOperations() => new()
    {
        "GetTableMetadata",
        "GetConsentRequest",
        "ApproveConsentRequestStep",
        "LoadSnapshot",
        "TenantTryGet",
        "RecordAuditTierA",
        "RecordAuditTierB",
    };

    private static Task Invoke(SqliteGovernanceRepository repository, string operation) => operation switch
    {
        "GetTableMetadata" => repository.GetTableMetadataAsync(new TableIdentifier("default", "public", "orders")),
        "GetConsentRequest" => repository.GetConsentRequestAsync(Guid.NewGuid()),
        "ApproveConsentRequestStep" => repository.ApproveConsentRequestStepAsync(Guid.NewGuid(), new Sid("S-1-5-21-APPROVER")),
        "LoadSnapshot" => repository.LoadSnapshotAsync(),
        "TenantTryGet" => repository.TryGetAsync(TenantId.LegacySingleTenant, CancellationToken.None).AsTask(),
        "RecordAuditTierA" => repository.RecordAuditEventAsync(new AuditLogEntry { EventType = "ADMIN_ACTION", ActorSid = new Sid("S-1-5-21-A"), TargetTable = "t" }),
        "RecordAuditTierB" => repository.RecordAuditEventAsync(new AuditLogEntry { EventType = AuditEventTypes.TableQuery, ActorSid = new Sid("S-1-5-21-A"), TargetTable = "t" }),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    [Theory]
    [MemberData(nameof(SqliteOperations))]
    public async Task Sqlite_Repository_UsedBeforeInitialize_ThrowsNotReady(string operation)
    {
        using var repository = NewRawSqlite();

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke(repository, operation));
    }

    [Fact]
    public async Task Sqlite_TierBAuditBeforeInitialize_IsRejectedNotQueued()
    {
        using var repository = NewRawSqlite();

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke(repository, "RecordAuditTierB"));
    }

    [Fact]
    public async Task Sqlite_PingBeforeInitialize_ReturnsFalse_AndDoesNotInitialize()
    {
        using var repository = NewRawSqlite();

        (await repository.PingAsync()).ShouldBeFalse();

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke(repository, "GetTableMetadata"));
    }

    [Fact]
    public async Task Sqlite_AfterMigrationAndInitialize_IsReady()
    {
        var cs = NewMemoryConnectionString();
        using var repository = NewRawSqlite(cs);
        await new SqliteGovernanceMigrationRunner(repository, Options.Create(Gateway(cs))).RunAsync();
        await repository.InitializeAsync();

        (await repository.PingAsync()).ShouldBeTrue();
        (await repository.GetConsentRequestAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task Sqlite_MigrationRunner_WorksBeforeInitialize()
    {
        var cs = NewMemoryConnectionString();
        using var repository = NewRawSqlite(cs);

        await Should.NotThrowAsync(() => new SqliteGovernanceMigrationRunner(repository, Options.Create(Gateway(cs))).RunAsync());
    }

    [Fact]
    public async Task Sqlite_FailedInitialize_StaysNotReady()
    {
        // The schema was never migrated: initialization fails (no AUDIT_LOG_ENTRIES table) and the store must stay closed.
        using var repository = NewRawSqlite();

        await Should.ThrowAsync<Exception>(() => repository.InitializeAsync());

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke(repository, "GetTableMetadata"));
        (await repository.PingAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task PostgreSql_UsedBeforeInitialize_ThrowsNotReady_WithoutConnecting()
    {
        var options = Options.Create(Gateway("Host=203.0.113.1;Database=x;Username=u;Password=p;Timeout=1"));
        await using var repository = GovernanceStoreFactory.CreateUnmigratedPostgreSql(Substitute.For<IEpochValidationService>(), options, TestEnvironment());

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => repository.GetTableMetadataAsync(new TableIdentifier("default", "public", "orders")));
        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => repository.GetConsentRequestAsync(Guid.NewGuid()));
        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke2(repository));
        (await repository.PingAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task SqlServer_UsedBeforeInitialize_ThrowsNotReady_WithoutConnecting()
    {
        var options = Options.Create(Gateway("Server=203.0.113.1;Database=x;User Id=u;Password=p;TrustServerCertificate=true;Connect Timeout=1"));
        await using var repository = GovernanceStoreFactory.CreateUnmigratedSqlServer(Substitute.For<IEpochValidationService>(), options, TestEnvironment());

        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => repository.GetTableMetadataAsync(new TableIdentifier("default", "public", "orders")));
        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => repository.GetConsentRequestAsync(Guid.NewGuid()));
        await Should.ThrowAsync<GovernanceStoreNotReadyException>(() => Invoke2(repository));
        (await repository.PingAsync()).ShouldBeFalse();
    }

    private static Task Invoke2(IGovernanceRepository repository) =>
        repository.RecordAuditEventAsync(new AuditLogEntry { EventType = "ADMIN_ACTION", ActorSid = new Sid("S-1-5-21-A"), TargetTable = "t" });
}
