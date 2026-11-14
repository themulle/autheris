using System;
using System.Text.Json;
using Autheris.Domain.Audit;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Audit;

public sealed class AuditDetailsBuilderTests
{
    [Fact]
    public void AuditEventTypes_ContainsAllMandatoryEventTypes()
    {
        AuditEventTypes.AuthFailed.ShouldBe("AUTH_FAILED");
        AuditEventTypes.AuthTokenRejected.ShouldBe("AUTH_TOKEN_REJECTED");
        AuditEventTypes.AuthSucceeded.ShouldBe("AUTH_SUCCEEDED");
        AuditEventTypes.AuthBruteForceDetected.ShouldBe("AUTH_BRUTE_FORCE_DETECTED");
        AuditEventTypes.AuthzEndpointDenied.ShouldBe("AUTHZ_ENDPOINT_DENIED");
        AuditEventTypes.RateLimitExceeded.ShouldBe("RATE_LIMIT_EXCEEDED");
        AuditEventTypes.TokenRevokedHit.ShouldBe("TOKEN_REVOKED_HIT");
        AuditEventTypes.CatalogRead.ShouldBe("CATALOG_READ");
        AuditEventTypes.MetadataExport.ShouldBe("METADATA_EXPORT");
        AuditEventTypes.WebSqlQuery.ShouldBe("WEBSQL_QUERY");
        AuditEventTypes.WebSqlQueryDenied.ShouldBe("WEBSQL_QUERY_DENIED");
        AuditEventTypes.WebSqlDmlExecuted.ShouldBe("WEBSQL_DML_EXECUTED");
        AuditEventTypes.WebSqlDmlRejected.ShouldBe("WEBSQL_DML_REJECTED");
        AuditEventTypes.QueryExecutionError.ShouldBe("QUERY_EXECUTION_ERROR");
        AuditEventTypes.EgressShadowCopy.ShouldBe("EGRESS_SHADOW_COPY");
        AuditEventTypes.AuditRead.ShouldBe("AUDIT_READ");
        AuditEventTypes.AuditExport.ShouldBe("AUDIT_EXPORT");
        AuditEventTypes.AuditConfigChanged.ShouldBe("AUDIT_CONFIG_CHANGED");
        AuditEventTypes.AuditPipelineFault.ShouldBe("AUDIT_PIPELINE_FAULT");
        AuditEventTypes.AuditRetentionPurge.ShouldBe("AUDIT_RETENTION_PURGE");
        AuditEventTypes.ServiceStarted.ShouldBe("SERVICE_STARTED");
        AuditEventTypes.ServiceStopped.ShouldBe("SERVICE_STOPPED");
        AuditEventTypes.TableQuery.ShouldBe("TABLE_QUERY");
        AuditEventTypes.StreamSubscribe.ShouldBe("STREAM_SUBSCRIBE");
    }

    [Fact]
    public void Builder_AllowlistedFields_AreRetained()
    {
        var json = new AuditDetailsBuilder()
            .WithField("channel", "REST")
            .WithField("sourceIp", "192.168.1.100")
            .WithField("reasonCode", "TOKEN_EXPIRED")
            .WithField("rows", 42)
            .WithField("bytes", 2048)
            .Build();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("channel").GetString().ShouldBe("REST");
        root.GetProperty("sourceIp").GetString().ShouldBe("192.168.1.100");
        root.GetProperty("reasonCode").GetString().ShouldBe("TOKEN_EXPIRED");
        root.GetProperty("rows").GetInt32().ShouldBe(42);
        root.GetProperty("bytes").GetInt64().ShouldBe(2048);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("token")]
    [InlineData("bearer")]
    [InlineData("secret")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    [InlineData("api_key")]
    public void Builder_SensitiveKeys_AreRedacted(string sensitiveKey)
    {
        var json = new AuditDetailsBuilder()
            .WithField(sensitiveKey, "super_secret_value_12345")
            .Build();

        json.ShouldNotContain("super_secret_value_12345");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty(sensitiveKey).GetString().ShouldBe("[REDACTED]");
    }

    [Fact]
    public void Builder_ControlCharactersAndNewlines_AreSanitized()
    {
        var maliciousInput = "line1\r\nline2\twith\0control\x1b[31mcharacters";
        var json = new AuditDetailsBuilder()
            .WithField("input", maliciousInput)
            .Build();

        json.ShouldNotContain("\r");
        json.ShouldNotContain("\n");
        json.ShouldNotContain("\0");
        json.ShouldNotContain("\x1b");
    }

    [Fact]
    public void Builder_LongValues_AreTruncatedWithIndicator()
    {
        var longString = new string('A', 2000);
        var json = new AuditDetailsBuilder(maxFieldLength: 100)
            .WithField("query", longString)
            .Build();

        using var doc = JsonDocument.Parse(json);
        var val = doc.RootElement.GetProperty("query").GetString()!;
        val.Length.ShouldBeLessThanOrEqualTo(100);
        val.ShouldEndWith("...[TRUNCATED]");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("client_secret")]
    [InlineData("x-api-key")]
    [InlineData("connection_string")]
    [InlineData("passwd")]
    [InlineData("set-cookie")]
    [InlineData("X-Authorization")]
    public void Redacts_by_normalised_substring(string key)
    {
        var json = new AuditDetailsBuilder()
            .WithField(key, "super_secret_value")
            .Build();

        json.ShouldNotContain("super_secret_value");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty(key).GetString().ShouldBe("[REDACTED]");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Redacts_nested_dictionary_and_array_values()
    {
        var nested = new Dictionary<string, object?>
        {
            ["client_secret"] = "nested-secret-val",
            ["items"] = new object?[]
            {
                new Dictionary<string, object?> { ["x-api-key"] = "array-secret-key", ["safe"] = "ok" }
            }
        };

        var json = new AuditDetailsBuilder()
            .WithField("payload", nested)
            .Build();

        json.ShouldNotContain("nested-secret-val");
        json.ShouldNotContain("array-secret-key");
        json.ShouldContain("ok");
        using var doc = JsonDocument.Parse(json);
        var payload = doc.RootElement.GetProperty("payload");
        payload.GetProperty("client_secret").GetString().ShouldBe("[REDACTED]");
        payload.GetProperty("items")[0].GetProperty("x-api-key").GetString().ShouldBe("[REDACTED]");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Keeps_allow_listed_non_secret_keys()
    {
        var json = new AuditDetailsBuilder()
            .WithField("keyset", "cursor-1")
            .WithField("primarykey", "user_id")
            .WithField("key_columns", new[] { "tenant_id", "id" })
            .WithField("idempotency_key_hash", "hash-abc")
            .WithField("idempotency_key", "raw-secret-idempotency")
            .Build();

        json.ShouldContain("cursor-1");
        json.ShouldContain("user_id");
        json.ShouldContain("tenant_id");
        json.ShouldContain("hash-abc");
        json.ShouldNotContain("raw-secret-idempotency");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Depth_beyond_limit_is_truncated()
    {
        var deep = new Dictionary<string, object?>
        {
            ["l2"] = new Dictionary<string, object?>
            {
                ["l3"] = new Dictionary<string, object?>
                {
                    ["l4"] = new Dictionary<string, object?>
                    {
                        ["l5"] = "too-deep-secret"
                    }
                }
            }
        };

        var json = new AuditDetailsBuilder()
            .WithField("l1", deep)
            .Build();

        json.ShouldNotContain("too-deep-secret");
        json.ShouldContain("[TRUNCATED]");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Control_characters_in_nested_strings_are_replaced()
    {
        var nested = new Dictionary<string, object?>
        {
            ["bad\u0085key\u2028z"] = "hello\u0007world\u009f\u2028next\u2029end"
        };

        var json = new AuditDetailsBuilder()
            .WithField("nested", nested)
            .Build();

        using var doc = JsonDocument.Parse(json);
        var obj = doc.RootElement.GetProperty("nested");
        var prop = obj.EnumerateObject().Single();
        prop.Name.ShouldNotContain("\u0085");
        prop.Name.ShouldNotContain("\u2028");
        var val = prop.Value.GetString()!;
        val.ShouldNotContain("\u0007");
        val.ShouldNotContain("\u009f");
        val.ShouldNotContain("\u2028");
        val.ShouldNotContain("\u2029");
    }
}
