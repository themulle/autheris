namespace Autheris.Tests.Unit.Procedures;

using System.IO;
using System;
using Autheris.Application.Procedures.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Write procedures stay rejected at load until they are enabled with data products.</summary>
public sealed class ProcedureReadOnlyTests
{
    [Theory]
    [Trait("Category", "Security")]
    [InlineData("write")]
    [InlineData("WRITE")]
    public void Parse_ModeWrite_Rejected_SEC_PROC_06(string mode)
    {
        string dir = Path.Combine(Path.GetTempPath(), "autheris-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var registry = new InMemoryProcedureRegistry();
            var loader = new ProcedureDefinitionLoader(registry, Options.Create(new GatewayOptions
            {
                DataSources = ProcedureTestData.SqlServerSources("procedures"),
                SqlEndpoints = new SqlEndpointsOptions { Procedures = new ProcedureEndpointsOptions { Enabled = true, Directory = dir, AllowedSchemas = ["api"] } }
            }));
            string file = Path.Combine(dir, "w.proc.yaml");
            File.WriteAllText(file, $"name: w\nprocedure: api.usp_W\nmode: {mode}\n");

            TestProcedureYaml.Definition(File.ReadAllText(file)).Mode.ShouldBe(ProcedureMode.Write);
            loader.CheckPolicy(TestProcedureYaml.Definition(File.ReadAllText(file))).ShouldBe(ProcedureDefinitionLoader.WriteModeRejectedMessage);
            loader.LoadFromDirectory().ShouldBe(0);
            registry.GetAll().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Write_Mode_Bound_To_Product_Passes_Static_Policy_And_Unbound_Does_Not()
    {
        var loader = new ProcedureDefinitionLoader(new InMemoryProcedureRegistry(), Options.Create(new GatewayOptions
        {
            DataSources = ProcedureTestData.SqlServerSources("procedures"),
            SqlEndpoints = new SqlEndpointsOptions { Procedures = new ProcedureEndpointsOptions { Enabled = true, AllowedSchemas = ["api"], RequireDdlHashForDeclared = false } }
        }));

        var bound = TestProcedureYaml.Definition("procedure: api.usp_W\nmode: write\nschema: sales\nproduct_owner: S-1-5-21-1002\n");

        bound.ProductSchema.ShouldBe("sales");
        bound.ProductOwner.ShouldBe("S-1-5-21-1002");
        loader.CheckPolicy(bound).ShouldNotBe(ProcedureDefinitionLoader.WriteModeRejectedMessage);
        loader.CheckPolicy(TestProcedureYaml.Definition("procedure: api.usp_W\nmode: write\nschema: sales\n")).ShouldBe(ProcedureDefinitionLoader.WriteModeRejectedMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mode: read\n")]
    public void Parse_ModeAbsentOrRead_Accepted(string line)
    {
        TestProcedureYaml.Definition("procedure: api.usp_X\n" + line).Mode.ShouldBe(ProcedureMode.Read);
    }
}
