namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class MultiWatermarkVectorTests
{
    [Fact]
    public void EmptyVector_HasNoDimensions()
    {
        var empty = MultiWatermarkVector.Empty;
        empty.Dimensions.Count.ShouldBe(0);
        empty.GetValue("created_at").ShouldBeNull();
    }

    [Fact]
    public void AdvanceDimension_ReturnsNewInstanceWithUpdatedDimension()
    {
        var vector = MultiWatermarkVector.Empty;
        var advanced = vector.AdvanceDimension("updated_at", "2026-10-19T00:00:00Z");

        vector.Dimensions.Count.ShouldBe(0); // Immutability
        advanced.Dimensions.Count.ShouldBe(1);
        advanced.GetValue("updated_at").ShouldBe("2026-10-19T00:00:00Z");
        advanced.GetValue("UPDATED_AT").ShouldBe("2026-10-19T00:00:00Z"); // Case-insensitivity
    }

    [Fact]
    public void Advance_DateTimeOffset_MonotonicallyAdvancesElementWiseMax()
    {
        var vector = new MultiWatermarkVector(new Dictionary<string, string>
        {
            ["updated_at"] = "2026-10-19T10:00:00Z"
        });

        // Smaller candidate -> does not advance
        var unchanged = vector.Advance("updated_at", "2026-10-19T09:00:00Z", WatermarkDimensionType.DateTimeOffset);
        unchanged.GetValue("updated_at").ShouldBe("2026-10-19T10:00:00Z");

        // Larger candidate -> advances
        var advanced = vector.Advance("updated_at", "2026-10-19T11:00:00Z", WatermarkDimensionType.DateTimeOffset);
        advanced.GetValue("updated_at").ShouldBe("2026-10-19T11:00:00Z");
    }

    [Fact]
    public void Advance_Int64_MonotonicallyAdvancesElementWiseMax()
    {
        var vector = new MultiWatermarkVector(new Dictionary<string, string>
        {
            ["seq"] = "100"
        });

        // Smaller candidate
        var unchanged = vector.Advance("seq", "99", WatermarkDimensionType.Int64);
        unchanged.GetValue("seq").ShouldBe("100");

        // Larger candidate
        var advanced = vector.Advance("seq", "101", WatermarkDimensionType.Int64);
        advanced.GetValue("seq").ShouldBe("101");
    }

    [Fact]
    public void Advance_MultipleDimensions_AdvanceIndependently()
    {
        var vector = new MultiWatermarkVector(new Dictionary<string, string>
        {
            ["created_at"] = "2026-10-19T00:00:00Z",
            ["updated_at"] = "2026-10-19T00:00:00Z",
            ["id"] = "10"
        });

        var advanced = vector
            .Advance("created_at", "2026-10-19T01:00:00Z", WatermarkDimensionType.DateTimeOffset)
            .Advance("updated_at", "2026-10-18T00:00:00Z", WatermarkDimensionType.DateTimeOffset) // Older, ignored
            .Advance("id", "15", WatermarkDimensionType.Int64);

        advanced.GetValue("created_at").ShouldBe("2026-10-19T01:00:00Z");
        advanced.GetValue("updated_at").ShouldBe("2026-10-19T00:00:00Z"); // unchanged
        advanced.GetValue("id").ShouldBe("15");
    }

    [Fact]
    public void JsonSerialization_RoundtripsAccurately()
    {
        var vector = new MultiWatermarkVector(new Dictionary<string, string>
        {
            ["created_at"] = "2026-10-19T01:00:00Z",
            ["id"] = "42"
        });

        var json = vector.ToJson();
        var deserialized = MultiWatermarkVector.FromJson(json);

        deserialized.GetValue("created_at").ShouldBe("2026-10-19T01:00:00Z");
        deserialized.GetValue("id").ShouldBe("42");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-json{")]
    public void FromJson_InvalidOrEmpty_ReturnsEmptyVector(string? json)
    {
        var result = MultiWatermarkVector.FromJson(json);
        result.ShouldNotBeNull();
        result.Dimensions.Count.ShouldBe(0);
    }
}
