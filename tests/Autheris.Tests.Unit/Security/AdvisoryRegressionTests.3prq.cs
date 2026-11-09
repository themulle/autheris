namespace Autheris.Tests.Unit.Security;

using System.Threading.Tasks;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    // GHSA-3prq-qxg7-92qf: WebSQL DML scoping (AllowDml=true is set explicitly; the tenant-column check runs before the write decision).
    [Fact]
    public async Task Ghsa_3prq_qxg7_regression()
    {
        await using var fixture = await BypassSemanticsAndDmlGuardrailTests.DmlFixture.CreateAsync();
        var service = BypassSemanticsAndDmlGuardrailTests.CreateDmlService(fixture, allowDml: true, permissiveWrite: true, plainTable: true);
        var writer = BypassSemanticsAndDmlGuardrailTests.CreateWriter();
        var tenant = new TenantId(BypassSemanticsAndDmlGuardrailTests.Tenant);

        // A table without a tenant column is shared by all tenants; DML on it is refused, also as the second table of a statement.
        foreach (var sql in new[] { "DELETE FROM plain WHERE id = 1", "DELETE FROM orders WHERE id IN (SELECT id FROM plain)" })
        {
            var ex = await Should.ThrowAsync<WebSqlPolicyException>(() =>
                service.ExecuteGovernedQueryAsync(new GovernedSqlQueryRequest(sql), writer, tenant, (_, _) => Task.CompletedTask));
            ex.Code.ShouldBe(GovernedSqlErrorCodes.PolicyViolation);
        }

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM orders")).ShouldBe(7);
    }
}
