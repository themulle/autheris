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

    [Theory]
    [InlineData(0, 10_000, 86_400)]
    [InlineData(-1, 10_000, 86_400)]
    [InlineData(60, 0, 86_400)]
    [InlineData(60, -5, 86_400)]
    [InlineData(60, 10_000, 0)]
    [InlineData(60, 10_000, -10)]
    public void AllowDml_with_non_positive_budget_or_ttl_fails_validation(
        int writeStatementsPerMinute,
        long writeRowsPerHour,
        int writeIdempotencyTtlSeconds)
    {
        var opts = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                AllowDml = true,
                MaxAffectedRows = 1000,
                WriteStatementsPerMinute = writeStatementsPerMinute,
                WriteRowsPerHour = writeRowsPerHour,
                WriteIdempotencyTtlSeconds = writeIdempotencyTtlSeconds
            },
            DataSources = new SqlDataSourceOptions { RequireTenantColumn = true }
        };

        new WebSqlDmlOptionsValidator().Validate(null, opts).Failed.ShouldBeTrue();
    }

    [Fact]
    public void AllowDml_false_with_WriteConnectionString_fails_validation()
    {
        var opts = new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowDml = false },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new DataSourceConnectionOptions
                    {
                        Provider = "PostgreSql",
                        ConnectionString = "Host=read",
                        WriteConnectionString = "Host=write"
                    }
                }
            }
        };

        var result = new WebSqlDmlOptionsValidator().Validate(null, opts);
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("WriteConnectionString", StringComparison.Ordinal));
    }
}
