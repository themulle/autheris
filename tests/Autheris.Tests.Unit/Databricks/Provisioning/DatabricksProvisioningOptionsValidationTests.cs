namespace Autheris.Tests.Unit.Databricks.Provisioning;

using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks.Provisioning;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1122: startup validation of the provisioning section. The provisioning principal is a dedicated OAuth M2M service
/// principal (never PAT, OBO or a query principal), one tenant is named, and only schemas of the data source catalog are allowed.
/// </summary>
[Trait("Category", "Security")]
public sealed class DatabricksProvisioningOptionsValidationTests
{
    [Fact]
    public void Valid_section_passes()
    {
        Should.NotThrow(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning())));
    }

    [Fact]
    public void Data_source_without_section_is_not_validated()
    {
        Should.NotThrow(() => DatabricksProvisioningOptionsValidator.Validate(Options(provisioning: null)));
    }

    [Theory]
    [InlineData(DatabricksAuthMode.Pat)]
    [InlineData(DatabricksAuthMode.OAuthObo)]
    public void Pat_and_obo_are_rejected(DatabricksAuthMode mode)
    {
        var options = Options(Provisioning(auth: new DatabricksAuthOptions { Mode = mode, ClientId = "prov-sp", ClientSecretRef = "kv://prov", PatRef = "kv://pat" }));
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(options)).Message.ShouldContain("OAuthM2M");
    }

    [Fact]
    public void Inline_secret_is_rejected()
    {
        var options = Options(Provisioning(auth: new DatabricksAuthOptions { ClientId = "prov-sp", ClientSecretRef = "plain-secret-value" }));
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(options)).Message.ShouldContain("secret reference");
    }

    [Theory]
    [InlineData("sp-id")]
    [InlineData("SP-ID")]
    [InlineData("tenant-sp")]
    public void Provisioning_principal_equal_to_a_query_principal_is_rejected(string clientId)
    {
        var options = Options(Provisioning(auth: new DatabricksAuthOptions { ClientId = clientId, ClientSecretRef = "kv://prov" }));
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(options)).Message.ShouldContain("differ");
    }

    [Fact]
    public void Provisioning_principal_used_for_queries_on_another_source_is_rejected()
    {
        var options = Options(Provisioning());
        var other = new DataSourceConnectionOptions
        {
            Provider = "databricks",
            Databricks = new DatabricksDataSourceOptions
            {
                Host = DatabricksTestSupport.Host,
                WarehouseId = "wh-2",
                Auth = new DatabricksAuthOptions { ClientId = "prov-sp", ClientSecretRef = "kv://other" }
            }
        };
        var withOther = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(options.DataSources.Connections) { ["other"] = other }
            }
        };

        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(withOther)).Message.ShouldContain("differ");
    }

    [Fact]
    public void M2m_section_with_obo_or_pat_settings_is_rejected()
    {
        var withObo = new DatabricksAuthOptions { ClientId = "prov-sp", ClientSecretRef = "kv://prov", Obo = new DatabricksOboOptions { ClientId = "x" } };
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(auth: withObo))));

        var withPat = new DatabricksAuthOptions { ClientId = "prov-sp", ClientSecretRef = "kv://prov", PatRef = "kv://pat" };
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(auth: withPat))));
    }

    [Fact]
    public void Additional_principals_in_the_provisioning_auth_are_rejected()
    {
        var withTenants = new DatabricksAuthOptions
        {
            ClientId = "prov-sp",
            ClientSecretRef = "kv://prov",
            Tenants = new Dictionary<string, DatabricksTenantPrincipalOptions> { ["acme"] = new() { ClientId = "x", ClientSecretRef = "kv://x" } }
        };
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(auth: withTenants))));

        var withFallback = new DatabricksAuthOptions { ClientId = "prov-sp", ClientSecretRef = "kv://prov", FallbackToGlobalPrincipal = true };
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(auth: withFallback))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("acme tenant")]
    [InlineData("_platform")]
    public void Missing_or_invalid_tenant_is_rejected(string? tenant)
    {
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(tenant: tenant))))
            .Message.ShouldContain("Tenant");
    }

    [Theory]
    [InlineData("main")]
    [InlineData("main.sales.orders")]
    [InlineData("main.*")]
    [InlineData("main.sa les")]
    [InlineData("other.sales")]
    [InlineData("")]
    public void Invalid_allowed_schema_entries_are_rejected(string entry)
    {
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(allowedSchemas: [entry]))));
    }

    [Fact]
    public void Empty_allowed_schemas_are_rejected()
    {
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(allowedSchemas: []))))
            .Message.ShouldContain("AllowedSchemas");
    }

    [Fact]
    public void Allowed_schemas_compare_case_insensitively_with_the_catalog()
    {
        Should.NotThrow(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(allowedSchemas: ["MAIN.Sales"]))));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(301)]
    public void Reconcile_interval_outside_bounds_is_rejected(int seconds)
    {
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(interval: seconds))));
    }

    [Fact]
    public void Revert_drift_mode_is_not_accepted_yet()
    {
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(drift: DatabricksDriftMode.Revert))))
            .Message.ShouldContain("Report");
    }

    [Fact]
    public void Undefined_modes_are_rejected()
    {
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(mode: (DatabricksProvisioningMode)9))));
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(drift: (DatabricksDriftMode)9))));
    }

    [Theory]
    [InlineData("http://adb-123.azuredatabricks.net/oidc/v1/token")]
    [InlineData("https://evil.example.com/oidc/v1/token")]
    [InlineData("https://adb-123.azuredatabricks.net/oidc/v1/token?x=1")]
    public void Token_endpoint_off_the_workspace_host_is_rejected(string endpoint)
    {
        var auth = new DatabricksAuthOptions { ClientId = "prov-sp", ClientSecretRef = "kv://prov", TokenEndpoint = endpoint };
        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(Options(Provisioning(auth: auth))));
    }

    [Fact]
    public void Section_on_a_non_databricks_source_is_rejected()
    {
        var options = Options(Provisioning());
        var connection = options.DataSources.Connections["dbx"];
        var postgres = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["pg"] = new() { Provider = "postgresql", ConnectionString = "Host=x", Databricks = connection.Databricks }
                }
            }
        };

        Should.Throw<ValidationException>(() => DatabricksProvisioningOptionsValidator.Validate(postgres));
    }

    private static DatabricksProvisioningOptions Provisioning(
        string? tenant = "acme",
        IReadOnlyList<string>? allowedSchemas = null,
        DatabricksAuthOptions? auth = null,
        int interval = 60,
        DatabricksProvisioningMode mode = DatabricksProvisioningMode.DryRun,
        DatabricksDriftMode drift = DatabricksDriftMode.Report) => new()
        {
            Mode = mode,
            DriftMode = drift,
            Tenant = tenant,
            AllowedSchemas = allowedSchemas ?? ["main.sales"],
            ReconcileIntervalSeconds = interval,
            Auth = auth ?? new DatabricksAuthOptions { Mode = DatabricksAuthMode.OAuthM2M, ClientId = "prov-sp", ClientSecretRef = "kv://dbx-prov-secret" }
        };

    private static GatewayOptions Options(DatabricksProvisioningOptions? provisioning) => new()
    {
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["dbx"] = new()
                {
                    Provider = "databricks",
                    Databricks = new DatabricksDataSourceOptions
                    {
                        Host = DatabricksTestSupport.Host,
                        WarehouseId = "wh-1",
                        Catalog = "main",
                        Auth = new DatabricksAuthOptions
                        {
                            Mode = DatabricksAuthMode.OAuthM2M,
                            ClientId = "sp-id",
                            ClientSecretRef = "kv://dbx-sp-secret",
                            Tenants = new Dictionary<string, DatabricksTenantPrincipalOptions>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["acme"] = new() { ClientId = "tenant-sp", ClientSecretRef = "kv://tenant-sp" }
                            },
                            FallbackToGlobalPrincipal = true
                        },
                        Provisioning = provisioning
                    }
                }
            }
        }
    };
}
