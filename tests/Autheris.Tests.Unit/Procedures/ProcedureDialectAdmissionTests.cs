namespace Autheris.Tests.Unit.Procedures;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using Autheris.Api.Extensions.DependencyInjection.StartupChecks;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC-PROC-05: closed dialect set (SQL Server, PostgreSQL) at host start and per loaded definition.</summary>
public sealed class ProcedureDialectAdmissionTests
{
    private const string Secret = "Server=db;Password=SuperSecretValue";

    private static GatewayOptions Options(bool enabled, string provider, string[]? allowed = null, bool withConnection = true)
    {
        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase);
        if (withConnection)
        {
            connections["procedures"] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = Secret };
        }

        foreach (string name in allowed ?? [])
        {
            connections[name] = new DataSourceConnectionOptions { Provider = "Databricks", ConnectionString = Secret };
        }

        return new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions { Connections = connections },
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions { Enabled = enabled, ConnectionName = "procedures", AllowedDataSources = [.. allowed ?? []] }
            }
        };
    }

    private static void RunCheck(GatewayOptions options)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        ProcedureStartupChecks.DialectAdmission(new StartupCheckContext(options, env, _ => null, null));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Oracle")]
    [InlineData("Databricks")]
    [InlineData("Sqlite")]
    [InlineData("NoSuchEngine")]
    public void Startup_ConnectionOracle_FailsHostStart(string provider)
    {
        // SEC-PROC-05: Oracle, Databricks and an unresolvable dialect are refused; the text names the source, not the secret.
        var ex = Should.Throw<ValidationException>(() => RunCheck(Options(true, provider)));
        ex.Message.ShouldBe("Stored procedure endpoints support SQL Server and PostgreSQL only (data source 'procedures').");
        ex.Message.ShouldNotContain("SuperSecretValue");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Startup_AllowedDataSourceDatabricks_FailsHostStart()
    {
        var ex = Should.Throw<ValidationException>(() => RunCheck(Options(true, "SqlServer", ["lake"])));
        ex.Message.ShouldBe("Stored procedure endpoints support SQL Server and PostgreSQL only (data source 'lake').");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Startup_MissingConnection_FailsHostStart()
    {
        var ex = Should.Throw<ValidationException>(() => RunCheck(Options(true, "", withConnection: false)));
        ex.Message.ShouldContain("(data source 'procedures')");
    }

    [Theory]
    [InlineData("SqlServer")]
    [InlineData("PostgreSql")]
    public void Startup_SqlServerAndPostgreSql_Accepted(string provider) =>
        Should.NotThrow(() => RunCheck(Options(true, provider)));

    [Fact]
    public void Startup_Disabled_NoCheck() =>
        Should.NotThrow(() => RunCheck(Options(false, "Oracle", ["lake"])));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("Oracle", "procedure", false)]
    [InlineData("Databricks", "tvf", false)]
    [InlineData("SqlServer", "procedure", true)]
    [InlineData("PostgreSql", "tvf", true)]
    public void Load_DefinitionWithUnsupportedDialect_Rejected(string provider, string kind, bool registered)
    {
        // SEC-PROC-05: the loader repeats the admission, so a definition added later cannot widen the set.
        string dir = Path.Combine(Path.GetTempPath(), "autheris-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var gateway = Options(true, provider);
            gateway = new GatewayOptions
            {
                DataSources = gateway.DataSources,
                SqlEndpoints = new SqlEndpointsOptions
                {
                    Procedures = new ProcedureEndpointsOptions
                    {
                        AllowDeclaredValidation = true, Enabled = true, ConnectionName = "procedures", Directory = dir, AllowedSchemas = ["api"], RequireDdlHashForDeclared = false
                    }
                }
            };
            string file = Path.Combine(dir, "p.proc.yaml");
            File.WriteAllText(file, $"name: p\nprocedure: api.usp_P\nkind: {kind}\nvalidation: declared\nresult_table: sales.orders\noutputs:\n  - order_id\n");
            var registry = new InMemoryProcedureRegistry();
            var loader = new ProcedureDefinitionLoader(registry, Microsoft.Extensions.Options.Options.Create(gateway));

            loader.TryLoadFile(file, dir).ShouldBe(registered);
            registry.TryGet("p", out _).ShouldBe(registered);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_PostgreSqlProcedureKind_Rejected()
    {
        var options = Options(true, "PostgreSql");
        ProcedureConnectionProvider.CheckAdmission(options, "procedures", ProcedureKind.Procedure).ShouldNotBeNull();
        ProcedureConnectionProvider.CheckAdmission(options, "procedures", ProcedureKind.TableValuedFunction).ShouldBeNull();
        ProcedureConnectionProvider.CheckAdmission(Options(true, "SqlServer"), "procedures", ProcedureKind.Procedure).ShouldBeNull();
    }
}
