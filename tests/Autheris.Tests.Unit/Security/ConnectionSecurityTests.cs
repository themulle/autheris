namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConnectionSecurityTests
{
    private const string Tcps = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=APP)))";
    private const string NonTcps = "User Id=app;Data Source=db.example:1521/APP";

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions SingleConnectionOptions(string provider, string connectionString, string? passwordKeyVaultRef = null) =>
        new()
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["test_ds"] = new()
                    {
                        Provider = provider,
                        ConnectionString = connectionString,
                        PasswordKeyVaultRef = passwordKeyVaultRef
                    }
                }
            }
        };

    [Fact]
    [Trait("SecurityCondition", "SEC-SIMPL-91")]
    public void Oracle_requires_tcps_and_plaintext_password_rejected_for_all_data_sources_outside_development()
    {
        // 1. Oracle requires TCPS outside Development: non-TCPS throws SecurityException
        var nonTcpsOracle = SingleConnectionOptions("Oracle", NonTcps, passwordKeyVaultRef: "vault-ref");
        Should.Throw<SecurityException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(nonTcpsOracle, Env(Environments.Production)));
        Should.Throw<SecurityException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(nonTcpsOracle, Env(Environments.Staging)));

        // 2. Oracle with TCPS and valid reference passes outside Development
        var validTcpsOracle = SingleConnectionOptions("Oracle", "User Id=app;Data Source=" + Tcps, passwordKeyVaultRef: "vault-ref");
        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceSecrets(validTcpsOracle, Env(Environments.Production)));

        // 3. Literal password in connection string is rejected outside Development for all data source providers (PG, SQL Server, Oracle)
        var pgPlaintext = SingleConnectionOptions("PostgreSql", "Host=localhost;Database=db;Username=u;Password=secret");
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(pgPlaintext, Env(Environments.Production)));
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(pgPlaintext, Env(Environments.Staging)));

        var sqlServerPlaintext = SingleConnectionOptions("SqlServer", "Server=localhost;Database=db;User Id=u;Password=secret");
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(sqlServerPlaintext, Env(Environments.Production)));
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(sqlServerPlaintext, Env(Environments.Staging)));

        var oraclePlaintext = SingleConnectionOptions("Oracle", "User Id=app;Password=secret;Data Source=" + Tcps, passwordKeyVaultRef: "vault-ref");
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(oraclePlaintext, Env(Environments.Production)));
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(oraclePlaintext, Env(Environments.Staging)));

        // 4. In Development, plaintext passwords do NOT throw
        var devEnv = Env(Environments.Development);
        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceSecrets(pgPlaintext, devEnv));
        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceSecrets(sqlServerPlaintext, devEnv));
        var devOraclePlaintext = SingleConnectionOptions("Oracle", "User Id=app;Password=secret;Data Source=localhost:1521/FREEPDB1");
        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceSecrets(devOraclePlaintext, devEnv));
    }
}
