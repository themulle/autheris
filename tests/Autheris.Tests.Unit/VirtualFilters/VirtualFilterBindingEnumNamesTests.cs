namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>The binding enums use one fixed snake_case name map in both directions; parsing needs an exact match.</summary>
public sealed class VirtualFilterBindingEnumNamesTests
{
    [Theory]
    [InlineData(VirtualFilterExecutionStrategy.Adaptive, "adaptive")]
    [InlineData(VirtualFilterExecutionStrategy.ShortCircuitOnly, "short_circuit_only")]
    [InlineData(VirtualFilterExecutionStrategy.DuckDbHashJoin, "duckdb_hash_join")]
    [InlineData(VirtualFilterExecutionStrategy.PushdownOnly, "pushdown_only")]
    public void Strategy_HasTheDocumentedName(VirtualFilterExecutionStrategy value, string name)
        => VirtualFilterBindingValidator.FormatEnum(value).ShouldBe(name);

    [Theory]
    [InlineData(PushdownParameterFormat.CommaSeparated, "comma_separated")]
    [InlineData(PushdownParameterFormat.RepeatedParam, "repeated_param")]
    [InlineData(PushdownParameterFormat.ODataIn, "odata_in")]
    [InlineData(PushdownParameterFormat.PostBatch, "post_batch")]
    public void PushdownFormat_HasTheDocumentedName(PushdownParameterFormat value, string name)
        => VirtualFilterBindingValidator.FormatEnum(value).ShouldBe(name);

    [Theory]
    [InlineData(UncoveredPolicy.Deny, "deny")]
    [InlineData(UncoveredPolicy.Skip, "skip")]
    public void Uncovered_HasTheDocumentedName(UncoveredPolicy value, string name)
        => VirtualFilterBindingValidator.FormatEnum(value).ShouldBe(name);

    [Fact]
    public void EveryValueOfEveryEnum_RoundTrips()
    {
        RoundTrip<VirtualFilterExecutionStrategy>();
        RoundTrip<PushdownParameterFormat>();
        RoundTrip<UncoveredPolicy>();
    }

    private static void RoundTrip<T>() where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            var name = VirtualFilterBindingValidator.FormatEnum(value);
            VirtualFilterBindingValidator.ParseEnum<T>(name, "field").ShouldBe(value);
        }
    }

    [Theory]
    [InlineData("DuckDbHashJoin")]      // PascalCase
    [InlineData("duckdbhashjoin")]      // underscores stripped
    [InlineData("DUCKDB_HASH_JOIN")]    // wrong case
    [InlineData("duck_db_hash_join")]   // the former echo
    [InlineData("duckdb-hash-join")]
    [InlineData(" adaptive")]
    [InlineData("2")]                   // numeric
    [InlineData("")]
    [InlineData("turbo")]
    public void ParseEnum_RejectsEverythingButTheExactName_WithAStableCode(string value)
    {
        var ex = Should.Throw<VirtualFilterBindingException>(() => VirtualFilterBindingValidator.ParseEnum<VirtualFilterExecutionStrategy>(value, "strategy"));

        ex.Code.ShouldBe(VirtualFilterErrorCodes.BindingInvalid);
        ex.Code.ShouldBe("VF_BINDING_INVALID");
        ex.Message.ShouldContain("strategy");
    }
}
