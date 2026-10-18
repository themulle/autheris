using System;
using Autheris.Application.Audit;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

[Trait("Category", "Security")]
public sealed class AuditRetentionOptionsValidatorTests
{
    private readonly IHostEnvironment _prodEnvironment;
    private readonly IHostEnvironment _devEnvironment;

    public AuditRetentionOptionsValidatorTests()
    {
        _prodEnvironment = Substitute.For<IHostEnvironment>();
        _prodEnvironment.EnvironmentName.Returns("Production");

        _devEnvironment = Substitute.For<IHostEnvironment>();
        _devEnvironment.EnvironmentName.Returns("Development");
    }

    [Fact]
    public void Validate_Production_ValidDefaults_Passes()
    {
        var validator = new AuditRetentionOptionsValidator(_prodEnvironment);
        var options = new AuditRetentionOptions();

        var result = validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(6)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Production_RetentionDaysBelowFloor_Fails(int retentionDays)
    {
        var validator = new AuditRetentionOptionsValidator(_prodEnvironment);
        var options = new AuditRetentionOptions { RetentionDays = retentionDays };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("SEC-AUD161-01");
        result.FailureMessage.ShouldContain("RetentionDays");
    }

    [Theory]
    [InlineData(6)]
    [InlineData(0)]
    public void Validate_Production_OperationsDaysBelowFloor_Fails(int operationsDays)
    {
        var validator = new AuditRetentionOptionsValidator(_prodEnvironment);
        var options = new AuditRetentionOptions { OperationsDays = operationsDays };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("SEC-AUD161-01");
        result.FailureMessage.ShouldContain("OperationsDays");
    }

    [Theory]
    [InlineData(29)]
    [InlineData(0)]
    public void Validate_Production_DataAccessDaysBelowFloor_Fails(int dataAccessDays)
    {
        var validator = new AuditRetentionOptionsValidator(_prodEnvironment);
        var options = new AuditRetentionOptions { DataAccessDays = dataAccessDays };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("SEC-AUD161-01");
        result.FailureMessage.ShouldContain("DataAccessDays");
    }

    [Theory]
    [InlineData(364)]
    [InlineData(0)]
    public void Validate_Production_SecurityDaysBelowFloor_Fails(int securityDays)
    {
        var validator = new AuditRetentionOptionsValidator(_prodEnvironment);
        var options = new AuditRetentionOptions { SecurityDays = securityDays };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("SEC-AUD161-01");
        result.FailureMessage.ShouldContain("SecurityDays");
    }

    [Theory]
    [InlineData(29)]
    [InlineData(0)]
    public void Validate_Production_AggregatedDaysBelowFloor_Fails(int aggregatedDays)
    {
        var validator = new AuditRetentionOptionsValidator(_prodEnvironment);
        var options = new AuditRetentionOptions { AggregatedDays = aggregatedDays };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("SEC-AUD161-01");
        result.FailureMessage.ShouldContain("AggregatedDays");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void Validate_InvalidBatchSize_Fails(int batchSize)
    {
        var validator = new AuditRetentionOptionsValidator(_prodEnvironment);
        var options = new AuditRetentionOptions { BatchSize = batchSize };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("BatchSize");
    }

    [Fact]
    public void Validate_Development_SubFloorValues_Permitted()
    {
        var validator = new AuditRetentionOptionsValidator(_devEnvironment);
        var options = new AuditRetentionOptions
        {
            RetentionDays = 1,
            OperationsDays = 1,
            DataAccessDays = 1,
            SecurityDays = 1,
            AggregatedDays = 1
        };

        var result = validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_Development_NegativeRetentionDays_Fails()
    {
        var validator = new AuditRetentionOptionsValidator(_devEnvironment);
        var options = new AuditRetentionOptions { RetentionDays = 0 };

        var result = validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("RetentionDays must be greater than zero");
    }
}
