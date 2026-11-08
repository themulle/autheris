using System;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class GovernanceTenantScopeTests
{
    [Fact]
    public void Global_tables_allow_null_or_empty_tenant()
    {
        foreach (var table in GovernanceGlobalTables.All)
        {
            var result = TenantScope.RequireTenant((TenantId?)null, table);
            result.ShouldBe(default(TenantId));

            var resultStruct = TenantScope.RequireTenant(default(TenantId), table);
            resultStruct.ShouldBe(default(TenantId));

            var resultStr = TenantScope.RequireTenantString(null, table);
            resultStr.ShouldBe(string.Empty);
        }
    }

    [Theory]
    [InlineData("CONSENTS")]
    [InlineData("CONSENT_REQUESTS")]
    [InlineData("CONSENT_ROW_FILTERS")]
    [InlineData("CONSENT_COLUMN_RULES")]
    [InlineData("AUDIT_LOG")]
    public void Tenant_scoped_tables_throw_when_tenant_is_missing(string table)
    {
        Should.Throw<ArgumentException>(() => TenantScope.RequireTenant((TenantId?)null, table))
            .Message.ShouldContain($"TenantId is required for tenant-scoped table '{table}'");

        Should.Throw<ArgumentException>(() => TenantScope.RequireTenant(default(TenantId), table))
            .Message.ShouldContain($"TenantId is required for tenant-scoped table '{table}'");

        Should.Throw<ArgumentException>(() => TenantScope.RequireTenantString(null, table))
            .Message.ShouldContain($"TenantId is required for tenant-scoped table '{table}'");

        Should.Throw<ArgumentException>(() => TenantScope.RequireTenantString("   ", table))
            .Message.ShouldContain($"TenantId is required for tenant-scoped table '{table}'");
    }

    [Theory]
    [InlineData("CONSENTS")]
    [InlineData("CONSENT_REQUESTS")]
    [InlineData("AUDIT_LOG")]
    public void Tenant_scoped_tables_return_valid_tenant(string table)
    {
        var tenant = new TenantId("tenant-alpha");
        var resolved = TenantScope.RequireTenant(tenant, table);
        resolved.ShouldBe(tenant);

        var resolvedNullable = TenantScope.RequireTenant((TenantId?)tenant, table);
        resolvedNullable.ShouldBe(tenant);

        var resolvedStr = TenantScope.RequireTenantString("tenant-alpha", table);
        resolvedStr.ShouldBe("tenant-alpha");
    }

    [Fact]
    public void IsGlobal_is_case_insensitive()
    {
        GovernanceGlobalTables.IsGlobal("tables").ShouldBeTrue();
        GovernanceGlobalTables.IsGlobal("TABLES").ShouldBeTrue();
        GovernanceGlobalTables.IsGlobal("roles").ShouldBeTrue();
        GovernanceGlobalTables.IsGlobal("ROLES").ShouldBeTrue();
        GovernanceGlobalTables.IsGlobal("consents").ShouldBeFalse();
        GovernanceGlobalTables.IsGlobal("CONSENTS").ShouldBeFalse();
    }
}
