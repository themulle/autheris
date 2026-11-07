using Autheris.Application.Common.Interfaces;
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

    private static IHostEnvironment TestEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Test");
        return env;
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
        repository.RecordAuditEventAsync(new AuditLogEntry { TenantId = Autheris.Tests.Common.TestTenants.Default, EventType = "ADMIN_ACTION", ActorSid = new Sid("S-1-5-21-A"), TargetTable = "t" });
}
