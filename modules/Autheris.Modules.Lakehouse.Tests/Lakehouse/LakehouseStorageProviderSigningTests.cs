namespace Autheris.Extensions.Tests.Lakehouse;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class LakehouseStorageProviderSigningTests
{
    private static string ComputeExpectedSigV4(
        string method,
        string canonicalUri,
        string canonicalQuery,
        string host,
        string amzDate,
        string region,
        string accessKey,
        string secretKey)
    {
        const string emptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var dateStamp = amzDate[..8];
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{emptyHash}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = $"{method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{emptyHash}";
        var crHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
        var scope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{crHash}";

        static byte[] Hmac(byte[] key, string msg)
        {
            using var h = new HMACSHA256(key);
            return h.ComputeHash(Encoding.UTF8.GetBytes(msg));
        }

        var kDate = Hmac(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), dateStamp);
        var kRegion = Hmac(kDate, region);
        var kService = Hmac(kRegion, "s3");
        var kSigning = Hmac(kService, "aws4_request");
        var sig = Convert.ToHexStringLower(Hmac(kSigning, stringToSign));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={sig}";
    }

    [Fact]
    public async Task S3_ReadTextAsync_PartitionedKeyWithEquals_SignsStrictRfc3986CanonicalUri()
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    S3Bucket = "analytics-lake",
                    S3Region = "eu-central-1",
                    S3AccessKey = "AKIAIOSFODNN7EXAMPLE",
                    S3SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
                }
            }
        });
        var provider = new S3LakehouseStorageProvider(new HttpClient(handler), options, NullLogger<S3LakehouseStorageProvider>.Instance);

        await provider.ReadTextAsync("s3://analytics-lake/data/tenant_id=acme/part-0.parquet");

        handler.Requests.Count.ShouldBe(1);
        var req = handler.Requests[0];
        var amzDate = req.Headers.GetValues("x-amz-date").Single();
        var auth = req.Headers.GetValues("Authorization").Single();

        var expectedAuth = ComputeExpectedSigV4(
            "GET",
            "/analytics-lake/data/tenant_id%3Dacme/part-0.parquet",
            string.Empty,
            "s3.amazonaws.com",
            amzDate,
            "eu-central-1",
            "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");

        auth.ShouldBe(expectedAuth);
    }

    [Fact]
    public async Task S3_ReadTextAsync_Rfc3986ReservedDelimitersAndUtf8_SignsEncodedSegments()
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    S3Endpoint = "http://minio:9000",
                    S3Bucket = "lake",
                    S3AccessKey = "minioadmin",
                    S3SecretKey = "miniopassword"
                }
            }
        });
        var provider = new S3LakehouseStorageProvider(new HttpClient(handler), options, NullLogger<S3LakehouseStorageProvider>.Instance);

        await provider.ReadTextAsync("s3://lake/a=1+2,3:4@5!6$7(8)9*0;end/caf\u00e9-._~file.parquet");

        handler.Requests.Count.ShouldBe(1);
        var req = handler.Requests[0];
        var amzDate = req.Headers.GetValues("x-amz-date").Single();
        var auth = req.Headers.GetValues("Authorization").Single();

        var expectedAuth = ComputeExpectedSigV4(
            "GET",
            "/lake/a%3D1%2B2%2C3%3A4%405%216%247%288%299%2A0%3Bend/caf%C3%A9-._~file.parquet",
            string.Empty,
            "minio:9000",
            amzDate,
            "us-east-1",
            "minioadmin",
            "miniopassword");

        auth.ShouldBe(expectedAuth);
    }

    [Fact]
    public async Task Azure_ReadTextAsync_MissingAccountKey_DefaultOptions_FailsClosedWithoutSending()
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    AzureAccountName = "myaccount",
                    AzureContainer = "iceberg",
                    AzureAccountKey = string.Empty
                }
            }
        });
        var provider = new AzureBlobStorageProvider(new HttpClient(handler), options, NullLogger<AzureBlobStorageProvider>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            provider.ReadTextAsync("abfss://iceberg@myaccount.dfs.core.windows.net/metadata/v1.metadata.json").AsTask());

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Azure_OpenReadStreamAsync_MissingAccountKey_DefaultOptions_FailsClosedWithoutSending()
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    AzureAccountName = "myaccount",
                    AzureContainer = "iceberg"
                }
            }
        });
        var provider = new AzureBlobStorageProvider(new HttpClient(handler), options, NullLogger<AzureBlobStorageProvider>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            provider.OpenReadStreamAsync("abfss://iceberg@myaccount.dfs.core.windows.net/data/part-0.parquet").AsTask());

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Azure_ReadTextAsync_InvalidBase64AccountKey_FailsClosedEvenWhenUnsignedAllowed()
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_allow_unsigned_s3_requests = true },
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    AzureAccountName = "myaccount",
                    AzureContainer = "iceberg",
                    AzureAccountKey = "not-valid-base64!!!"
                }
            }
        });
        var provider = new AzureBlobStorageProvider(new HttpClient(handler), options, NullLogger<AzureBlobStorageProvider>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            provider.ReadTextAsync("abfss://iceberg@myaccount.dfs.core.windows.net/metadata/v1.metadata.json").AsTask());

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Azure_ReadTextAsync_MissingAccountKey_WhenUnsignedExplicitlyAllowed_SendsUnsigned()
    {
        var handler = new CapturingHandler();
        var options = Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { warn_allow_unsigned_s3_requests = true },
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    AzureAccountName = "myaccount",
                    AzureContainer = "iceberg"
                }
            }
        });
        var provider = new AzureBlobStorageProvider(new HttpClient(handler), options, NullLogger<AzureBlobStorageProvider>.Instance);

        await provider.ReadTextAsync("abfss://iceberg@myaccount.dfs.core.windows.net/metadata/v1.metadata.json");

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Headers.Contains("Authorization").ShouldBeFalse();
    }

    [Fact]
    public void S3_KnownAnswerTest_AwsS3OfficialVectors_MatchByteForByte()
    {
        var timestamp = new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);
        const string accessKey = "AKIAIOSFODNN7EXAMPLE";
        const string secretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

        // Official AWS S3 SigV4 docs: GET Bucket Lifecycle (?lifecycle -> lifecycle=)
        using var req1 = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/?lifecycle");
        S3LakehouseStorageProvider.SignAwsSigV4(
            req1,
            HttpMethod.Get,
            req1.RequestUri!,
            accessKey,
            secretKey,
            "us-east-1",
            timestamp);

        req1.Headers.GetValues("Authorization").Single().ShouldBe(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
            "Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543");

        // Official AWS S3 SigV4 docs: GET Bucket List Objects (?prefix=J&max-keys=2 -> sorted query max-keys=2&prefix=J)
        using var req2 = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/?prefix=J&max-keys=2");
        S3LakehouseStorageProvider.SignAwsSigV4(
            req2,
            HttpMethod.Get,
            req2.RequestUri!,
            accessKey,
            secretKey,
            "us-east-1",
            timestamp);

        req2.Headers.GetValues("Authorization").Single().ShouldBe(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
            "Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7");

        // Partitioned key KAT with deterministic timestamp
        var katTimestamp = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        using var req3 = new HttpRequestMessage(HttpMethod.Get, "https://s3.eu-central-1.amazonaws.com/analytics-lake/data/tenant_id=acme/part-0.parquet");
        S3LakehouseStorageProvider.SignAwsSigV4(
            req3,
            HttpMethod.Get,
            req3.RequestUri!,
            accessKey,
            secretKey,
            "eu-central-1",
            katTimestamp);

        req3.Headers.GetValues("Authorization").Single().ShouldBe(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20261121/eu-central-1/s3/aws4_request, " +
            "SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
            "Signature=0ecf0c6a983e632122012b5dbbabf6ee2f6a427e2a61aa49f00a0d45989b0054");
    }

    [Fact]
    public void S3_BuildCanonicalQueryString_SortsAndRfc3986EncodesParameters()
    {
        S3LakehouseStorageProvider.BuildCanonicalQueryString("?prefix=orders/metadata/&list-type=2&flag&eq=a=b")
            .ShouldBe("eq=a%3Db&flag=&list-type=2&prefix=orders%2Fmetadata%2F");
    }

    [Fact]
    public void Azure_KnownAnswerTest_SharedKeyBlobAndContainerList_MatchExpectedHmacSignature()
    {
        var keyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012"));
        var timestamp = new DateTimeOffset(2023, 11, 3, 12, 0, 0, TimeSpan.Zero);

        using var blobReq = new HttpRequestMessage(HttpMethod.Get, "https://myaccount.blob.core.windows.net/iceberg/data/tenant_id=acme/part-0.parquet");
        AzureBlobStorageProvider.ApplySharedKeySignature(blobReq, HttpMethod.Get, blobReq.RequestUri!, "myaccount", keyB64, timestamp);

        blobReq.Headers.GetValues("x-ms-date").Single().ShouldBe("Fri, 03 Nov 2023 12:00:00 GMT");
        blobReq.Headers.GetValues("x-ms-version").Single().ShouldBe("2023-11-03");
        blobReq.Headers.GetValues("Authorization").Single().ShouldBe("SharedKey myaccount:WYEB/hAlwwbtC5pHVZuMfXzAvIzTMWN2RpnMkVzYcgc=");

        using var listReq = new HttpRequestMessage(
            HttpMethod.Get,
            "https://myaccount.blob.core.windows.net/iceberg?restype=container&comp=list&prefix=orders%2Fmetadata%2F&delimiter=%2F");
        AzureBlobStorageProvider.ApplySharedKeySignature(listReq, HttpMethod.Get, listReq.RequestUri!, "myaccount", keyB64, timestamp);

        listReq.Headers.GetValues("Authorization").Single().ShouldBe("SharedKey myaccount:Y/EjZ5kV2+6JnIUr14NvfXh30HK3lAt/Fh4UFi7JS6s=");
    }

    [Fact]
    public async Task S3_And_Azure_ListAsync_ParseXmlListingsCleanly()
    {
        const string s3Xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Contents><Key>orders/metadata/v1.metadata.json</Key></Contents>
              <Contents><Key>orders/metadata/00007-abc.metadata.json</Key></Contents>
              <IsTruncated>false</IsTruncated>
            </ListBucketResult>
            """;
        var s3Handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(s3Xml, Encoding.UTF8, "application/xml")
        });
        var s3Options = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    S3Endpoint = "http://minio:9000",
                    S3Bucket = "lake",
                    S3AccessKey = "ak",
                    S3SecretKey = "sk"
                }
            }
        });
        var s3 = new S3LakehouseStorageProvider(new HttpClient(s3Handler), s3Options, NullLogger<S3LakehouseStorageProvider>.Instance);
        var s3Keys = await s3.ListAsync("s3://lake/orders/metadata");
        s3Keys.ShouldBe(["s3://lake/orders/metadata/v1.metadata.json", "s3://lake/orders/metadata/00007-abc.metadata.json"]);

        const string azXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <EnumerationResults>
              <Blobs>
                <Blob><Name>orders/metadata/v2.metadata.json</Name></Blob>
                <Blob><Name>orders/metadata/v11.metadata.json</Name></Blob>
              </Blobs>
              <NextMarker />
            </EnumerationResults>
            """;
        var azHandler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(azXml, Encoding.UTF8, "application/xml")
        });
        var azKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("12345678901234567890123456789012"));
        var azOptions = Options.Create(new GatewayOptions
        {
            Lakehouse = new LakehouseOptions
            {
                Storage = new LakehouseStorageOptions
                {
                    AzureAccountName = "myaccount",
                    AzureContainer = "iceberg",
                    AzureAccountKey = azKey
                }
            }
        });
        var az = new AzureBlobStorageProvider(new HttpClient(azHandler), azOptions, NullLogger<AzureBlobStorageProvider>.Instance);
        var azBlobs = await az.ListAsync("abfss://iceberg@myaccount.dfs.core.windows.net/orders/metadata");
        azBlobs.ShouldBe([
            "abfss://iceberg@myaccount.dfs.core.windows.net/orders/metadata/v2.metadata.json",
            "abfss://iceberg@myaccount.dfs.core.windows.net/orders/metadata/v11.metadata.json"
        ]);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = responder?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
