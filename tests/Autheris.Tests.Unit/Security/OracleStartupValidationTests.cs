namespace Autheris.Tests.Unit.Security;

using System.ComponentModel.DataAnnotations;
using System.Security;
using System.Text;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>CR-ADG-08: Oracle runtime startup rules (WP-F1) and the tenant collision check (decision B-1).</summary>
public sealed class OracleStartupValidationTests
{
    private const string Tcps = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=APP)))";

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions Options(string connectionString, string? passwordRef = null) => new()
    {
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["ora"] = new() { Provider = "Oracle", ConnectionString = connectionString, PasswordKeyVaultRef = passwordRef }
            }
        }
    };

    // ---- OracleConnectionString_SecretsOnlyFromKeyVault ----

    [Fact]
    public void OracleConnectionString_SecretsOnlyFromKeyVault_PlaintextPasswordFailsStartupOutsideDevelopment()
    {
        var options = Options("User Id=app;Password=hunter2;Data Source=" + Tcps, passwordRef: "vault-ref");
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateOracleRuntime(options, Env(Environments.Production)));
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateOracleRuntime(options, Env(Environments.Staging)));
    }

    [Fact]
    public void OracleConnectionString_WithoutAKeyVaultReference_FailsStartupOutsideDevelopment()
    {
        var options = Options("User Id=app;Data Source=" + Tcps);
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateOracleRuntime(options, Env(Environments.Production)));
    }

    [Fact]
    public void OracleConnectionString_WithAKeyVaultReferenceAndNoPassword_IsAccepted()
    {
        var options = Options("User Id=app;Data Source=" + Tcps, passwordRef: "vault-ref");
        Should.NotThrow(() => GatewayStartupValidator.ValidateOracleRuntime(options, Env(Environments.Production)));
    }

    [Fact]
    public void PlaintextPassword_IsAllowed_InDevelopment()
    {
        var options = Options("User Id=app;Password=hunter2;Data Source=localhost:1521/FREEPDB1");
        Should.NotThrow(() => GatewayStartupValidator.ValidateOracleRuntime(options, Env(Environments.Development)));
    }

    [Fact]
    public void ValidatorAppliesTheConnectionPolicy_TcpsAndAccountRules_OutsideDevelopment()
    {
        Should.Throw<SecurityException>(() => GatewayStartupValidator.ValidateOracleRuntime(
            Options("User Id=app;Data Source=db.example:1521/APP", "vault-ref"), Env(Environments.Production)));
        Should.Throw<SecurityException>(() => GatewayStartupValidator.ValidateOracleRuntime(
            Options("User Id=\"SYSTEM\";Data Source=" + Tcps, "vault-ref"), Env(Environments.Production)));
    }

    [Fact]
    public void OtherProviders_AreNotAffected()
    {
        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["pg"] = new() { Provider = "PostgreSql", ConnectionString = "Host=h;Password=p" }
                }
            }
        };
        Should.NotThrow(() => GatewayStartupValidator.ValidateOracleRuntime(options, Env(Environments.Production)));
    }

    [Fact]
    public async Task Factory_RefusesAPlaintextPassword_OutsideDevelopment_BeforeConnecting()
    {
        var factory = new SqlConnectionFactory(Env(Environments.Production), Substitute.For<IKeyVaultSecretProvider>());
        var options = new DataSourceConnectionOptions { Provider = "Oracle", ConnectionString = "User Id=app;Password=hunter2;Data Source=" + Tcps, PasswordKeyVaultRef = "ref" };
        await Should.ThrowAsync<SecurityException>(() => factory.CreateOpenConnectionAsync(options));
    }

    [Fact]
    public async Task Factory_RefusesAMissingKeyVaultReference_OutsideDevelopment()
    {
        var factory = new SqlConnectionFactory(Env(Environments.Production), Substitute.For<IKeyVaultSecretProvider>());
        var options = new DataSourceConnectionOptions { Provider = "Oracle", ConnectionString = "User Id=app;Data Source=" + Tcps };
        await Should.ThrowAsync<SecurityException>(() => factory.CreateOpenConnectionAsync(options));
    }

    [Fact]
    public async Task Factory_RefusesAnUnresolvableKeyVaultReference()
    {
        var secrets = Substitute.For<IKeyVaultSecretProvider>();
        secrets.GetSecretBytes(Arg.Any<string>()).Returns(Array.Empty<byte>());
        var factory = new SqlConnectionFactory(Env(Environments.Production), secrets);
        var options = new DataSourceConnectionOptions { Provider = "Oracle", ConnectionString = "User Id=app;Data Source=" + Tcps, PasswordKeyVaultRef = "ref" };
        await Should.ThrowAsync<SecurityException>(() => factory.CreateOpenConnectionAsync(options));
    }

    [Fact]
    public void PlaintextPasswordDetection_IsAccurate()
    {
        OracleConnectionStringPolicy.HasPlaintextPassword("User Id=a;Password=x;Data Source=d").ShouldBeTrue();
        OracleConnectionStringPolicy.HasPlaintextPassword("User Id=a;Data Source=d").ShouldBeFalse();
        OracleConnectionStringPolicy.HasPlaintextPassword("User Id=a;Password=;Data Source=d").ShouldBeFalse();
    }

    // ---- administrative account check ignores delimiters ----

    [Theory]
    [InlineData("User Id=\"SYSTEM\";Password=x;Data Source=" + Tcps)]
    [InlineData("User Id=\"system\";Password=x;Data Source=" + Tcps)]
    [InlineData("User Id=\"SYS\";Password=x;Data Source=" + Tcps)]
    [InlineData("User Id=' SYS ';Password=x;Data Source=" + Tcps)]
    [InlineData("User Id=\"\";Password=x;Data Source=" + Tcps)]
    public void QuotedAdministrativeAccounts_AreRejected(string connectionString)
    {
        Should.Throw<SecurityException>(() => OracleConnectionStringPolicy.Validate(connectionString, requireTcps: false));
    }

    // ---- B-1 tenant collision check ----

    [Fact]
    public void TenantCollisions_AreFound_CaseInsensitively_AndDeniedByEverySpelling()
    {
        var ids = new[] { "acme", "ACME", "other", "Other", "other", "solo", null, " " };
        var collisions = TenantCollisionCheck.FindCollisions(ids);
        collisions.Count.ShouldBe(2);
        collisions.SelectMany(c => c.Spellings).OrderBy(x => x, StringComparer.Ordinal).ShouldBe(new[] { "ACME", "Other", "acme", "other" });
        TenantCollisionCheck.DeniedTenants(ids).OrderBy(x => x, StringComparer.Ordinal).ShouldBe(new[] { "ACME", "Other", "acme", "other" });
        TenantCollisionCheck.DeniedTenants(ids).ShouldNotContain("solo");
    }

    [Fact]
    public void IdenticalSpellings_AreNoCollision() =>
        TenantCollisionCheck.FindCollisions(new[] { "acme", "acme", "beta" }).ShouldBeEmpty();

    private static GatewayOptions CollidingTenants() => new()
    {
        WebSql = new WebSqlOptions
        {
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["acme"] = ["ds"], ["ACME"] = ["ds"] }
        }
    };

    [Fact]
    public void CollidingTenants_StrictMode_RefusesTheStart_OutsideDevelopment()
    {
        var options = new GatewayOptions
        {
            WebSql = CollidingTenants().WebSql,
            TenantIsolation = new TenantIsolationOptions { StrictCollisionStartup = true }
        };
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateTenantCollisions(options, Env(Environments.Production)));
    }

    [Fact]
    public void CollidingTenants_Default_StartsAndLogsCritical_BecauseRequestsAreDeniedPerTenant()
    {
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);

        Should.NotThrow(() => GatewayStartupValidator.ValidateTenantCollisions(CollidingTenants(), Env(Environments.Production), logger));

        logger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Log"
            && (Microsoft.Extensions.Logging.LogLevel)c.GetArguments()[0]! == Microsoft.Extensions.Logging.LogLevel.Critical).ShouldBe(1);
    }

    [Fact]
    public void CollidingTenants_OnlyWarn_InDevelopment()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateTenantCollisions(CollidingTenants(), Env(Environments.Development)));
    }

    [Fact]
    public void DistinctTenants_AreAccepted_OutsideDevelopment()
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["acme"] = ["ds"], ["beta"] = ["ds"] }
            }
        };
        Should.NotThrow(() => GatewayStartupValidator.ValidateTenantCollisions(options, Env(Environments.Production)));
    }
}
