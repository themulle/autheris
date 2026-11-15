namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DatabaseDialectTests
{
    [Fact]
    public void Only_supported_values_exist()
    {
        Enum.GetValues<DatabaseDialect>().ShouldBe(
            [
                DatabaseDialect.PostgreSql,
                DatabaseDialect.SqlServer,
                DatabaseDialect.Databricks,
                DatabaseDialect.Oracle
            ],
            ignoreOrder: true);
    }
}

public sealed class DataSourceProviderTests
{
    [Theory]
    [Trait("Category", "Security")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_provider_fails_resolution(string? provider)
    {
        DataSourceProvider.TryResolveDialect(provider, out _).ShouldBeFalse();

        const string secretConnection = "Host=db.internal;Password=super-secret-value-123";
        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["empty_ds"] = new()
                    {
                        Provider = provider!,
                        ConnectionString = secretConnection
                    }
                }
            }
        };

        var validationEx = Should.Throw<UnsupportedDataSourceDialectException>(
            () => DataSourceDialectValidator.Validate(gatewayOptions));
        validationEx.Message.ShouldNotContain("super-secret-value-123");

        var factory = new SqlConnectionFactory();
        var factoryEx = await Should.ThrowAsync<NotSupportedException>(
            () => factory.CreateOpenConnectionAsync(gatewayOptions.DataSources.Connections["empty_ds"]));
        factoryEx.Message.ShouldNotContain("super-secret-value-123");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Sqlite")]
    [InlineData("sqlite")]
    [InlineData("sqlite3")]
    [InlineData("SQLITE")]
    [InlineData("SQLITE3")]
    public async Task Sqlite_aliases_are_not_resolved(string alias)
    {
        DataSourceProvider.TryResolveDialect(alias, out _).ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => DatabaseDialectExtensions.ParseDialect(alias));

        const string secretConnection = "Data Source=/var/data/secret.sqlite;Password=super-secret-value-456";
        var gatewayOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sqlite_ds"] = new()
                    {
                        Provider = alias,
                        ConnectionString = secretConnection
                    }
                }
            }
        };

        var validationEx = Should.Throw<UnsupportedDataSourceDialectException>(
            () => DataSourceDialectValidator.Validate(gatewayOptions));
        validationEx.Message.ShouldNotContain("super-secret-value-456");

        var factory = new SqlConnectionFactory();
        var factoryEx = await Should.ThrowAsync<NotSupportedException>(
            () => factory.CreateOpenConnectionAsync(gatewayOptions.DataSources.Connections["sqlite_ds"]));
        factoryEx.Message.ShouldNotContain("super-secret-value-456");
    }
}
