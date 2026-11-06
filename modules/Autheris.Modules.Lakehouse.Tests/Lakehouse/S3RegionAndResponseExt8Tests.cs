namespace Autheris.Extensions.Tests.Lakehouse;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-8: SigV4 uses the bucket's region (not always us-east-1), and failed reads release their HTTP response.
/// </summary>
public sealed class S3RegionAndResponseExt8Tests
{
    [Theory]
    [InlineData("", "https://s3.eu-central-1.amazonaws.com/b/k", "eu-central-1")]
    [InlineData("", "https://bucket.s3.eu-west-1.amazonaws.com/k", "eu-west-1")]
    [InlineData("", "https://s3-us-west-2.amazonaws.com/b/k", "us-west-2")]
    [InlineData("", "https://s3.amazonaws.com/b/k", "us-east-1")]
    [InlineData("", "http://minio:9000/b/k", "us-east-1")]
    [InlineData("ap-southeast-2", "http://minio:9000/b/k", "ap-southeast-2")]
    public void SigningRegion_IsDerivedFromHostOrConfiguration(string configured, string url, string expected)
    {
        S3LakehouseStorageProvider.ResolveSigningRegion(configured, new Uri(url)).ShouldBe(expected);
    }

    private sealed class TrackingContent() : StringContent("not found")
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

    [Fact]
    public async Task OpenReadStream_NotFound_DisposesTheResponse()
    {
        var content = new TrackingContent();
        var response = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = content };
        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_allow_unsigned_s3_requests = true },
            Lakehouse = new LakehouseOptions { Storage = new LakehouseStorageOptions { S3Bucket = "bucket", S3Endpoint = "http://minio:9000" } }
        });
        var provider = new S3LakehouseStorageProvider(new HttpClient(new Handler(response)), options, NullLogger<S3LakehouseStorageProvider>.Instance);

        await Should.ThrowAsync<FileNotFoundException>(() => provider.OpenReadStreamAsync("s3://bucket/missing.parquet").AsTask());

        content.Disposed.ShouldBeTrue();
    }
}
