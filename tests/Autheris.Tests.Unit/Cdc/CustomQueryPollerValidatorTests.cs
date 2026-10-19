namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Validation;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

public sealed class CustomQueryPollerValidatorTests
{
    private readonly ISqlEngine _sqlEngine = FastSqlEngine.Default;
    private readonly CustomQueryPollerValidator _validator;

    public CustomQueryPollerValidatorTests()
    {
        _validator = new CustomQueryPollerValidator(_sqlEngine);
    }

    private static CustomQueryPollerStreamOptions ValidOptions() => new()
    {
        StreamName = "crm_customers",
        Table = "crm.dbo.customers",
        QueryTemplate = "SELECT id, name, updated_at FROM customers WHERE (updated_at > @p_wm_updated_at OR id > @p_wm_id) ORDER BY updated_at ASC, id ASC",
        KeyColumns = ["id"],
        Watermarks =
        [
            new() { Name = "updated_at", Type = WatermarkDimensionType.DateTimeOffset, ParameterName = "p_wm_updated_at", Column = "updated_at", InitialValue = "1970-01-01T00:00:00Z" },
            new() { Name = "id", Type = WatermarkDimensionType.Int64, ParameterName = "p_wm_id", Column = "id", InitialValue = "0" }
        ],
        SoftDelete = new() { Enabled = true, Column = "is_deleted", Strategy = SoftDeleteStrategy.BooleanFlag }
    };

    [Fact]
    public void ValidOptions_PassesValidation()
    {
        var options = ValidOptions();
        var result = _validator.ValidatePollerOptions(options);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingStreamName_FailsValidation(string? streamName)
    {
        var options = ValidOptions();
        var invalid = new CustomQueryPollerStreamOptions
        {
            StreamName = streamName!,
            Table = options.Table,
            QueryTemplate = options.QueryTemplate,
            KeyColumns = options.KeyColumns,
            Watermarks = options.Watermarks
        };

        var result = _validator.ValidatePollerOptions(invalid);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("StreamName is required"));
    }

    [Fact]
    public void MissingKeyColumns_FailsValidation()
    {
        var options = ValidOptions();
        var invalid = new CustomQueryPollerStreamOptions
        {
            StreamName = options.StreamName,
            Table = options.Table,
            QueryTemplate = options.QueryTemplate,
            KeyColumns = [],
            Watermarks = options.Watermarks
        };

        var result = _validator.ValidatePollerOptions(invalid);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("declared primary key column"));
    }

    [Fact]
    public void MissingWatermarks_FailsValidation()
    {
        var options = ValidOptions();
        var invalid = new CustomQueryPollerStreamOptions
        {
            StreamName = options.StreamName,
            Table = options.Table,
            QueryTemplate = options.QueryTemplate,
            KeyColumns = options.KeyColumns,
            Watermarks = []
        };

        var result = _validator.ValidatePollerOptions(invalid);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("watermark dimension is required"));
    }

    [Theory]
    [InlineData("bad-name!")]
    [InlineData("bad name")]
    [InlineData("updated@at")]
    public void WatermarkNameWithSpecialChars_FailsValidation(string badName)
    {
        var options = ValidOptions();
        options.Watermarks =
        [
            new() { Name = badName, Type = WatermarkDimensionType.Int64, ParameterName = "p_wm_val", Column = "col", InitialValue = "0" }
        ];

        var result = _validator.ValidatePollerOptions(options);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("Must match ^[a-zA-Z0-9_]+$"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingInitialValue_FailsValidation(string? initialValue)
    {
        var options = ValidOptions();
        options.Watermarks =
        [
            new() { Name = "updated_at", Type = WatermarkDimensionType.DateTimeOffset, ParameterName = "p_wm_updated_at", Column = "updated_at", InitialValue = initialValue! }
        ];

        var result = _validator.ValidatePollerOptions(options);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("non-empty InitialValue"));
    }

    [Fact]
    public void UnparseableInitialValue_FailsValidation()
    {
        var options = ValidOptions();
        options.Watermarks =
        [
            new() { Name = "updated_at", Type = WatermarkDimensionType.DateTimeOffset, ParameterName = "p_wm_updated_at", Column = "updated_at", InitialValue = "not-a-date" }
        ];

        var result = _validator.ValidatePollerOptions(options);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("cannot be parsed as DateTimeOffset"));
    }

    [Fact]
    public void MissingParameterTokenInQuery_FailsValidation()
    {
        var options = ValidOptions();
        options.QueryTemplate = "SELECT id, name FROM customers WHERE id > 0";

        var result = _validator.ValidatePollerOptions(options);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("does not contain required parameter token"));
    }

    [Fact]
    public void SoftDelete_StringStatusWithoutDeletedValue_FailsValidation()
    {
        var options = ValidOptions();
        options.SoftDelete = new() { Enabled = true, Column = "status", Strategy = SoftDeleteStrategy.StringStatus, DeletedValue = null };

        var result = _validator.ValidatePollerOptions(options);
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("requires a non-empty DeletedValue"));
    }

    [Fact]
    public void ValidateOrThrow_ThrowsGovernedCdcConfigurationException_OnFailure()
    {
        var options = ValidOptions();
        options.KeyColumns = [];

        var ex = Should.Throw<GovernedCdcConfigurationException>(() => _validator.ValidateOrThrow(options));
        ex.Message.ShouldContain("Governed query poller validation failed");
    }
}
