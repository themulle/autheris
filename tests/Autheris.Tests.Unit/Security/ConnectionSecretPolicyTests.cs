namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConnectionSecretPolicyTests
{
    private const string Tcps = "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.example)(PORT=2484))(CONNECT_DATA=(SERVICE_NAME=APP)))";

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static GatewayOptions CreateOptions(string provider, string connectionString, string? passwordKeyVaultRef = null) =>
        new()
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ds"] = new()
                    {
                        Provider = provider,
                        ConnectionString = connectionString,
                        PasswordKeyVaultRef = passwordKeyVaultRef
                    }
                }
            }
        };

    [Fact]
    public void PostgreSql_plaintext_password_is_rejected()
    {
        const string connStr = "Host=localhost;Database=mydb;Username=myuser;Password=secret";
        ConnectionSecretPolicy.HasPlaintextPassword(DatabaseDialect.PostgreSql, connStr).ShouldBeTrue();

        var options = CreateOptions("PostgreSql", connStr);
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(options, Env(Environments.Production)));
    }

    [Fact]
    public void SqlServer_plaintext_password_is_rejected()
    {
        const string connStr = "Server=localhost;Database=mydb;User Id=myuser;Password=secret";
        ConnectionSecretPolicy.HasPlaintextPassword(DatabaseDialect.SqlServer, connStr).ShouldBeTrue();

        var options = CreateOptions("SqlServer", connStr);
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(options, Env(Environments.Production)));
    }

    [Fact]
    public void Oracle_plaintext_password_is_rejected()
    {
        ConnectionSecretPolicy.HasPlaintextPassword(DatabaseDialect.Oracle, "User Id=a;Password=x;Data Source=d").ShouldBeTrue();
        ConnectionSecretPolicy.HasPlaintextPassword(DatabaseDialect.Oracle, "User Id=a;Data Source=d").ShouldBeFalse();
        ConnectionSecretPolicy.HasPlaintextPassword(DatabaseDialect.Oracle, "User Id=a;Password=;Data Source=d").ShouldBeFalse();

        var options = CreateOptions("Oracle", "User Id=app;Password=secret;Data Source=" + Tcps, passwordKeyVaultRef: "vault-ref");
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDataSourceSecrets(options, Env(Environments.Production)));
    }

    [Theory]
    [InlineData("PostgreSql", "Host=localhost;Database=mydb;Username=myuser")]
    [InlineData("SqlServer", "Server=localhost;Database=mydb;User Id=myuser")]
    [InlineData("Oracle", "User Id=app;Data Source=" + Tcps)]
    public void Secret_reference_is_accepted(string provider, string connectionString)
    {
        var options = CreateOptions(provider, connectionString, passwordKeyVaultRef: "kv-ref");
        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceSecrets(options, Env(Environments.Production)));
    }

    [Fact]
    public void Integrated_security_without_reference_is_accepted_for_postgres_and_sqlserver()
    {
        var pgOptions = CreateOptions("PostgreSql", "Host=localhost;Database=mydb;Username=myuser");
        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceSecrets(pgOptions, Env(Environments.Production)));

        var ssOptions = CreateOptions("SqlServer", "Server=localhost;Database=mydb;Integrated Security=true");
        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceSecrets(ssOptions, Env(Environments.Production)));
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "PostgreSql", "Host=localhost;Database=mydb;Username=myuser;Password=supersecret")]
    [InlineData(DatabaseDialect.SqlServer, "SqlServer", "Server=localhost;Database=mydb;User Id=myuser;Password=supersecret")]
    [InlineData(DatabaseDialect.Oracle, "Oracle", "User Id=app;Password=supersecret;Data Source=localhost:1521/FREEPDB1")]
    public void Plaintext_password_is_warned_not_rejected_in_development(DatabaseDialect dialect, string provider, string connectionString)
    {
        ConnectionSecretPolicy.HasPlaintextPassword(dialect, connectionString).ShouldBeTrue();
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);

        var options = CreateOptions(provider, connectionString);
        Should.NotThrow(() => GatewayStartupValidator.ValidateDataSourceSecrets(options, Env(Environments.Development), logger));

        // Verify warning logged
        logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == "Log" &&
            (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);

        // Security check: logger must NEVER receive the plaintext password or connection string
        foreach (var call in logger.ReceivedCalls())
        {
            foreach (var arg in call.GetArguments())
            {
                var argStr = arg?.ToString() ?? string.Empty;
                argStr.ShouldNotContain("supersecret");
                argStr.ShouldNotContain(connectionString);
            }
        }
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "invalid;connection;;==string")]
    [InlineData(DatabaseDialect.SqlServer, "invalid;connection;;==string")]
    [InlineData(DatabaseDialect.Oracle, "invalid;connection;;==string")]
    public void Unparsable_connection_string_fails_closed(DatabaseDialect dialect, string connectionString)
    {
        Should.Throw<SecurityException>(() => ConnectionSecretPolicy.HasPlaintextPassword(dialect, connectionString));
    }
}
