using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

[Trait("Category", "Security")]
public sealed class AccessProfileStoreSqlTests
{
    [Fact]
    public void NewProfileItemSql_IsFixedTextWithTenantFilter()
    {
        string[] reads = [AccessProfileStore.ItemsSelectSql, AccessProfileStore.LevelsSelectSql];
        string[] deletes = [AccessProfileStore.ItemsDeleteSql, AccessProfileStore.LevelsDeleteSql];
        string[] inserts = [AccessProfileStore.ItemInsertSql, AccessProfileStore.LevelInsertSql];

        foreach (var sql in reads)
        {
            sql.ShouldContain("tenant_id = @tenantId AND profile_id IN ({0})");
        }

        foreach (var sql in deletes)
        {
            sql.ShouldEndWith("WHERE tenant_id = @tenantId AND profile_id = @profileId");
        }

        foreach (var sql in inserts)
        {
            sql.ShouldContain("(tenant_id, profile_id,");
            sql.ShouldContain("VALUES (@tenantId, @profileId,");
        }

        // Fixed text: the only placeholder is the bound-parameter list of an IN clause; no value is ever concatenated.
        foreach (var sql in reads.Concat(deletes).Concat(inserts))
        {
            sql.ShouldNotContain("'");
            sql.ShouldNotContain("+");
            sql.ShouldNotContain("$");
        }
    }
}
