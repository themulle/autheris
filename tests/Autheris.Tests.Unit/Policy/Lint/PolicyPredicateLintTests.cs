using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy.Lint;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Policy.Lint;

/// <summary>WP-X0x (SEC-ADG-26, SEC-X1-16): the pre-deploy lint of every stored row predicate.</summary>
[Trait("Category", "Security")]
public sealed class PolicyPredicateLintTests
{
    private const string SecretValue = "SECRET_VALUE_42";

    private static TableMetadata Orders(string sourceType = "PostgreSQL") => new()
    {
        Table = new Table { SourceType = sourceType, SourceName = "erp", SchemaName = "main", TableName = "orders" },
        Identifier = new TableIdentifier("erp", "main", "orders"),
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "name", DataType = "varchar" },
            new TableColumn { ColumnName = "customer_id", DataType = "int" }
        ]
    };

    private static VirtualFilter Filter(string name, string sql, string source = "erp") => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        Name = name,
        Source = source,
        Sql = sql
    };

    private static PolicyLintInput Input(
        IEnumerable<VirtualFilter>? filters = null,
        string sourceType = "PostgreSQL") =>
        new([Orders(sourceType)], [], (filters ?? []).ToList());

    private static Task<PolicyLintInput> Load(PolicyLintInput input) => Task.FromResult(input);

    private static async Task<(int Exit, string Output)> RunCli(PolicyLintInput input, string[]? args = null, string? connection = "Data Source=x")
    {
        var writer = new StringWriter();
        var exit = await PolicyLintCli.RunAsync(
            args ?? [],
            (_, _) => Load(input),
            name => name == PolicyLintCli.ConnectionStringVariable ? connection : null,
            writer);
        return (exit, writer.ToString());
    }

    [Fact]
    public async Task Lint_ReportsUnparsable_ExitCode1()
    {
        var input = Input([
            Filter("p_good", "from main.orders o where o.id = target.id and o.region = 'EU'"),
            Filter("p_bad", "from main.orders o where o.id = target.id and [o].[name] = 'x'")
        ]);

        var report = new PolicyPredicateLinter().Lint(input);
        var (exit, output) = await RunCli(input);

        report.Findings.Count.ShouldBe(1);
        report.Findings[0].PolicyId.ShouldBe("p_bad");
        report.Findings[0].Kind.ShouldBe(PolicyLintKind.VirtualFilter);
        report.Findings[0].Table.ShouldBe("erp");
        exit.ShouldBe(1);
        output.ShouldContain("p_bad");
        output.ShouldContain("erp");
    }

    [Fact]
    public async Task Lint_OutputHasNoPolicyValues()
    {
        var input = Input([Filter("p_bad", $"from main.orders o where o.region = '{SecretValue}' AND [o].[name] = '{SecretValue}'")]);

        var (exit, output) = await RunCli(input);

        exit.ShouldBe(1);
        output.ShouldContain("p_bad");
        output.ShouldNotContain(SecretValue);
        output.ShouldNotContain("region");
    }

    [Fact]
    public async Task Lint_AllValid_ExitCode0()
    {
        var input = Input([
            Filter("p1", "from main.orders o where o.id = target.id and o.region = 'EU'"),
            Filter("p2", "from main.orders o where o.id = target.id and o.id > 5 and lower(o.name) = 'a'")
        ]);

        var report = new PolicyPredicateLinter().Lint(input);
        var (exit, output) = await RunCli(input);

        report.Checked.ShouldBe(2);
        report.IsClean.ShouldBeTrue();
        exit.ShouldBe(0);
        output.ShouldContain("unparsable=0");
    }

    [Fact]
    public void Lint_UsesProductionFunctionAllowlist()
    {
        var allowed = new[] { Filter("p_lower", "from main.orders o where o.id = target.id and lower(o.name) = 'a'") };
        var disallowed = new[] { Filter("p_split", "from main.orders o where o.id = target.id and split_part(o.name, '-', 1) = 'a'") };

        new PolicyPredicateLinter().Lint(Input(allowed, sourceType: "PostgreSQL")).IsClean.ShouldBeTrue();
        new PolicyPredicateLinter().Lint(Input(disallowed, sourceType: "PostgreSQL")).Findings.Single().PolicyId.ShouldBe("p_split");
    }

    [Fact]
    public void Lint_ChecksColumnsAgainstTheCatalog()
    {
        var report = new PolicyPredicateLinter().Lint(Input([Filter("p_col", "from main.not_a_table o where o.id = target.id and o.region = 'x'")]));

        report.Findings.Single().PolicyId.ShouldBe("p_col");
    }

    [Theory]
    [InlineData("--connection-string")]
    [InlineData("--connection")]
    [InlineData("Server=db;Database=gov;Password=hunter2")]
    [InlineData("Data Source=gov.db")]
    public async Task Lint_RejectsConnectionStringArgument(string arg)
    {
        var (exit, output) = await RunCli(Input([Filter("p1", "from main.orders o where o.id = target.id and o.region = 'EU'")]), [arg, "Server=db;Password=hunter2"]);

        exit.ShouldBe(2);
        output.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task Lint_WithoutConnectionVariable_ExitCode2()
    {
        var (exit, _) = await RunCli(Input(), connection: null);

        exit.ShouldBe(2);
    }
}
