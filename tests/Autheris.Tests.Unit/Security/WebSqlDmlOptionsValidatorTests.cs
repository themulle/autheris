namespace Autheris.Tests.Unit.Security;

using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebSqlDmlOptionsValidatorTests
{
    private static GatewayOptions Options(bool allowDml, bool requireTenantColumn, long maxAffectedRows) => new()
    {
        WebSql = new WebSqlOptions { AllowDml = allowDml, MaxAffectedRows = maxAffectedRows },
        DataSources = new SqlDataSourceOptions { RequireTenantColumn = requireTenantColumn }
    };

    [Theory]
    [InlineData(true, false, 1000L)]
    [InlineData(true, true, 0L)]
    [InlineData(true, true, -1L)]
    public void SEC_36_SP3b_Dml_without_scoping_prerequisites_fails_startup(bool allowDml, bool requireTenantColumn, long maxAffectedRows)
    {
        var result = new WebSqlDmlOptionsValidator().Validate(null, Options(allowDml, requireTenantColumn, maxAffectedRows));

        result.Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, false, 0L)]
    [InlineData(true, true, 1000L)]
    public void SEC_36_SP3b_Valid_combinations_pass(bool allowDml, bool requireTenantColumn, long maxAffectedRows)
    {
        new WebSqlDmlOptionsValidator().Validate(null, Options(allowDml, requireTenantColumn, maxAffectedRows)).Succeeded.ShouldBeTrue();
    }
}
