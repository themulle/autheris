using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Policy.Lint;
using Autheris.Infrastructure.Cache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy.Lint;

/// <summary>WP-X0x: the SQLite governance source feeds the lint end to end (written after the code: a guard, not a Red step).</summary>
public sealed class SqliteGovernanceLintSourceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"lint_source_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + ".audit-anchor.json", _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task LoadAsync_ReadsCatalogAndProfiles_AndTheLintFlagsTheBrokenOne()
    {
        var connection = $"Data Source={_path}";
        using (var repo = new SqliteGovernanceRepository(new EpochValidationService(),
                   Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = connection } })))
        {
            var table = new Table { SourceType = "PostgreSQL", SourceName = "erp", SchemaName = "main", TableName = "orders" };
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = table,
                Identifier = new TableIdentifier("erp", "main", "orders"),
                Columns = [new TableColumn { TableId = table.Id, ColumnName = "region", DataType = "varchar" }]
            });
            await repo.UpsertProfileAsync(new AccessProfile
            {
                ProfileId = "p-ok", Name = "ok", TenantId = TenantId.LegacySingleTenant, TargetTables = ["erp.main.orders"], RowFilterPredicate = "region = 'EU'", CreatedBy = "t"
            });
            await repo.UpsertProfileAsync(new AccessProfile
            {
                ProfileId = "p-bad", Name = "bad", TenantId = TenantId.LegacySingleTenant, TargetTables = ["erp.main.*"], RowFilterPredicate = "[region] = 'EU'", CreatedBy = "t"
            });
        }

        var input = await SqliteGovernanceLintSource.LoadAsync(connection);
        var report = new PolicyPredicateLinter().Lint(input);

        input.Catalog.Single().Identifier.ToString().ShouldBe("erp.main.orders");
        report.Checked.ShouldBe(2);
        report.Findings.Single().PolicyId.ShouldBe("p-bad");
    }
}
