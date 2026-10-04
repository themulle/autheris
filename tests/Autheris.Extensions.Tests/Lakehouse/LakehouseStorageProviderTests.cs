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

public sealed class LakehouseStorageProviderTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task S3StorageProvider_ReadTextAsync_ShouldSendSignedRequestAndReturnContent()
    {
        // Arrange
        HttpRequestMessage? interceptedRequest = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            interceptedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
            };
        });

        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    S3Endpoint = "http://minio:9000",
                    S3Bucket = "lakehouse-bucket",
                    S3AccessKey = "minioadmin",
                    S3SecretKey = "miniopassword"
                }
            }
        });

        var httpClient = new HttpClient(handler);
        var provider = new S3LakehouseStorageProvider(httpClient, options, NullLogger<S3LakehouseStorageProvider>.Instance);

        // Act
        var content = await provider.ReadTextAsync("s3://lakehouse-bucket/metadata/v1.metadata.json");

        // Assert
        content.ShouldBe("{\"status\":\"ok\"}");
        interceptedRequest.ShouldNotBeNull();
        interceptedRequest.RequestUri!.ToString().ShouldBe("http://minio:9000/lakehouse-bucket/metadata/v1.metadata.json");
        interceptedRequest.Headers.Contains("Authorization").ShouldBeTrue();
        interceptedRequest.Headers.GetValues("Authorization").First().ShouldStartWith("AWS4-HMAC-SHA256");
        interceptedRequest.Headers.Contains("x-amz-date").ShouldBeTrue();
    }

    [Fact]
    public async Task S3StorageProvider_ExistsAsync_ShouldReturnTrueOnSuccessAndFalseOn404()
    {
        // Arrange
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("exists.json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                warn_allow_unsigned_s3_requests = true
            }
        });
        var httpClient = new HttpClient(handler);
        var provider = new S3LakehouseStorageProvider(httpClient, options, NullLogger<S3LakehouseStorageProvider>.Instance);

        // Act & Assert
        var exists = await provider.ExistsAsync("s3://test-bucket/exists.json");
        var notExists = await provider.ExistsAsync("s3://test-bucket/missing.json");

        exists.ShouldBeTrue();
        notExists.ShouldBeFalse();
    }

    [Fact]
    public async Task AzureBlobStorageProvider_ReadTextAsync_ShouldSendAuthAndReturnContent()
    {
        // Arrange
        HttpRequestMessage? interceptedRequest = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            interceptedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"azure\":\"blob-content\"}", Encoding.UTF8, "application/json")
            };
        });

        var dummyKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012"));
        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    AzureAccountName = "mystorageaccount",
                    AzureContainer = "iceberg",
                    AzureAccountKey = dummyKey
                }
            }
        });

        var httpClient = new HttpClient(handler);
        var provider = new AzureBlobStorageProvider(httpClient, options, NullLogger<AzureBlobStorageProvider>.Instance);

        // Act
        var content = await provider.ReadTextAsync("abfss://iceberg@mystorageaccount.dfs.core.windows.net/metadata/v1.metadata.json");

        // Assert
        content.ShouldBe("{\"azure\":\"blob-content\"}");
        interceptedRequest.ShouldNotBeNull();
        interceptedRequest.Headers.Contains("x-ms-version").ShouldBeTrue();
        interceptedRequest.Headers.Contains("Authorization").ShouldBeTrue();
        interceptedRequest.Headers.GetValues("Authorization").First().ShouldStartWith("SharedKey mystorageaccount:");
    }

    [Fact]
    public async Task CompositeLakehouseStorageProvider_ShouldRouteToProperProvider()
    {
        // Arrange
        var testDir = Path.Combine(AppContext.BaseDirectory, "lakehouse_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        var tempFile = Path.Combine(testDir, "test.json");
        try
        {
            await File.WriteAllTextAsync(tempFile, "{\"source\":\"local\"}");

            var s3Handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"source\":\"s3\"}", Encoding.UTF8, "application/json")
            });
            var azureHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"source\":\"azure\"}", Encoding.UTF8, "application/json")
            });

            var options = Options.Create(new GatewayOptions
            {
                Lakehouse = new LakehouseOptions
                {
                    warn_allow_unsigned_s3_requests = true,
                    Storage = new LakehouseStorageOptions
                    {
                        LocalBasePath = testDir,
                        // SEC H-18: only the configured account may be addressed
                        AzureAccountName = "account"
                    }
                }
            });
            var localProvider = new LocalStorageProvider(options);
            var s3Provider = new S3LakehouseStorageProvider(new HttpClient(s3Handler), options, NullLogger<S3LakehouseStorageProvider>.Instance);
            var azureProvider = new AzureBlobStorageProvider(new HttpClient(azureHandler), options, NullLogger<AzureBlobStorageProvider>.Instance);

            var composite = new CompositeLakehouseStorageProvider(localProvider, s3Provider, azureProvider, options);

            // Act
            var localContent = await composite.ReadTextAsync($"file://{tempFile}");
            var s3Content = await composite.ReadTextAsync("s3://bucket/test.json");
            var azureContent = await composite.ReadTextAsync("abfss://container@account.dfs.core.windows.net/test.json");

            // Assert
            localContent.ShouldBe("{\"source\":\"local\"}");
            s3Content.ShouldBe("{\"source\":\"s3\"}");
            azureContent.ShouldBe("{\"source\":\"azure\"}");
        }
        finally
        {
            if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
        }
    }
}
