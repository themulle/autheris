namespace Autheris.Tests.Unit.Databricks;

using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.DatabricksTestSupport;

/// <summary>WP-X0i / SEC-ADG-09 item 4: startup validation of the Databricks section.</summary>
public sealed class DatabricksStartupValidationTests
{
    [Fact]
    public void PatAuth_RejectedOutsideDevelopment_AtStartup()
    {
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(Options(DatabricksAuthMode.Pat), Env("Production")))
            .Message.ShouldContain("Pat");
    }

    [Fact]
    public void PatAuth_AllowedInDevelopment_AtStartup()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateDatabricks(Options(DatabricksAuthMode.Pat), Env("Development")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://adb-123.azuredatabricks.net")]
    [InlineData("*.azuredatabricks.net")]
    [InlineData("10.0.0.5")]
    public void InvalidHost_RejectedAtStartup(string host)
    {
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(Options(host: host), Env("Production")));
    }

    [Fact]
    public void OAuthWithoutClientIdOrSecretRef_RejectedAtStartup()
    {
        var options = Options();
        var auth = options.DataSources.Connections["dbx"].Databricks!.Auth;
        var broken = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["dbx"] = new() { Provider = "databricks", Databricks = new DatabricksDataSourceOptions { Host = Host, WarehouseId = "wh", Auth = new DatabricksAuthOptions { ClientId = auth.ClientId } } }
                }
            }
        };

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(broken, Env("Production")));
    }

    [Fact]
    public void ValidOAuthConfiguration_Passes()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateDatabricks(Options(), Env("Production")));
    }
}
