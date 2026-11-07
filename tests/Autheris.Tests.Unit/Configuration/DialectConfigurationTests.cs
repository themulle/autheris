namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DialectConfigurationTests
{
    private static GatewayOptions CreateOptions(string provider) =>
        new()
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["test_source"] = new()
                    {
                        Provider = provider,
                        ConnectionString = "Host=localhost;Database=db;"
                    }
                }
            }
        };

    [Theory]
    [InlineData("Ansi")]
    [InlineData("DuckDb")]
    [InlineData("Snowflake")]
    [InlineData("nonsense")]
    public void Unsupported_dialect_fails_validation(string provider)
    {
        var options = CreateOptions(provider);
        var ex = Should.Throw<UnsupportedDataSourceDialectException>(() => DataSourceDialectValidator.Validate(options));
        ex.DataSourceName.ShouldBe("test_source");
        ex.Provider.ShouldBe(provider);
        ex.Message.ShouldContain("test_source");
        ex.Message.ShouldContain(provider);
    }

    [Theory]
    [InlineData("Oracle")]
    [InlineData("Databricks")]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    [InlineData("Sqlite")]
    public void Oracle_and_Databricks_are_accepted(string provider)
    {
        var options = CreateOptions(provider);
        Should.NotThrow(() => DataSourceDialectValidator.Validate(options));
    }
}
