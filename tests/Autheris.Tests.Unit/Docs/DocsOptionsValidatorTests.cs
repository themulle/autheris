namespace Autheris.Tests.Unit.Docs;

using Autheris.Application.Docs.Services;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class DocsOptionsValidatorTests
{
    private readonly DocsOptionsValidator _validator = new();

    [Fact]
    public void Valid_default_options_succeed()
    {
        var options = new DocsOptions();
        var result = _validator.Validate(null, options);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_non_positive_limits(int nonPositive)
    {
        var options = new DocsOptions
        {
            MaxDocumentBytes = nonPositive,
            MaxTitleLength = nonPositive,
            MaxTags = nonPositive,
            MaxTagLength = nonPositive,
            MaxBundleBytes = nonPositive,
            MaxBundleEntries = nonPositive,
            MaxBundleUncompressedBytes = nonPositive,
            TombstoneRetentionHours = nonPositive,
            Quota = new DocsQuotaOptions
            {
                MaxDocumentsPerTenant = nonPositive,
                MaxBytesPerTenant = nonPositive
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("MaxDocumentBytes"));
        result.Failures.ShouldContain(f => f.Contains("MaxTitleLength"));
        result.Failures.ShouldContain(f => f.Contains("MaxTags"));
        result.Failures.ShouldContain(f => f.Contains("MaxTagLength"));
        result.Failures.ShouldContain(f => f.Contains("MaxBundleBytes"));
        result.Failures.ShouldContain(f => f.Contains("MaxBundleEntries"));
        result.Failures.ShouldContain(f => f.Contains("MaxBundleUncompressedBytes"));
        result.Failures.ShouldContain(f => f.Contains("TombstoneRetentionHours"));
        result.Failures.ShouldContain(f => f.Contains("Quota:MaxDocumentsPerTenant"));
        result.Failures.ShouldContain(f => f.Contains("Quota:MaxBytesPerTenant"));
    }

    [Fact]
    public void Rejects_document_limit_above_tenant_byte_quota()
    {
        var options = new DocsOptions
        {
            MaxDocumentBytes = 10_000_000,
            Quota = new DocsQuotaOptions
            {
                MaxBytesPerTenant = 5_000_000
            }
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("Docs:MaxDocumentBytes must not exceed Docs:Quota:MaxBytesPerTenant"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    public void Rejects_index_refresh_seconds_outside_range(int refreshSeconds)
    {
        var options = new DocsOptions
        {
            IndexRefreshSeconds = refreshSeconds
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("Docs:IndexRefreshSeconds must be between 5 and 600"));
    }

    [Fact]
    public void Rejects_null_quota()
    {
        var options = new DocsOptions
        {
            Quota = null!
        };

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("Docs:Quota must not be null"));
    }
}
