---
name: csharp-performance-engineer
description: >-
  Performance diagnostics and allocation optimization in .NET 10. Guides zero-allocation patterns
  (Span, Memory, ArrayPool), LOH avoidance, BenchmarkDotNet profiling, EF Core query tuning, and thread-pool starvation prevention.
---

# C# & .NET Performance Engineer

Guides performance analysis, allocation elimination, and throughput optimization in .NET 10.

---

## 1. Memory & GC Optimization

- **`Span<T>` & `ReadOnlySpan<T>`:** Zero-allocation slicing, parsing, and byte transforms on the stack:
  ```csharp
  ReadOnlySpan<char> span = input.AsSpan();
  int idx = span.IndexOf(':');
  var key = span[..idx];
  var val = span[(idx + 1)..];
  ```
- **`Memory<T>` & `ReadOnlyMemory<T>`:** Use across `async` boundaries (`Span` cannot be a field in async state machines).
- **`ArrayPool<T>.Shared`:** Rent buffers for temporary I/O; always return in `finally`:
  ```csharp
  byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
  try {
      int read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
      ProcessBuffer(buffer.AsSpan(0, read));
  } finally {
      ArrayPool<byte>.Shared.Return(buffer);
  }
  ```
- **Avoid Large Object Heap (LOH):** Objects $\ge 85,000$ bytes go to LOH (Gen 2 GC cost). Stream, chunk, or rent buffers instead.
- **`ValueTask<T>` over `Task<T>`:** For paths that frequently complete synchronously (e.g. cache hits).

---

## 2. Microbenchmarking (BenchmarkDotNet)

Run in Release mode (`dotnet run -c Release`):
```csharp
[MemoryDiagnoser]
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class ParsingBenchmarks {
    private string _payload = default!;
    [GlobalSetup] public void Setup() => _payload = "Bearer eyJhbGciOi...";
    [Benchmark(Baseline = true)] public string Substring() => _payload.Substring(7);
    [Benchmark] public ReadOnlySpan<char> Span() => _payload.AsSpan(7); // 0 B allocation
}
```

---

## 3. Database & Query Tuning

- **`.AsNoTracking()`:** Mandatory on read-only queries (saves up to 50% allocations and CPU).
- **Projections (`.Select(...)`):** Query only required columns; never load wide entities if only ID/Name are needed.
- **`.AsSplitQuery()`:** Prevents cartesian explosion on multiple 1:N `.Include(...)` joins.
- **Chunking Large `IN (...)` Filters:** Chunk large collections (e.g. `.Chunk(500)`) to stay within database parameter limits.

---

## 4. Concurrency & Starvation Prevention

- ❌ **No Sync-Over-Async:** Never invoke `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` (causes thread-pool deadlocks/starvation).
- **Async Synchronization:** Never use `lock (obj)` with `await`. Use `SemaphoreSlim`:
  ```csharp
  await _semaphore.WaitAsync(ct);
  try { await CriticalSectionAsync(ct); }
  finally { _semaphore.Release(); }
  ```
- **`ConfigureAwait(false)`:** Standard in infrastructure, data access, and library pipelines.

---

## 5. Performance Checklist

1. [ ] No string allocations on hot paths (loops, parsers, tokenizers)?
2. [ ] Buffers recycled via `ArrayPool<T>`?
3. [ ] Read-only EF queries configured with `.AsNoTracking()`?
4. [ ] I/O pipeline purely asynchronous (zero blocking waits)?
5. [ ] Benchmarks validated with `[MemoryDiagnoser]`?
