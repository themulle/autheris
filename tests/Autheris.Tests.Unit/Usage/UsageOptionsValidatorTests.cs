namespace Autheris.Tests.Unit.Usage;

using System.Collections.Generic;
using Autheris.Application.Usage;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class UsageOptionsValidatorTests
{
    private readonly UsageAccountingOptionsValidator _validator = new();

    [Fact]
    public void Default_options_are_valid()
    {
        var options = new UsageAccountingOptions();
        var result = _validator.Validate(null, options);
        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    public void FlushIntervalSeconds_out_of_range_fails(int value)
    {
        var options = new UsageAccountingOptions { FlushIntervalSeconds = value };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("FlushIntervalSeconds");
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1_000_001)]
    public void MaxSeries_out_of_range_fails(int value)
    {
        var options = new UsageAccountingOptions { MaxSeries = value };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxSeries");
    }

    [Theory]
    [InlineData(29)]
    [InlineData(1101)]
    public void RetentionDays_out_of_range_fails(int value)
    {
        var options = new UsageAccountingOptions { RetentionDays = value };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("RetentionDays");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void MaxFlushRetries_out_of_range_fails(int value)
    {
        var options = new UsageAccountingOptions { MaxFlushRetries = value };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxFlushRetries");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    public void MaxGroupsPerUser_out_of_range_fails(int value)
    {
        var options = new UsageAccountingOptions { MaxGroupsPerUser = value };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxGroupsPerUser");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1101)]
    public void MaxReportRangeDays_out_of_range_fails(int value)
    {
        var options = new UsageAccountingOptions { MaxReportRangeDays = value };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxReportRangeDays");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    public void MaxReportRows_out_of_range_fails(int value)
    {
        var options = new UsageAccountingOptions { MaxReportRows = value };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("MaxReportRows");
    }

    [Fact]
    public void ExcludedGroupSidPrefixes_invalid_prefix_fails()
    {
        var options = new UsageAccountingOptions
        {
            ExcludedGroupSidPrefixes = ["X-1-5-", "S-1-1-"]
        };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("ExcludedGroupSidPrefixes");
    }

    [Fact]
    public void IncludedGroupSidPrefixes_invalid_prefix_fails()
    {
        var options = new UsageAccountingOptions
        {
            IncludedGroupSidPrefixes = ["INVALID-PREFIX"]
        };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("IncludedGroupSidPrefixes");
    }

    [Fact]
    public void BillingGroups_with_duplicate_sids_fails()
    {
        var options = new UsageAccountingOptions
        {
            BillingGroups = new Dictionary<string, List<string>>
            {
                ["tenantA"] = ["S-1-5-21-1", "S-1-5-21-1"]
            }
        };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("duplicate SID");
    }

    [Fact]
    public void BillingGroups_with_invalid_sid_fails()
    {
        var options = new UsageAccountingOptions
        {
            BillingGroups = new Dictionary<string, List<string>>
            {
                ["tenantA"] = ["not-a-sid"]
            }
        };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("invalid SID");
    }

    [Fact]
    public void GroupDisplayNames_with_invalid_sid_fails()
    {
        var options = new UsageAccountingOptions
        {
            GroupDisplayNames = new Dictionary<string, string>
            {
                ["invalid-sid"] = "GroupName"
            }
        };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("GroupDisplayNames");
    }

    [Fact]
    public void GroupDisplayNames_with_overlong_name_fails()
    {
        var options = new UsageAccountingOptions
        {
            GroupDisplayNames = new Dictionary<string, string>
            {
                ["S-1-5-21-1"] = new string('x', 257)
            }
        };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("GroupDisplayNames");
    }

    [Fact]
    public void PseudonymizeUsers_without_keyvault_ref_fails()
    {
        var options = new UsageAccountingOptions
        {
            PseudonymizeUsers = true,
            PseudonymizationKeyVaultRef = null
        };
        var result = _validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("PseudonymizationKeyVaultRef");
    }

    [Fact]
    public void PseudonymizeUsers_with_keyvault_ref_succeeds()
    {
        var options = new UsageAccountingOptions
        {
            PseudonymizeUsers = true,
            PseudonymizationKeyVaultRef = "vault://keys/telemetry-pseudonym-key"
        };
        var result = _validator.Validate(null, options);
        result.Succeeded.ShouldBeTrue();
    }
}
