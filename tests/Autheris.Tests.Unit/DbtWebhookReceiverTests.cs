namespace Autheris.Tests.Unit;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DbtWebhookReceiverTests
{
    private static string ComputeHmacSha256Hex(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void ValidateSignature_WithValidHmac_ReturnsTrue()
    {
        var secret = "super-secret-dbt-token-12345";
        var payload = """{"eventId":"evt_1","eventType":"job_run.completed"}""";
        var signature = ComputeHmacSha256Hex(payload, secret);

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var isValid = receiver.ValidateSignature(payload, signature, secret);
        isValid.ShouldBeTrue();

        // Also test with sha256= prefix
        var isValidWithPrefix = receiver.ValidateSignature(payload, "sha256=" + signature, secret);
        isValidWithPrefix.ShouldBeTrue();
    }

    [Fact]
    public void ValidateSignature_WithInvalidHmac_ReturnsFalse()
    {
        var secret = "super-secret-dbt-token-12345";
        var payload = """{"eventId":"evt_1","eventType":"job_run.completed"}""";
        var invalidSignature = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var isValid = receiver.ValidateSignature(payload, invalidSignature, secret);
        isValid.ShouldBeFalse();
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithValidPayloadAndSignature_ReturnsSuccess()
    {
        var secret = "test-secret-key";
        var now = DateTimeOffset.UtcNow.ToString("O");
        var payload = $$"""
        {
            "eventId": "evt_999",
            "eventType": "job_run.completed",
            "timestamp": "{{now}}",
            "accountId": 42,
            "data": {
                "jobId": 101,
                "jobName": "nightly_marts",
                "runId": 5555,
                "runStatus": "Success",
                "environmentId": 7,
                "dbtVersion": "1.8.0"
            }
        }
        """;
        var signature = "sha256=" + ComputeHmacSha256Hex(payload, secret);

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var result = await receiver.ProcessWebhookAsync(payload, signature);

        result.Success.ShouldBeTrue();
        result.RunId.ShouldBe(5555);
        result.EventType.ShouldBe("job_run.completed");
        result.Message.ShouldContain("Success");
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithSkewedTimestamp_RejectsRequest()
    {
        var secret = "test-secret-key";
        var pastTime = DateTimeOffset.UtcNow.AddMinutes(-30).ToString("O");
        var payload = $$"""
        {
            "eventId": "evt_skewed",
            "eventType": "job_run.completed",
            "timestamp": "{{pastTime}}",
            "data": { "runId": 123 }
        }
        """;
        var signature = "sha256=" + ComputeHmacSha256Hex(payload, secret);

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var result = await receiver.ProcessWebhookAsync(payload, signature);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("replay window");
    }

    [Fact]
    public async Task ProcessWebhookAsync_DuplicateEventId_SkipsDuplicate()
    {
        var secret = "test-secret-key";
        var now = DateTimeOffset.UtcNow.ToString("O");
        var eventId = "evt_dedup_" + Guid.NewGuid().ToString("N");
        var payload = $$"""
        {
            "eventId": "{{eventId}}",
            "eventType": "job_run.completed",
            "timestamp": "{{now}}",
            "data": { "runId": 777 }
        }
        """;
        var signature = "sha256=" + ComputeHmacSha256Hex(payload, secret);

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var first = await receiver.ProcessWebhookAsync(payload, signature);
        first.Success.ShouldBeTrue();

        var duplicate = await receiver.ProcessWebhookAsync(payload, signature);
        duplicate.Success.ShouldBeTrue();
        duplicate.Message.ShouldContain("Duplicate event");
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithInvalidSignature_RejectsRequest()
    {
        var secret = "test-secret-key";
        var payload = """{"eventId":"evt_1"}""";

        var options = Options.Create(new GatewayOptions
        {
            Dbt = new DbtOptions { WebhookSecret = secret }
        });
        var circuitBreaker = Substitute.For<IDbtHealthCircuitBreaker>();
        var receiver = new DbtWebhookReceiver(options, circuitBreaker, NullLogger<DbtWebhookReceiver>.Instance);

        var result = await receiver.ProcessWebhookAsync(payload, "invalid-sig");

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid or missing HMAC signature");
    }
}
