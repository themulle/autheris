using Autheris.Domain.Audit;
using BenchmarkDotNet.Attributes;

namespace Autheris.Benchmarks;

/// <summary>Audit entry hashing: HMAC-SHA-256 and SHA-256 over the canonical payload (the per-entry cost of <c>LinkBatch</c>).</summary>
[MemoryDiagnoser]
public class AuditChainBenchmark
{
    private static readonly byte[] Key = "audit-key-0123456789-0123456789"u8.ToArray();
    private static readonly DateTimeOffset At = new(2026, 11, 17, 10, 20, 30, TimeSpan.Zero);
    private const string Details = "{\"table\":\"orders\",\"columns\":[\"id\",\"region\"],\"rows\":120,\"note\":\"x|y\"}";

    [Benchmark]
    public string HmacEntryHash() => AuditChainEngine.ComputeEntryHash(
        Key, 12345, "11111111-2222-3333-4444-555555555555", "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
        At, "DATA_READ", "S-1-5-21-1-2-3-1001", "dbo.orders", "region", "ALLOW", "00-0123456789abcdef-01", Details, "acme");

    [Benchmark]
    public string Sha256EntryHash() => AuditChainEngine.ComputeEntryHash(
        null, 12345, "11111111-2222-3333-4444-555555555555", "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789",
        At, "DATA_READ", "S-1-5-21-1-2-3-1001", "dbo.orders", "region", "ALLOW", "00-0123456789abcdef-01", Details, "acme");
}
