using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;
using NSubstitute;
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
    public void Tenant_scoped_statement_without_tenant_throws(string table)
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

    [Fact]
    public void Global_table_allowlist_is_exact()
    {
        // SEC-SIMPL-22: pins the literal set; adding a table is a deliberate, reviewed change.
        GovernanceGlobalTables.All.OrderBy(t => t, StringComparer.Ordinal).ShouldBe(
        [
            "AUDIT_LOG_TRUNCATE_GUARD", "CATALOG_STATE", "COLUMN_MASKING_RULES", "DATA_OWNERS", "DATA_OWNER_DELEGATIONS",
            "POLICY_EPOCHS", "ROLES", "ROLE_MEMBERS", "TABLES", "TABLE_CATALOG_SETTINGS", "TABLE_COLUMNS", "TABLE_INDEXES",
            "TABLE_INDEX_COLUMNS", "TABLE_OWNERS", "TABLE_PRIMARY_KEY_COLUMNS", "TABLE_RELATIONS", "TABLE_RELATION_KEYS",
            "TABLE_ROW_FILTER_STRATEGIES", "TABLE_TAGS", "TRUSTED_DOCUMENT_APPS", "TRUSTED_DOCUMENT_APP_ORIGINS",
            "TRUSTED_DOCUMENT_APP_VERSIONS", "TRUSTED_DOCUMENT_GENERATION", "TRUSTED_DOCUMENT_HASHES",
        ]);
    }

    [Fact]
    public void Global_tables_have_no_tenant_id_column_in_baseline_schema()
    {
        // SEC-SIMPL-22: a table with a tenant_id column must never be allowlisted as global.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull();
        string Schema(string file) => File.ReadAllText(Path.Combine(dir.FullName, "src", "Autheris.Infrastructure", "Persistence", "Migrations", file));
        var tenantTables = new[] { Schema("PostgreSqlBaselineSchema.cs"), Schema("SqlServerBaselineSchema.cs") }
            .SelectMany(ddl => Regex.Matches(ddl, @"CREATE TABLE (?:IF NOT EXISTS )?(?:dbo\.)?""?(\w+)""?\s*\((.*?)\n\s*\);", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            .Where(m => Regex.IsMatch(m.Groups[2].Value, @"\btenant_id\b", RegexOptions.IgnoreCase))
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .ToList();

        tenantTables.ShouldNotBeEmpty("the DDL scan must find tenant-scoped tables (negative control)");
        GovernanceGlobalTables.All.Where(t => tenantTables.Contains(t, StringComparer.OrdinalIgnoreCase)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Consent_reads_with_null_tenant_throw()
    {
        // SEC-SIMPL-22: tenant-typed consent reads never drop the tenant predicate.
        var repo = new GovernanceRepository(Substitute.For<IGovernanceSqlDialect>());
        var table = new TableIdentifier("d", "s", "t");
        var subjects = new[] { new Sid("S-1") };

        await Should.ThrowAsync<ArgumentException>(async () => await repo.GetActiveConsentsForSubjectsAsync(subjects, table, DateTimeOffset.UtcNow, (TenantId?)null));
        await Should.ThrowAsync<ArgumentException>(async () => await repo.GetAllActiveConsentsForSubjectsAsync(subjects, null, null, (TenantId?)null));
        await Should.ThrowAsync<ArgumentException>(async () => await repo.GetActiveRoleConsentsAsync(table, "role", (TenantId?)null, DateTimeOffset.UtcNow));
    }
}
