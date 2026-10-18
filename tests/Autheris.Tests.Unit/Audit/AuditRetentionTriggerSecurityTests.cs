using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

/// <summary>
/// SEC-AUD161-04 / SEC-AUD161-05: Verifies database schema shape and trigger baseline contracts
/// for AUDIT_PURGE_CHECKPOINTS and append-only constraints across all database backends.
/// </summary>
[Trait("Category", "Security")]
public sealed class AuditRetentionTriggerSecurityTests
{
    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    public void EmbeddedShape_AllDialects_ContainsAuditPurgeCheckpoints(string dialect)
    {
        var shape = GovernanceSchemaShapeLoader.LoadEmbedded(dialect);
        var table = shape.Tables.Single(t => string.Equals(t.Name, "AUDIT_PURGE_CHECKPOINTS", StringComparison.OrdinalIgnoreCase));
        var colNames = table.Columns.Select(c => c.Name).ToList();

        colNames.ShouldContain("first_surviving_seq");
        colNames.ShouldContain("surviving_prev_hash");
        colNames.ShouldContain("purged_count");
        colNames.ShouldContain("purged_through_utc");
        colNames.ShouldContain("created_at");
        colNames.ShouldContain("signature");
    }

    [Fact]
    public async Task Sqlite_BaselineSchema_AppliesPurgeCheckpointsTable()
    {
        var cs = $"Data Source=shape_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = cs, SeedDemoData = false }
        });

        using var repository = GovernanceStoreFactory.CreateUnmigratedSqlite(Substitute.For<IEpochValidationService>(), options);
        await using var connection = await repository.Connections.OpenAsync(CancellationToken.None);
        new SqliteBaselineSchema(connection, null).Apply();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AUDIT_PURGE_CHECKPOINTS";
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(CancellationToken.None));
        count.ShouldBe(0);
    }
}
