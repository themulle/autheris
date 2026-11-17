namespace Autheris.Tests.Unit.Audit;

using System;
using System.Security.Cryptography;
using System.Text;
using Autheris.Domain.Audit;
using Shouldly;
using Xunit;

/// <summary>
/// Guards the allocation-free audit hashing path: the digest must stay byte-identical to hashing the UTF-8 bytes of
/// <see cref="AuditCanonicalizer.BuildCanonicalPayload"/>, for SHA-256 and HMAC-SHA-256, with and without a sequence.
/// </summary>
[Trait("Category", "Security")]
public sealed class AuditEntryHashGoldenVectorTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("audit-key-0123456789");
    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 11, 17, 10, 20, 30, TimeSpan.Zero).AddTicks(1234567);
    private const string Id = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Hmac_sequenced_matches_golden_vector()
    {
        string hash = AuditChainEngine.ComputeEntryHash(
            Key, 7, Id, "GENESIS_00", At, "AUTH", "S-1-5-21-1", "dbo.orders", "region", "ALLOW", "trace-1", "{\"a\":\"x|y\\n\"}", "acme");
        hash.ShouldBe("2A5CA71D7B9AD5F14CB6E9E4A8D5399F75C6B0AEFE8127F6D1B09A63F857CFC4");
    }

    [Fact]
    public void Sha256_sequenced_matches_golden_vector()
    {
        string hash = AuditChainEngine.ComputeEntryHash(
            null, 7, Id, "GENESIS_00", At, "AUTH", "S-1-5-21-1", "dbo.orders", "region", "ALLOW", "trace-1", "{\"a\":\"x|y\\n\"}", "acme");
        hash.ShouldBe("0E659110352BD3B211FA331441CDC31635BD6D8688B2D0F1044D6254E33B1FEB");
    }

    [Fact]
    public void Hmac_unsequenced_with_escapes_unicode_and_nulls_matches_golden_vector()
    {
        string hash = AuditCanonicalizer.ComputeEntryHash(
            Key, null, Id, "GENESIS_00", At, "é|\t\r\n\\", "ü😀", null, "", null, null, null, "acme");
        hash.ShouldBe("975787AC0D975F3FBBF3036018929E609E09B5B85F34B530AD1F40D8C958DC1F");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    [InlineData(5000)]
    [InlineData(70000)]
    public void Digest_equals_hash_of_the_canonical_payload_for_any_details_length(int detailsLength)
    {
        var details = new StringBuilder();
        for (int i = 0; i < detailsLength; i++)
        {
            details.Append(i % 7 switch { 0 => '|', 1 => '\\', 2 => '\n', 3 => 'é', 4 => '\t', _ => 'x' });
        }

        string payload = AuditCanonicalizer.BuildCanonicalPayload(
            42, Id, "prev", At, "EV", "S-1", "t", "c", "DENY", "tr", details.ToString(), "tenant");
        byte[] bytes = Encoding.UTF8.GetBytes(payload);

        AuditChainEngine.ComputeEntryHash(Key, 42, Id, "prev", At, "EV", "S-1", "t", "c", "DENY", "tr", details.ToString(), "tenant")
            .ShouldBe(Convert.ToHexString(HMACSHA256.HashData(Key, bytes)));
        AuditChainEngine.ComputeEntryHash(null, 42, Id, "prev", At, "EV", "S-1", "t", "c", "DENY", "tr", details.ToString(), "tenant")
            .ShouldBe(Convert.ToHexString(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Empty_non_null_key_stays_an_hmac_key_in_the_canonicalizer()
    {
        string payload = AuditCanonicalizer.BuildCanonicalPayload(1, Id, "p", At, "E", "a", "t", "c", "d", "tr", "{}", "x");
        AuditCanonicalizer.ComputeEntryHash(Array.Empty<byte>(), 1, Id, "p", At, "E", "a", "t", "c", "d", "tr", "{}", "x")
            .ShouldBe(Convert.ToHexString(HMACSHA256.HashData(Array.Empty<byte>(), Encoding.UTF8.GetBytes(payload))));
    }
}
