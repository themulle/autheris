namespace Autheris.Tests.Unit.DataApi;

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class VirtualSystemTablesTests
{
    [Fact]
    public void VirtualSystemTables_DefinesAllFiveCanonicalEntities()
    {
        VirtualSystemTables.All.Count.ShouldBe(5);

        // 1. governance.system.datasources
        var ds = VirtualSystemTables.Get(new TableIdentifier("governance", "system", "datasources"));
        ds.ShouldNotBeNull();
        ds.Columns.Select(c => c.ColumnName).ShouldBe(new[]
        {
            "id", "name", "domain", "type", "base_url", "is_configured", "status", "created_at", "updated_at"
        });

        // 2. governance.system.policies
        var pol = VirtualSystemTables.Get(new TableIdentifier("governance", "system", "policies"));
        pol.ShouldNotBeNull();
        pol.Columns.Select(c => c.ColumnName).ShouldBe(new[]
        {
            "id", "table_id", "column_name", "sensitivity", "masking_type", "classification_tags"
        });

        // 3. governance.system.rebac_tuples
        var rebac = VirtualSystemTables.Get(new TableIdentifier("governance", "system", "rebac_tuples"));
        rebac.ShouldNotBeNull();
        rebac.Columns.Select(c => c.ColumnName).ShouldBe(new[]
        {
            "user", "relation", "object"
        });

        // 4. governance.system.virtual_filters
        var vf = VirtualSystemTables.Get(new TableIdentifier("governance", "system", "virtual_filters"));
        vf.ShouldNotBeNull();
        vf.Columns.Select(c => c.ColumnName).ShouldBe(new[]
        {
            "id", "table_id", "principal", "filter_expression", "valid_until"
        });

        // 5. governance.system.audit_trail
        var audit = VirtualSystemTables.Get(new TableIdentifier("governance", "system", "audit_trail"));
        audit.ShouldNotBeNull();
        audit.Columns.Select(c => c.ColumnName).ShouldBe(new[]
        {
            "id", "timestamp", "actor_sid", "channel", "action", "target", "correlation_id", "worm_signature"
        });
    }

    [Fact]
    public async Task RegisterSystemTablesAsync_UpsertsAllEntitiesToRepository()
    {
        var repo = Substitute.For<ITableMetadataRepository>();

        await VirtualSystemTables.RegisterSystemTablesAsync(repo, CancellationToken.None);

        await repo.Received(5).UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m => m.Identifier == VirtualSystemTables.DataSourcesId), Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m => m.Identifier == VirtualSystemTables.PoliciesId), Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m => m.Identifier == VirtualSystemTables.RebacTuplesId), Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m => m.Identifier == VirtualSystemTables.VirtualFiltersId), Arg.Any<CancellationToken>());
        await repo.Received(1).UpsertTableMetadataAsync(Arg.Is<TableMetadata>(m => m.Identifier == VirtualSystemTables.AuditTrailId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsSystemTable_DetectsGovernanceSystemDomain()
    {
        VirtualSystemTables.IsSystemTable(new TableIdentifier("governance", "system", "datasources")).ShouldBeTrue();
        VirtualSystemTables.IsSystemTable(new TableIdentifier("GOVERNANCE", "SYSTEM", "audit_trail")).ShouldBeTrue();
        VirtualSystemTables.IsSystemTable(new TableIdentifier("sales", "dbo", "customers")).ShouldBeFalse();
    }
}
