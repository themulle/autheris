using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

/// <summary>L14 / DEF-SC-14: an unparsable stored profile tenant never becomes the caller tenant; it fails closed with a governed code and no echo.</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileTenantIntegrityTests
{
    private static async Task<SqliteDataReader> ReaderForAsync(SqliteConnection conn, string tenantColumn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 'p1', @t, 'name', 'Strict', NULL, NULL, NULL, NULL, '2026-01-01T00:00:00+00:00', NULL";
        cmd.Parameters.AddWithValue("@t", tenantColumn);
        var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        return reader;
    }

    [Fact]
    public async Task AccessProfile_Invalid_Stored_Tenant_Fails_Closed()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var store = new AccessProfileStore(AccessProfileDialect.Sqlite, (_, _) => throw new InvalidOperationException("not used"));

        await using var bad = await ReaderForAsync(conn, "bad tenant!");
        var ex = Should.Throw<AccessProfileTenantIntegrityException>(() => store.ReadProfile(bad, 0));

        ex.ErrorCode.ShouldBe("ACCESS_PROFILE_TENANT_INVALID");
        ex.Message.ShouldNotContain("bad tenant!");
        ex.InnerException.ShouldBeNull();

        await using var good = await ReaderForAsync(conn, "tenant-a");
        store.ReadProfile(good, 0).TenantId.ShouldBe(new TenantId("tenant-a"));
    }
}
