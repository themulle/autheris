namespace Autheris.Tests.Unit.Sql;

using System;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Tests.Unit.Governance;
using Shouldly;
using Xunit;

/// <summary>
/// The data-source scoped dry run of the governed SQL pipeline (query analysis): it gives the verdict execution gives and has none of
/// execution's side effects (connection, cost admission, quota, failure fee, deny audit).
/// </summary>
public sealed class GovernedSqlValidateParityTests
{
    private const string DataSource = SqlCompileParityHarness.DataSource;
    private static readonly TenantId Tenant = SqlCompileParityHarness.TenantA;

    private static SqlCompileParityHarness Create() => new(consents: SqlCompileParityHarness.AllowConsent());

    [Theory]
    [InlineData("SELECT pg_sleep(1) FROM demo.main.t")]
    [InlineData("SELECT id FROM demo.main.t TABLESAMPLE BERNOULLI (10)")]
    [InlineData("SELEC id FROM demo.main.t")]
    public async Task Validate_WithDataSource_RejectsWhatExecutionRejects(string sql)
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        var validate = await Should.ThrowAsync<Exception>(() => h.Governed.ValidateAsync(sql, user, Tenant, DataSource));
        var execute = await Should.ThrowAsync<Exception>(() => h.Governed.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest(sql, DataSourceName: DataSource, Transport: SqlTransports.WebSql), user, Tenant));

        validate.GetType().ShouldBe(execute.GetType());
        SqlCompilerErrorMapper.TryMap(validate, out var validateMapped).ShouldBeTrue();
        SqlCompilerErrorMapper.TryMap(execute, out var executeMapped).ShouldBeTrue();
        validateMapped.ShouldBe(executeMapped);
    }

    [Fact]
    public async Task Validate_WithDataSource_AcceptsAValidStatement()
    {
        var h = Create();

        await h.Governed.ValidateAsync("SELECT id, amount FROM demo.main.t", SqlCompileParityHarness.Caller(), Tenant, DataSource);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Validate_OpensNoConnection()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        await h.Governed.ValidateAsync("SELECT id FROM demo.main.t", user, Tenant, DataSource);
        await Should.ThrowAsync<Exception>(() => h.Governed.ValidateAsync("SELECT pg_sleep(1) FROM demo.main.t", user, Tenant, DataSource));

        h.Connections.CreateCalls.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Validate_WritesNoAudit()
    {
        var h = Create();
        var user = SqlCompileParityHarness.Caller();

        await h.Governed.ValidateAsync("SELECT id FROM demo.main.t", user, Tenant, DataSource);
        await Should.ThrowAsync<Exception>(() => h.Governed.ValidateAsync("SELECT pg_sleep(1) FROM demo.main.t", user, Tenant, DataSource));
        await Should.ThrowAsync<Exception>(() => h.Governed.ValidateAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller("S-1-STRANGER"), Tenant, DataSource));

        h.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Validate_DoesNotAdmitCost()
    {
        var h = Create();

        await h.Governed.ValidateAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller(), Tenant, DataSource);

        h.CostQuota.Calls.ShouldNotContain("AdmitAsync");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Validate_NoQuotaOrFeeCharged()
    {
        var h = Create();

        await Should.ThrowAsync<Exception>(() => h.Governed.ValidateAsync("SELECT pg_sleep(1) FROM demo.main.t", SqlCompileParityHarness.Caller(), Tenant, DataSource));

        h.CostQuota.Calls.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Validate_UsesTargetPrincipalAndTenant()
    {
        // only the target has a consent: the statement is accepted for it and refused for the other principal of the same tenant
        var h = new SqlCompileParityHarness(consents: SqlCompileParityHarness.AllowConsent("S-1-TARGET"));

        await h.Governed.ValidateAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller("S-1-TARGET"), Tenant, DataSource);
        await Should.ThrowAsync<Exception>(() => h.Governed.ValidateAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller("S-1-USER"), Tenant, DataSource));
        // another tenant sees none of the consents of this one
        await Should.ThrowAsync<Exception>(() => h.Governed.ValidateAsync("SELECT id FROM demo.main.t", SqlCompileParityHarness.Caller("S-1-TARGET", "tenant-b"), new TenantId("tenant-b"), DataSource));
    }
}
