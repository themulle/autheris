namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    private const string WriteScopeSql = "DELETE FROM orders WHERE amount = 10";

    // GHSA-83g2-c2jr-c8vq: DML needs an explicit scope on every token (SEC-36-SP3c-01, SEC-36-ORDER-01/02).
    [Fact]
    public async Task Dml_TokenWithoutWriteScope_IsRejected()
    {
        // A permissive write decision makes the scope guard observable (it runs before the write decision).
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, allowDml: true, permissiveWrite: true);
        var tenant = new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.ExecuteGovernedQueryAsync(new GovernedSqlQueryRequest(WriteScopeSql), BypassSemanticsAndDmlGuardrailTests.CreateWriter(withWriteScope: false), tenant, (_, _) => Task.CompletedTask));
        ex.Code.ShouldBe(WebSqlPolicyException.ReadOnlyTokenCode);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
    }

    [Fact]
    public async Task Dml_ReadOnlyMarkedPrincipalWithWriteScope_IsRejected()
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, allowDml: true, permissiveWrite: true);
        var writer = BypassSemanticsAndDmlGuardrailTests.CreateWriter();
        TokenAccessScope.MarkReadOnly((ClaimsIdentity)writer.Identity!);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.ExecuteGovernedQueryAsync(new GovernedSqlQueryRequest(WriteScopeSql), writer, new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant), (_, _) => Task.CompletedTask));
        ex.Code.ShouldBe(WebSqlPolicyException.ReadOnlyTokenCode);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
    }

    [Fact]
    public async Task Dml_WriteScope_DoesNotBypassTheWriteDecision()
    {
        // With the scope present, the later deny-all write decision still applies (distinct refusal).
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, allowDml: true, permissiveWrite: false, withDependency: true);

        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.ExecuteGovernedQueryAsync(new GovernedSqlQueryRequest(WriteScopeSql), BypassSemanticsAndDmlGuardrailTests.CreateWriter(), new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant), (_, _) => Task.CompletedTask));
        ex.Code.ShouldNotBe(WebSqlPolicyException.ReadOnlyTokenCode);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
    }

    [Fact]
    public void WriteScope_IsACapability_NotADataScope()
    {
        var principal = BypassSemanticsAndDmlGuardrailTests.CreateWriter();

        principal.HasScope(TokenAccessScope.WriteScope).ShouldBeTrue();
        TokenAccessScope.GetScopeSet(principal).ShouldBeEmpty();
        TokenAccessScope.GetScopeSet(principal.Claims).ShouldBeEmpty();
    }
}
