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

    private static AccessProfile Profile(string id, string predicate) => new()
    {
        ProfileId = id,
        Name = id,
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        TargetTables = ["erp.main.orders"],
        RowFilterPredicate = predicate
    };

    private static PolicyLintInput Input(
        IEnumerable<AccessProfile>? profiles = null,
        IEnumerable<CasbinLintRule>? casbin = null,
        string sourceType = "PostgreSQL") =>
        new([Orders(sourceType)], (profiles ?? []).ToList(), [], (casbin ?? []).ToList());

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
        var input = Input([Profile("p-good", "region = 'EU'"), Profile("p-bad", "region = 'EU' AND [name] = 'x'")]);

        var report = new PolicyPredicateLinter().Lint(input);
        var (exit, output) = await RunCli(input);

        report.Findings.Count.ShouldBe(1);
        report.Findings[0].PolicyId.ShouldBe("p-bad");
        report.Findings[0].Kind.ShouldBe(PolicyLintKind.AccessProfileRowFilter);
        report.Findings[0].Table.ShouldBe("erp.main.orders");
        exit.ShouldBe(1);
        output.ShouldContain("p-bad");
        output.ShouldContain("erp.main.orders");
    }

    [Fact]
    public async Task Lint_OutputHasNoPolicyValues()
    {
        var input = Input([Profile("p-bad", $"region = '{SecretValue}' AND [name] = '{SecretValue}'")]);

        var (exit, output) = await RunCli(input);

        exit.ShouldBe(1);
        output.ShouldContain("p-bad");
        output.ShouldNotContain(SecretValue);
        output.ShouldNotContain("region");
    }

    [Fact]
    public async Task Lint_AllValid_ExitCode0()
    {
        var input = Input([Profile("p1", "region = 'EU'"), Profile("p2", "id > 5 AND lower(name) = 'a'")]);

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
        var profiles = new[] { Profile("p-split", "split_part(name, '-', 1) = 'a'") };

        // split_part is on the PostgreSQL allowlist only: the lint must apply the allowlist of the table's own dialect.
        new PolicyPredicateLinter().Lint(Input(profiles, sourceType: "PostgreSQL")).IsClean.ShouldBeTrue();
        new PolicyPredicateLinter().Lint(Input(profiles, sourceType: "SqlServer")).Findings.Single().PolicyId.ShouldBe("p-split");
    }

    [Fact]
    public void Lint_ChecksColumnsAgainstTheCatalog()
    {
        var report = new PolicyPredicateLinter().Lint(Input([Profile("p-col", "not_a_column = 'x'")]));

        report.Findings.Single().PolicyId.ShouldBe("p-col");
    }

    [Fact]
    public void Lint_CoversCasbinCorrelatedFilters()
    {
        var valid = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("erp", "main", "customers"),
            DependentTableAlias = "c",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
        };
        var broken = new ConsentRowFilter
        {
            FilterType = RowFilterType.SubqueryCorrelated,
            DependentTable = new TableIdentifier("erp", "main", "customers"),
            DependentTableAlias = "c; DROP TABLE x",
            ForeignKeyColumn = "customer_id",
            PrimaryKeyColumn = "id",
            SubqueryFilterPredicateJson = "{\"c.country\": \"CH\"}"
        };
        var rules = new[]
        {
            new CasbinLintRule("rule-ok", "erp.main.orders", null, valid),
            new CasbinLintRule("rule-bad", "erp.main.orders", null, broken),
            new CasbinLintRule("rule-text-bad", "erp.main.orders", "region = 'a' AND [name] = 'x'", null),
            new CasbinLintRule("rule-text-ok", "erp.main.orders", "region = ${user.region}", null)
        };

        var report = new PolicyPredicateLinter().Lint(Input(casbin: rules));

        report.Findings.Select(f => f.PolicyId).OrderBy(x => x).ShouldBe(["rule-bad", "rule-text-bad"]);
        report.Findings.Single(f => f.PolicyId == "rule-bad").Kind.ShouldBe(PolicyLintKind.CasbinCorrelatedRowFilter);
        report.Findings.Single(f => f.PolicyId == "rule-text-bad").Kind.ShouldBe(PolicyLintKind.CasbinRowFilter);
        report.Checked.ShouldBe(4);
    }

    [Theory]
    [InlineData("--connection-string")]
    [InlineData("--connection")]
    [InlineData("Server=db;Database=gov;Password=hunter2")]
    [InlineData("Data Source=gov.db")]
    public async Task Lint_RejectsConnectionStringArgument(string arg)
    {
        var (exit, output) = await RunCli(Input([Profile("p1", "region = 'EU'")]), [arg, "Server=db;Password=hunter2"]);

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

/// <summary>REQ-GOV-129 (A-8, ST-129-18): a caller placeholder is only valid in a Virtual Filter.</summary>
[Trait("Category", "Security")]
public sealed class PolicyPredicateLintCallerPlaceholderTests
{
    private static TableMetadata Orders() => new()
    {
        Table = new Table { SourceType = "PostgreSQL", SourceName = "erp", SchemaName = "main", TableName = "orders" },
        Identifier = new TableIdentifier("erp", "main", "orders"),
        Columns = [new TableColumn { ColumnName = "region", DataType = "varchar" }]
    };

    [Theory]
    [InlineData("region = {{caller.dept}}")]
    [InlineData("region = '{{caller.dept}}'")]
    [InlineData("region = '__autheris_caller__dept'")]
    public void AccessProfile_WithAPlaceholder_IsAFinding(string predicate)
    {
        var profile = new AccessProfile { ProfileId = "p", Name = "p", TenantId = Autheris.Tests.Common.TestTenants.Default, TargetTables = ["erp.main.orders"], RowFilterPredicate = predicate };
        var report = new PolicyPredicateLinter().Lint(new PolicyLintInput([Orders()], [profile], [], []));
        report.Findings.ShouldHaveSingleItem().Code.ShouldBe(PolicyPredicateLinter.CodeCallerPlaceholderNotAllowed);
    }

    [Fact]
    public void CasbinRlsFilter_WithAPlaceholder_IsAFinding_AndTheFindingCarriesNoText()
    {
        var rule = new CasbinLintRule("rule-1", "erp.main.orders", "region = {{caller.dept}}", null);
        var report = new PolicyPredicateLinter().Lint(new PolicyLintInput([Orders()], [], [], [rule]));
        var finding = report.Findings.ShouldHaveSingleItem();
        finding.Code.ShouldBe("CALLER_PLACEHOLDER_NOT_ALLOWED");
        finding.Kind.ShouldBe(PolicyLintKind.CasbinRowFilter);
        finding.ToString().ShouldNotContain("dept");
    }
}
