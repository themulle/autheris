namespace Autheris.Tests.Unit.Security;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// #487 binding conditions SEC-OI-487-1 and SEC-OI-487-2. The remaining #487 cases live next to their subjects:
/// <c>KeysetCursorEncoderV2Tests</c> (future iat, expiry, v1 downgrade) and <c>ConnectionCursorCallerBindingTests</c>
/// (empty SID, tenant claim vs request tenant).
/// </summary>
[Trait("Category", "Security")]
public sealed class KeysetCursorSecurityTests
{
    private const string Key = "keyset-security-signing-key-0123456789";

    private static readonly KeysetCursorContext Context = new(
        TenantId: "tenant-1",
        DataSource: "sales",
        TableName: "orders",
        Direction: "asc",
        Columns: ["id"],
        Caller: "S-1-5-21-test-caller",
        Schema: "dbo",
        RequestHash: "request-hash-1");

    private static TableMetadata Metadata() => new()
    {
        Identifier = new TableIdentifier("sales", "dbo", "orders"),
        Table = new Table { TableName = "orders", SchemaName = "dbo", SourceName = "sales", SourceType = "PostgreSQL" },
        PrimaryKeyColumns = ["id"],
        Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
    };

    private static IHostEnvironment Env(string name)
    {
        IHostEnvironment env = new TestHostEnvironment(name);
        return env;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Whitespace_or_short_key_fails_startup(string environment)
    {
        var validator = new BulkStreamSigningKeyOptionsValidator(Env(environment));

        foreach (var invalid in new[] { null, "", "   \t\r\n  ", new string('k', 31), "  " + new string('k', 31) + "  ", "changeme-changeme-changeme-changeme" })
        {
            var result = validator.Validate(null, new GatewayOptions
            {
                RestApi = new RestApiOptions { BulkStream = new BulkStreamOptions { SigningKey = invalid } }
            });

            result.Failed.ShouldBeTrue($"key '{invalid}' must fail startup in {environment}");
        }

        Should.Throw<ArgumentException>(() => new KeysetCursorEncoder("   "));
        Should.Throw<ArgumentException>(() => new KeysetCursorEncoder(new string('k', 31)));
    }

    [Fact]
    public void Tampered_signature_returns_uniform_cursor_invalid()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        var encoder = new KeysetCursorEncoder(Key, clock);
        var metadata = Metadata();

        var valid = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 1 }, Context);
        var lastChar = valid[^1];
        var flipped = valid[..^1] + (lastChar == 'A' ? 'B' : 'A');
        var truncatedSignature = valid[..^4];
        var garbage = "v2|00000000|0|not-base64.not-a-signature";

        var failures = new[]
        {
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(flipped, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(truncatedSignature, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(garbage, metadata, Context)),
            Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(valid, metadata, Context with { Caller = "S-1-5-21-other" }))
        };

        foreach (var ex in failures)
        {
            ex.Code.ShouldBe("cursor.invalid");
            ex.Message.ShouldBe(failures[0].Message);
            ex.InnerException.ShouldBeNull();
        }
    }
}
