using System.Data;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

/// <summary>
/// Set-based protection and container reads (protection snapshot prefetch): tenant-scoped, bound ids only, chunked, and per table the same
/// result as the per-table reads.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class GovernanceDomainStoreSqlTests
{
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetProtectionBatchAsync_BindsTenantAndIds_InChunks_AndMergesRowsPerTable(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var ids = Enumerable.Range(0, 501).Select(static _ => Guid.NewGuid()).ToList();
        var rows = new DataTable();
        rows.Columns.Add("table_id", typeof(string));
        rows.Columns.Add("column_name", typeof(string));
        rows.Columns.Add("protection_class", typeof(string));
        rows.Columns.Add("mask_category", typeof(string));
        rows.Rows.Add(ids[0].ToString("D"), "", "INTERNAL", DBNull.Value);
        rows.Rows.Add(ids[0].ToString("D"), "salary", "SECRET", "PII_SALARY");
        rows.Rows.Add(ids[0].ToString("D"), "email", DBNull.Value, "PUBLIC");
        conn.EnqueueReader(rows);

        var result = await store.GetProtectionBatchAsync(TenantA, ids);

        var batchCommands = conn.Commands.Where(static c => c.CommandText.StartsWith(GovernanceDomainStore.GetProtectionBatchSqlPrefix, StringComparison.Ordinal)).ToList();
        batchCommands.Count.ShouldBe(2);
        batchCommands.ShouldAllBe(c => c.CommandText.EndsWith(GovernanceDomainStore.GetProtectionBatchSqlSuffix, StringComparison.Ordinal));
        batchCommands.ShouldAllBe(c => c.Parameters.Any(p => p.Name == "@tenantId" && Equals(p.Value, TenantA.Value)));
        batchCommands.Sum(static c => c.Parameters.Count(static p => p.Name.StartsWith("@tid", StringComparison.Ordinal))).ShouldBe(501);
        batchCommands.ShouldAllBe(c => !c.CommandText.Contains('\''));

        result.Count.ShouldBe(501);
        result[ids[0]].Table.ShouldBe(new ObjectProtection(ProtectionClass.Internal, null));
        result[ids[0]].Columns["SALARY"].ShouldBe(new ObjectProtection(ProtectionClass.Secret, "PII_SALARY"));
        result[ids[0]].Columns["email"].MaskCategory.ShouldBeNull();
        result[ids[1]].Table.ShouldBeNull();
        result[ids[1]].Columns.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetContainersForTablesAsync_MatchesThePerTableResolution(string dialectName)
    {
        var (store, conn) = CreateStore(dialectName);
        var assigned = Guid.NewGuid();
        var orphaned = Guid.NewGuid();
        var unassigned = Guid.NewGuid();
        var domainId = Guid.NewGuid();

        var assignments = new DataTable();
        assignments.Columns.Add("table_id", typeof(string));
        assignments.Columns.Add("id", typeof(string));
        assignments.Columns.Add("owner_group_sid", typeof(string));
        assignments.Columns.Add("default_class", typeof(string));
        assignments.Columns.Add("visibility", typeof(string));
        assignments.Rows.Add(assigned.ToString("D"), domainId.ToString("D"), Actor.Value, "SECRET", "OPEN");
        assignments.Rows.Add(orphaned.ToString("D"), DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        conn.EnqueueReader(assignments);

        var unassignedRow = new DataTable();
        unassignedRow.Columns.Add("id", typeof(string));
        unassignedRow.Columns.Add("owner_group_sid", typeof(string));
        unassignedRow.Columns.Add("default_class", typeof(string));
        unassignedRow.Columns.Add("visibility", typeof(string));
        unassignedRow.Rows.Add(Guid.Empty.ToString("D"), DBNull.Value, "CONFIDENTIAL", "ON_REQUEST");
        conn.EnqueueReader(unassignedRow);

        var result = await store.GetContainersForTablesAsync(TenantA, [assigned, orphaned, unassigned]);

        result[assigned].Container.ShouldBe(new ContainerRef(ContainerKind.Domain, domainId));
        result[assigned].DefaultClass.ShouldBe(ProtectionClass.Secret);
        result[assigned].Visibility.ShouldBe(CatalogVisibilityMode.Open);
        result[orphaned].ShouldBe(ContainerDefaults.Unassigned);
        result[unassigned].Container.ShouldBe(new ContainerRef(ContainerKind.Domain, Guid.Empty));
        result[unassigned].IsValid.ShouldBeTrue();

        conn.Commands.ShouldContain(c => c.CommandText == GovernanceDomainStore.GetUnassignedContainerSql
            && c.Parameters.Any(p => p.Name == "@tenantId" && Equals(p.Value, TenantA.Value)));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Batch_reads_reject_invalid_tenants_and_empty_ids(string dialectName)
    {
        var (store, _) = CreateStore(dialectName);

        await Should.ThrowAsync<ArgumentException>(() => store.GetProtectionBatchAsync(default, [Guid.NewGuid()]));
        await Should.ThrowAsync<ArgumentException>(() => store.GetContainersForTablesAsync(TenantId.Platform, [Guid.NewGuid()]));
        await Should.ThrowAsync<ArgumentException>(() => store.GetProtectionBatchAsync(TenantA, [Guid.Empty]));
    }
}
