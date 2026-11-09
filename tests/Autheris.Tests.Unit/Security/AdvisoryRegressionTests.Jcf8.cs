namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    // GHSA-jcf8-hchm-55h3: WebSQL DML authorization (AllowDml=true is set explicitly; the write decision is the last check).
    [Fact]
    public async Task Ghsa_jcf8_55h3_regression()
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, allowDml: true, permissiveWrite: false, withDependency: true);
        var writer = BypassSemanticsAndDmlGuardrailTests.CreateWriter();
        var tenant = new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant);

        // A writer role alone grants nothing, also on a table the writer can read.
        await Should.ThrowAsync<WebSqlTableDeniedException>(() =>
            service.ExecuteGovernedQueryAsync(new GovernedSqlQueryRequest("UPDATE orders SET amount = 0 WHERE id = 1"), writer, tenant, (_, _) => Task.CompletedTask));

        // A table read by a row policy is refused with its own code before the write decision.
        var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
            service.ExecuteGovernedQueryAsync(new GovernedSqlQueryRequest("DELETE FROM consents WHERE id = 1"), writer, tenant, (_, _) => Task.CompletedTask));
        ex.ShouldNotBeOfType<WebSqlTableDeniedException>();

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders WHERE amount = 0")).ShouldBe(0);
    }
}
