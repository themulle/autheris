namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using Xunit;

[Trait("Category", "Security")]
public sealed class DialectGateTests
{
    [Theory]
    [InlineData("Snowflake")]
    [InlineData("Ansi")]
    [Trait("SecurityCondition", "SEC-SIMPL-38")]
    public async Task Unsupported_dialect_fails_closed_without_generic_fallback(string provider)
    {
        // 1. Startup gate: typed UnsupportedDataSourceDialectException is thrown
        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["unsupported"] = new()
                    {
                        Provider = provider,
                        ConnectionString = "Host=localhost;Database=db;"
                    }
                }
            }
        };

        var startupEx = Should.Throw<UnsupportedDataSourceDialectException>(() => DataSourceDialectValidator.Validate(options));
        startupEx.Provider.ShouldBe(provider);
        startupEx.DataSourceName.ShouldBe("unsupported");

        // 2. Factory gate: refuses unsupported provider, no command created
        var factory = new SqlConnectionFactory();
        var factoryEx = await Should.ThrowAsync<NotSupportedException>(() =>
            factory.CreateOpenConnectionAsync(new DataSourceConnectionOptions
            {
                Provider = provider,
                ConnectionString = "Host=localhost;Database=db;"
            }));
        factoryEx.Message.ShouldContain("is not supported");

        // 3. Dialect generator factory: no generic or fallback generator is ever returned for invalid enum values
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDialectGeneratorFactory.GetGenerator((TargetSqlDialect)999));
    }
}
