namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// DEP-7 / INF-6: outside Development database connections must encrypt and verify the server certificate.
/// </summary>
public sealed class ConnectionTlsPolicyDep7Tests
{
    [Theory]
    [InlineData("PostgreSql", "Host=db;Database=x;SSL Mode=Require", false)]
    [InlineData("PostgreSql", "Host=db;Database=x;SSL Mode=Prefer", false)]
    [InlineData("PostgreSql", "Host=db;Database=x", false)]
    [InlineData("PostgreSql", "Host=db;Database=x;SSL Mode=VerifyCA", true)]
    [InlineData("PostgreSql", "Host=db;Database=x;SSL Mode=VerifyFull", true)]
    [InlineData("SqlServer", "Server=db;Database=x;TrustServerCertificate=True", false)]
    [InlineData("SqlServer", "Server=db;Database=x;Encrypt=False", false)]
    [InlineData("SqlServer", "Server=db;Database=x;Encrypt=Mandatory", true)]
    [InlineData("SqlServer", "Server=db;Database=x;Encrypt=Strict", true)]
    [InlineData("Sqlite", "Data Source=/app/data/x.db", true)]
    public void Validate_RequiresCertificateVerification(string provider, string connectionString, bool expectedValid)
    {
        (ConnectionTlsPolicy.Validate(provider, connectionString) == null).ShouldBe(expectedValid);
    }

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions WithDataSource(string connectionString) => new()
    {
        DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "vault://keys/prod-hmac" },
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["finance"] = new() { Provider = "SqlServer", ConnectionString = connectionString }
            }
        }
    };

    [Fact]
    public void Startup_InProduction_RejectsTrustServerCertificate()
    {
        var ex = Should.Throw<ValidationException>(() => GatewayServiceCollectionExtensions.ValidateGatewayOptions(
            WithDataSource("Server=db;Database=x;TrustServerCertificate=true"), Env(Environments.Production), _ => null));

        ex.Message.ShouldContain("finance");
        ex.Message.ShouldContain("TrustServerCertificate");
    }

    [Fact]
    public void Startup_InProduction_AcceptsVerifiedTls()
    {
        Should.NotThrow(() => GatewayServiceCollectionExtensions.ValidateGatewayOptions(
            WithDataSource("Server=db;Database=x;Encrypt=Mandatory"), Env(Environments.Production), _ => null));
    }

    [Fact]
    public void Startup_InDevelopment_AllowsTrustServerCertificate()
    {
        Should.NotThrow(() => GatewayServiceCollectionExtensions.ValidateGatewayOptions(
            WithDataSource("Server=db;Database=x;TrustServerCertificate=true"), Env(Environments.Development), _ => null));
    }
}
