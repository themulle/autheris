namespace Autheris.Extensions.Tests.Lakehouse;

using System.Net.Http;
using System.Security;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-5: lakehouse requests are signed for the whole storage account; only allowlisted S3 buckets and Azure
/// containers may be addressed, and an empty allowlist allows nothing (fail-closed).
/// </summary>
public sealed class LakehouseAllowlistExt5Tests
{
    private static IOptions<GatewayOptions> Options(LakehouseStorageOptions storage) =>
        Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_allow_unsigned_s3_requests = true },
            Lakehouse = new LakehouseOptions { Storage = storage }
        });

    [Fact]
    public void S3_WithoutAnyAllowedBucket_RejectsEveryBucket()
    {
        var provider = new S3LakehouseStorageProvider(new HttpClient(), Options(new LakehouseStorageOptions()), NullLogger<S3LakehouseStorageProvider>.Instance);

        Should.Throw<SecurityException>(() => provider.ResolveS3Uri("https://other-tenant.s3.eu-central-1.amazonaws.com/x.json", out _, out _));
    }

    [Fact]
    public void S3_AllowedBucketsOption_IsHonoured()
    {
        var provider = new S3LakehouseStorageProvider(new HttpClient(), Options(new LakehouseStorageOptions { S3AllowedBuckets = ["shared-bucket"] }), NullLogger<S3LakehouseStorageProvider>.Instance);

        provider.ResolveS3Uri("s3://shared-bucket/t/x.json", out var bucket, out _);
        bucket.ShouldBe("shared-bucket");
        Should.Throw<SecurityException>(() => provider.ResolveS3Uri("s3://other-bucket/t/x.json", out _, out _));
    }

    [Fact]
    public void Azure_ContainerOutsideAllowlist_IsRejected()
    {
        var provider = new AzureBlobStorageProvider(new HttpClient(), Options(new LakehouseStorageOptions { AzureAccountName = "account", AzureContainer = "tenant-a" }), NullLogger<AzureBlobStorageProvider>.Instance);

        provider.ResolveAzureUri("abfss://tenant-a@account.dfs.core.windows.net/t/x.json", out _, out var container, out _);
        container.ShouldBe("tenant-a");
        Should.Throw<SecurityException>(() => provider.ResolveAzureUri("abfss://tenant-b@account.dfs.core.windows.net/t/x.json", out _, out _, out _));
        Should.Throw<SecurityException>(() => provider.ResolveAzureUri("https://account.blob.core.windows.net/tenant-b/x.json", out _, out _, out _));
    }

    [Fact]
    public void Azure_WithoutAnyAllowedContainer_RejectsRelativeLocations()
    {
        var provider = new AzureBlobStorageProvider(new HttpClient(), Options(new LakehouseStorageOptions { AzureAccountName = "account" }), NullLogger<AzureBlobStorageProvider>.Instance);

        Should.Throw<SecurityException>(() => provider.ResolveAzureUri("t/x.json", out _, out _, out _));
    }
}
