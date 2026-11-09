namespace Autheris.Tests.Unit.Procedures;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>YAML is the only procedure definition format; removed and unknown keys are rejected, never ignored.</summary>
public sealed class ProcedureDefinitionModelTests
{
    private const string Minimal = "name: p\nprocedure: api.usp_X\n";

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("timeout_seconds: 10")]
    [InlineData("required_roles: [admin]")]
    [InlineData("context_bindings:\n  - key: tenant_id\n    parameter: T")]
    [InlineData("allow_dynamic_sql: true")]
    public void Parse_RemovedKey_Rejected_SEC_PROC_07(string line)
    {
        var ex = Should.Throw<FormatException>(() => TestProcedureYaml.Definition(Minimal + line));

        ex.Message.ShouldStartWith("Invalid YAML procedure declaration:");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Parse_UnknownKey_Rejected_SEC_PROC_07()
    {
        Should.Throw<FormatException>(() => TestProcedureYaml.Definition(Minimal + "bogus: 1"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Parse_DuplicateKey_Rejected_SEC_PROC_07()
    {
        Should.Throw<FormatException>(() => TestProcedureYaml.Definition(Minimal + "timeout: 5\ntimeout: 6"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Parse_RolesKey_StillRestrictsAccess_SEC_PROC_04()
    {
        TestProcedureYaml.Definition(Minimal + "roles: [auditor]").RequiredRoles.ShouldBe(["auditor"]);
    }

    [Fact]
    public void Parse_TimeoutKey_IsClampedToConfiguredMaximum()
    {
        TestProcedureYaml.Definition(Minimal + "timeout: 500", maxTimeoutSeconds: 60).TimeoutSeconds.ShouldBe(60);
    }

    private static (ProcedureDefinitionLoader Loader, IOptions<GatewayOptions> Options, string Dir) CreateLoader()
    {
        string dir = Path.Combine(Path.GetTempPath(), "autheris-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = Options.Create(new GatewayOptions
        {
            DataSources = ProcedureTestData.SqlServerSources("procedures"),
            SqlEndpoints = new SqlEndpointsOptions
            {
                Procedures = new ProcedureEndpointsOptions { Enabled = true, Directory = dir, AllowedSchemas = ["api"], EnableHotReload = false }
            }
        });
        return (new ProcedureDefinitionLoader(new InMemoryProcedureRegistry(), options), options, dir);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Startup_ProcSqlFilePresent_FailsHostStart_SEC_PROC_07()
    {
        var (loader, options, dir) = CreateLoader();
        try
        {
            File.WriteAllText(Path.Combine(dir, "legacy.proc.sql"), "-- @procedure api.usp_X");
            File.WriteAllText(Path.Combine(dir, "ok.proc.yaml"), Minimal);
            var service = new ProcedureRegistrationService(loader, new InMemoryProcedureRegistry(), Substitute.For<IServiceScopeFactory>(), options);

            var ex = await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));

            ex.Message.ShouldBe("SQL-header procedure declarations (*.proc.sql) are no longer supported; convert them to *.proc.yaml.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Loader_ProcYmlExtension_IsLoaded()
    {
        var (loader, _, dir) = CreateLoader();
        try
        {
            File.WriteAllText(Path.Combine(dir, "p.proc.yml"), Minimal);
            loader.LoadFromDirectory().ShouldBe(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
