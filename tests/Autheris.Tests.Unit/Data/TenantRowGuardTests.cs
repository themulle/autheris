namespace Autheris.Tests.Unit.Data;

using System;
using System.Collections.Generic;
using Autheris.Application.Data;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

/// <summary>VF-03: the in-memory tenant check denies NULL, DBNull, missing and differently cased tenant values.</summary>
[Trait("Category", "Security")]
public sealed class TenantRowGuardTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static Dictionary<string, object?> Row(string key, object? value) =>
        new(StringComparer.OrdinalIgnoreCase) { [key] = value, ["id"] = 1 };

    [Fact]
    public void Matches_NullTenantValue_False() => TenantRowGuard.Matches(Row("tenant_id", null), "tenant_id", Tenant).ShouldBeFalse();

    [Fact]
    public void Matches_MissingColumn_False() => TenantRowGuard.Matches(Row("other", "tenant-a"), "tenant_id", Tenant).ShouldBeFalse();

    [Fact]
    public void Matches_DbNull_False() => TenantRowGuard.Matches(Row("tenant_id", DBNull.Value), "tenant_id", Tenant).ShouldBeFalse();

    [Fact]
    public void Matches_CaseDifferentTenant_False() => TenantRowGuard.Matches(Row("tenant_id", "TENANT-A"), "tenant_id", Tenant).ShouldBeFalse();

    [Fact]
    public void Matches_ExactTenant_True() => TenantRowGuard.Matches(Row("TENANT_ID", "tenant-a"), "tenant_id", Tenant).ShouldBeTrue();

    [Fact]
    public void Matches_NonTenantedTable_True() => TenantRowGuard.Matches(Row("x", null), null, Tenant).ShouldBeTrue();
}
