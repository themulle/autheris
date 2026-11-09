namespace Autheris.Tests.Unit.Sql;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.GraphQL;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>DML authorization: every write target is denied until a dedicated write decision exists; policy dependency tables are refused first.</summary>
[Trait("Category", "Security")]
public sealed class WebSqlDmlGuardTests
{
    private static ClaimsPrincipal Principal(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-DML-WRITER"), new("tenant_id", BypassSemanticsAndDmlGuardrailTests.Tenant) };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static Task Run(GovernedSqlExecutionService service, ClaimsPrincipal user, string sql) =>
        service.ExecuteGovernedQueryAsync(new GovernedSqlQueryRequest(sql), user, new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant), (_, _) => Task.CompletedTask);

    [Theory]
    [InlineData("WebSqlWriter")]
    [InlineData("WebSqlWriter", "ClusterAdmin")]
    [InlineData("WebSqlWriter", "GovernanceAdmin")]
    public async Task SEC_36_SP3a_01_Dml_is_refused_for_every_role_while_no_write_decision_exists(params string[] roles)
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, permissiveWrite: false);

        await Should.ThrowAsync<WebSqlTableDeniedException>(() => Run(service, Principal(roles), "DELETE FROM orders WHERE id = 1"));

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
    }

    [Fact]
    public async Task SEC_36_SP3a_Dml_on_row_filter_dependency_table_is_refused()
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, permissiveWrite: false, withDependency: true);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => Run(service, Principal("WebSqlWriter"), "DELETE FROM consents WHERE id = 1"));

        // The dependency refusal carries its own code, so the later deny-all decision cannot mask a broken guard.
        ex.ShouldNotBeOfType<WebSqlTableDeniedException>();
        ex.Code.ShouldBe(GovernedSqlErrorCodes.PolicyViolation);
    }

    [Fact]
    public async Task SEC_36_ORDER_02_Dependency_refusal_holds_against_a_permissive_write_decision()
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, permissiveWrite: true, withDependency: true);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => Run(service, Principal("WebSqlWriter"), "DELETE FROM consents WHERE id = 1"));

        ex.Code.ShouldBe(GovernedSqlErrorCodes.PolicyViolation);
    }

    [Fact]
    public void SEC_36_ORDER_02_Dependency_guard_matches_by_resolved_table_only()
    {
        var policies = new[] { SecuredTreeHarness.ExistsPolicy() };

        DmlPolicyDependencyGuard.IsReadByRowPolicy(policies, new TableIdentifier("default", "main", "consents"), "default").ShouldBeTrue();
        DmlPolicyDependencyGuard.IsReadByRowPolicy(policies, new TableIdentifier("default", "main", "orders"), "default").ShouldBeFalse();
        DmlPolicyDependencyGuard.IsReadByRowPolicy([null], new TableIdentifier("default", "main", "consents"), "default").ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_36_SP3a_Dependency_held_by_a_table_without_columns_is_still_refused()
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, permissiveWrite: true, columnlessHolder: true);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() => Run(service, Principal("WebSqlWriter"), "DELETE FROM consents WHERE id = 1"));

        ex.Code.ShouldBe(GovernedSqlErrorCodes.PolicyViolation);
    }

    [Fact]
    public async Task SEC_36_ORDER_01_Tenant_column_refusal_wins_over_the_write_denial()
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, permissiveWrite: false, plainTable: true, requireTenantColumn: true);

        var ex = await Should.ThrowAsync<System.Exception>(() => Run(service, Principal("WebSqlWriter"), "DELETE FROM plain WHERE id = 1"));

        ex.ShouldNotBeOfType<WebSqlTableDeniedException>();
    }
}
