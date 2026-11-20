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
    public void PatAuth_WithSecretReference_AllowedInProduction_AtStartup()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateDatabricks(Options(DatabricksAuthMode.Pat), Env("Production")));
    }

    [Fact]
    public void PatAuth_AllowedInDevelopment_AtStartup()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateDatabricks(Options(DatabricksAuthMode.Pat), Env("Development")));
    }

    [Fact]
    public void TokenEndpoint_OnAllowlistedAccountHost_Passes()
    {
        var options = new GatewayOptions
        {
            Databricks = new DatabricksOptions
            {
                AllowedAccountTokenHosts = ["accounts.azuredatabricks.net"]
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["dbx"] = new()
                    {
                        Provider = "databricks",
                        CommandTimeoutSeconds = 30,
                        Databricks = new DatabricksDataSourceOptions
                        {
                            Host = Host,
                            WarehouseId = "wh-1",
                            Auth = new DatabricksAuthOptions
                            {
                                Mode = DatabricksAuthMode.OAuthM2M,
                                ClientId = "sp-id",
                                ClientSecretRef = "kv://dbx-sp-secret",
                                TokenEndpoint = "https://accounts.azuredatabricks.net/oidc/accounts/123/v1/token"
                            }
                        }
                    }
                }
            }
        };

        Should.NotThrow(() => GatewayStartupValidator.ValidateDatabricks(options, Env("Production")));
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

    [Fact]
    public void ValidOAuthOboConfiguration_Passes()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateDatabricks(Options(DatabricksAuthMode.OAuthObo), Env("Production")));
    }

    [Fact]
    public void OAuthOboWithoutOboSection_RejectedAtStartup()
    {
        var broken = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["dbx"] = new()
                    {
                        Provider = "databricks",
                        Databricks = new DatabricksDataSourceOptions
                        {
                            Host = Host,
                            WarehouseId = "wh",
                            Auth = new DatabricksAuthOptions { Mode = DatabricksAuthMode.OAuthObo, Obo = null }
                        }
                    }
                }
            }
        };

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(broken, Env("Production")))
            .Message.ShouldContain("Auth.Obo section");
    }

    [Fact]
    public void OAuthOboWithoutSecretOrCert_RejectedAtStartup()
    {
        var broken = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["dbx"] = new()
                    {
                        Provider = "databricks",
                        Databricks = new DatabricksDataSourceOptions
                        {
                            Host = Host,
                            WarehouseId = "wh",
                            Auth = new DatabricksAuthOptions
                            {
                                Mode = DatabricksAuthMode.OAuthObo,
                                Obo = new DatabricksOboOptions { ClientId = "obo-id" }
                            }
                        }
                    }
                }
            }
        };

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(broken, Env("Production")))
            .Message.ShouldContain("Auth.Obo.ClientSecretRef is required");
    }

    [Theory]
    [InlineData("http://login.microsoftonline.com/t", null)]
    [InlineData("https://evil.example/t", null)]
    [InlineData("https://login.microsoftonline.com/t?x=1", null)]
    [InlineData("https://login.microsoftonline.com/t/extra", null)]
    [InlineData(null, "t/../../evil")]
    [Trait("Category", "Security")]
    public void Obo_UntrustedAuthority_FailsStartup(string? authority, string? tenantId)
    {
        var options = Options(DatabricksAuthMode.OAuthObo, extra: conns =>
        {
            var c = conns["dbx"];
            conns["dbx"] = new DataSourceConnectionOptions
            {
                Provider = c.Provider,
                CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                Databricks = new DatabricksDataSourceOptions
                {
                    Host = c.Databricks!.Host,
                    WarehouseId = c.Databricks.WarehouseId,
                    Auth = new DatabricksAuthOptions
                    {
                        Mode = DatabricksAuthMode.OAuthObo,
                        Obo = new DatabricksOboOptions
                        {
                            TenantId = tenantId,
                            ClientId = "obo-id",
                            ClientSecretRef = "kv://dbx-obo-secret",
                            Authority = authority,
                            Scopes = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default"
                        }
                    }
                }
            };
        });

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(options, Env("Production")));
        ex.Message.ShouldContain("Auth.Obo.Authority");
    }

    [Theory]
    [InlineData("kv://dbx-obo-cert", null)]
    [InlineData("kv://dbx-obo-cert", "kv://dbx-obo-secret")]
    [Trait("Category", "Security")]
    public void Obo_ClientCertificateRef_FailsStartup(string certRef, string? secretRef)
    {
        var options = Options(DatabricksAuthMode.OAuthObo, extra: conns =>
        {
            var c = conns["dbx"];
            conns["dbx"] = new DataSourceConnectionOptions
            {
                Provider = c.Provider,
                CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                Databricks = new DatabricksDataSourceOptions
                {
                    Host = c.Databricks!.Host,
                    WarehouseId = c.Databricks.WarehouseId,
                    Auth = new DatabricksAuthOptions
                    {
                        Mode = DatabricksAuthMode.OAuthObo,
                        Obo = new DatabricksOboOptions
                        {
                            TenantId = "tenant-123",
                            ClientId = "obo-id",
                            ClientSecretRef = secretRef,
#pragma warning disable CS0618
                            ClientCertificateRef = certRef,
#pragma warning restore CS0618
                            Scopes = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default"
                        }
                    }
                }
            };
        });

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(options, Env("Production")));
        ex.Message.ShouldContain("ClientCertificateRef");
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "")]
    [InlineData("Development", "   ")]
    [Trait("Category", "Security")]
    public void Obo_MissingClientSecretRef_FailsStartup(string envName, string? secretRef)
    {
        var options = Options(DatabricksAuthMode.OAuthObo, extra: conns =>
        {
            var c = conns["dbx"];
            conns["dbx"] = new DataSourceConnectionOptions
            {
                Provider = c.Provider,
                CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                Databricks = new DatabricksDataSourceOptions
                {
                    Host = c.Databricks!.Host,
                    WarehouseId = c.Databricks.WarehouseId,
                    Auth = new DatabricksAuthOptions
                    {
                        Mode = DatabricksAuthMode.OAuthObo,
                        Obo = new DatabricksOboOptions
                        {
                            TenantId = "tenant-123",
                            ClientId = "obo-id",
                            ClientSecretRef = secretRef,
                            Scopes = "2ff814a6-3304-4ab8-85cb-cd0e6f879c1d/.default"
                        }
                    }
                }
            };
        });

        var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(options, Env(envName)));
        ex.Message.ShouldContain("ClientSecretRef");
    }

    [Fact]
    public void DelegatedEnforcement_WithKeySetCacheAllowed_RejectedAtStartup()
    {
        var options = Options(DatabricksAuthMode.OAuthObo, extra: conns =>
        {
            var c = conns["dbx"];
            conns["dbx"] = new DataSourceConnectionOptions
            {
                Provider = c.Provider,
                CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                Databricks = c.Databricks,
                EnforcementMode = EnforcementMode.Delegated,
                KeySetCacheAllowed = true
            };
        });

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(options, Env("Production")))
            .Message.ShouldContain("KeySetCacheAllowed must be false");
    }

    [Fact]
    public void DelegatedEnforcement_WithoutOAuthObo_RejectedAtStartup()
    {
        var options = Options(DatabricksAuthMode.OAuthM2M, extra: conns =>
        {
            var c = conns["dbx"];
            conns["dbx"] = new DataSourceConnectionOptions
            {
                Provider = c.Provider,
                CommandTimeoutSeconds = c.CommandTimeoutSeconds,
                Databricks = c.Databricks,
                EnforcementMode = EnforcementMode.Delegated,
                KeySetCacheAllowed = false
            };
        });

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDatabricks(options, Env("Production")))
            .Message.ShouldContain("Delegated enforcement requires Auth.Mode = OAuthObo");
    }
}
