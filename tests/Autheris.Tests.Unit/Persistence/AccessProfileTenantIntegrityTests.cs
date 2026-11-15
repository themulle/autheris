using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common.Fakes;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

/// <summary>L14 / DEF-SC-14: an unparsable stored profile tenant never becomes the caller tenant; it fails closed with a governed code and no echo.</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileTenantIntegrityTests
{
    private static StubDbDataReader ReaderFor(string tenantColumn)
    {
        var columns = new[]
        {
            "profile_id", "tenant_id", "name", "masking_mode",
            "target_tables", "row_scope", "justification", "created_by",
            "created_at", "valid_to"
        };
        var row = new object?[]
        {
            "p1", tenantColumn, "name", "Strict",
            null, null, null, null,
            "2026-01-01T00:00:00+00:00", null
        };
        var reader = new StubDbDataReader(columns, [row]);
        reader.Read().ShouldBeTrue();
        return reader;
    }

    [Fact]
    public void AccessProfile_Invalid_Stored_Tenant_Fails_Closed()
    {
        var store = new AccessProfileStore(AccessProfileDialect.PostgreSql, (_, _) => throw new InvalidOperationException("not used"));

        using var bad = ReaderFor("bad tenant!");
        var ex = Should.Throw<AccessProfileTenantIntegrityException>(() => store.ReadProfile(bad, 0));

        ex.ErrorCode.ShouldBe("ACCESS_PROFILE_TENANT_INVALID");
        ex.Message.ShouldNotContain("bad tenant!");
        ex.InnerException.ShouldBeNull();

        using var good = ReaderFor("tenant-a");
        store.ReadProfile(good, 0).TenantId.ShouldBe(new TenantId("tenant-a"));
    }
}
