namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Federation.Parameters;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

public sealed class FederatedParameterContractSecurityTests
{
    private const string Value = "SENTINEL-869-VALUE";

    public static TheoryData<string, string> CaseOnlyDuplicates() => new()
    {
        { "p0", "P0" },
        { "@p0", "p0" },
        { "@P0", "@p0" },
        { "Region", "region" },
    };

    [Theory]
    [MemberData(nameof(CaseOnlyDuplicates))]
    [Trait("Category", "Security")]
    public void FederatedParameterContract_CaseOnlyDuplicateKeys_AreRefused(string first, string second)
    {
        // Either key order must be refused: the outcome never depends on which spelling comes first.
        foreach (var (a, b) in new[] { (first, second), (second, first) })
        {
            var raw = new Dictionary<string, object?>(StringComparer.Ordinal) { [a] = Value, [b] = "other" };

            var ex = Should.Throw<ArgumentException>(() => FederatedParameterContract.Create(raw));

            ex.Message.ShouldNotContain(Value);
            ex.Message.ShouldNotContain("other");
            ex.Message.ShouldNotContain(a.TrimStart('@'), Case.Insensitive);
            var failure = SqlFailureClassifier.Classify(ex);
            failure.Kind.ShouldBe(SqlFailureKind.InvalidQuery);
            failure.Code.ShouldBe("BAD_REQUEST");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FederatedParameterContract_ParameterNamedP0_IsAnOrdinaryName()
    {
        var contract = FederatedParameterContract.Create(new Dictionary<string, object?> { ["@p0"] = Value, ["p1"] = 2 });

        contract.HasValues.ShouldBeTrue();
        contract.Names.ShouldBe(["p0", "p1"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DuckDbOlap_ClientNamedSlot_WithoutCompiledBinder_Throws()
    {
        using var engine = new DuckDbOlapEngine(new DuckDbOlapOptions { Enabled = true }, NullLogger<DuckDbOlapEngine>.Instance);
        var slot = new BoundParameter("$1", "p0", 0, null, SqlParameterType.String, ParameterOrigin.ClientNamed, "p0");

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await engine.ExecuteOlapQueryAsync(new OlapQueryRequest("SELECT $1", [], Parameters: [slot])));

        ex.Message.ShouldNotContain(Value);
    }
}
