namespace Autheris.Extensions.Tests.Lakehouse;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-03: failed Azure blob stream reads release their HTTP response (mirror of the S3 EXT-8 fix).
/// </summary>
public sealed class AzureResponseDisposalExt03Tests
{
    private sealed class TrackingContent() : StringContent("error")
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

    private static AzureBlobStorageProvider CreateProvider(HttpResponseMessage response)
    {
        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012"));
        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions { AzureAccountName = "account", AzureContainer = "iceberg", AzureAccountKey = key }
            }
        });
        return new AzureBlobStorageProvider(new HttpClient(new Handler(response)), options, NullLogger<AzureBlobStorageProvider>.Instance);
    }

    [Fact]
    public async Task OpenReadStream_NotFound_DisposesTheResponse()
    {
        var content = new TrackingContent();
        var provider = CreateProvider(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = content });

        await Should.ThrowAsync<FileNotFoundException>(() => provider.OpenReadStreamAsync("abfss://iceberg@account.dfs.core.windows.net/missing.parquet").AsTask());

        content.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task OpenReadStream_ErrorStatus_DisposesTheResponse()
    {
        var content = new TrackingContent();
        var provider = CreateProvider(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = content });

        await Should.ThrowAsync<HttpRequestException>(() => provider.OpenReadStreamAsync("abfss://iceberg@account.dfs.core.windows.net/x.parquet").AsTask());

        content.Disposed.ShouldBeTrue();
    }
}
