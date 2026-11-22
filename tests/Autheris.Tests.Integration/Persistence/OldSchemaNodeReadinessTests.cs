using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Infrastructure.Health;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Integration.Governance;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

[Trait("Category", "Security")]
public sealed class OldSchemaNodeReadinessTests
{
    [Fact]
    public async Task Older_node_fails_readiness()
    {
        await using var pgDb = await PostgreSqlTestDatabase.CreateAsync();
        if (!pgDb.IsAvailable) return;

        await GovernanceSchemaTestSupport.RunPostgreSqlAsync(pgDb.ConnectionString, Domain.Options.GovernanceMigrationMode.Apply);

        var dialect = new PostgreSqlGovernanceDialect(pgDb.ConnectionString);
        var purgeStore = new MigrationPurgeStore(dialect);
        var appliedMax = await purgeStore.GetAppliedSchemaMaxVersionAsync(CancellationToken.None);
        appliedMax.ShouldBeGreaterThanOrEqualTo(41);

        var olderBinaryCheck = new GovernanceSchemaVersionReadinessCheck(
            purgeStore.GetAppliedSchemaMaxVersionAsync,
            highestKnownVersion: appliedMax - 1,
            NullLogger<GovernanceSchemaVersionReadinessCheck>.Instance);

        var staleReport = await olderBinaryCheck.EvaluateAsync(CancellationToken.None);
        staleReport.IsHealthy.ShouldBeFalse();
        staleReport.Name.ShouldBe(GovernanceSchemaVersionReadinessCheck.ComponentName);

        var currentBinaryCheck = new GovernanceSchemaVersionReadinessCheck(
            purgeStore,
            dialect,
            NullLogger<GovernanceSchemaVersionReadinessCheck>.Instance);
        var currentReport = await currentBinaryCheck.EvaluateAsync(CancellationToken.None);
        currentReport.IsHealthy.ShouldBeTrue();
    }
}
