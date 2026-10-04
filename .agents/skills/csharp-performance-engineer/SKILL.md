---
name: csharp-performance-engineer
description: >-
  Gezieltes Aufspüren von Flaschenhälsen, Allokationsoptimierung und Performance-Diagnose in .NET.
  Nutze diesen Skill für Speicher- und GC-Optimierung (Span<T>, Memory<T>, ArrayPool<T>,
  Vermeidung von LOH-Allokationen), Benchmarks mit BenchmarkDotNet, EF Core / Query-Tuning
  und die Diagnose von Thread-Pool-Starvation.
---

# C# & .NET Performance Engineer

Dieser Skill leitet die gezielte Performance-Analyse, Allokationsreduktion und Durchsatzoptimierung in modernen .NET-Anwendungen (insb. .NET 8 / 9 / 10) an.

---

## 1. Speicher- & GC-Optimierung

### Zero-Allocation Patterns
- **Span\<T\> & ReadOnlySpan\<T\>**:
  - Für String-Parsing, Slicing und Byte-Operationen auf dem Stack ohne Heap-Allokation:
    ```csharp
    ReadOnlySpan<char> span = input.AsSpan();
    int colonIndex = span.IndexOf(':');
    var key = span[..colonIndex];
    var value = span[(colonIndex + 1)..];
    ```
- **Memory\<T\> & ReadOnlyMemory\<T\>**:
  - Verwenden, wenn Puffer über `async`-Methodengrenzen hinweg gehalten werden müssen (`Span` ist ein `ref struct` und darf nicht in `async`-Zustandsautomaten liegen).
- **ArrayPool\<T\>.Shared**:
  - Für temporäre Puffer, um GC-Druck zu vermeiden. Puffer **immer** im `finally`-Block zurückgeben:
    ```csharp
    byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    try
    {
        int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
        ProcessBuffer(buffer.AsSpan(0, bytesRead));
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
    ```
- **Large Object Heap (LOH) Vermeidung**:
  - Objekte $\ge 85.000$ Bytes landen im LOH und werden nur in teuren Gen-2-GCs bereinigt.
  - Große Arrays oder Strings streamen, chunking einsetzen oder via `ArrayPool` recyceln.
- **ValueTask\<T\> statt Task\<T\>**:
  - Wenn eine Methode häufig synchron abschließt (z. B. Cache-Hit), allokiert `ValueTask<T>` kein Heap-Objekt.

---

## 2. Benchmarking mit BenchmarkDotNet

Microbenchmarks müssen methodisch sauber aufgebaut sein, um Compiler-Optimierungen und JIT-Dead-Code-Elimination zu berücksichtigen:

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

[MemoryDiagnoser] // Zeigt genaue GC-Allokationen (B/Op, Gen 0/1/2)
[Orderer(BenchmarkDotNet.Order.SummaryOrderPolicy.FastestToSlowest)]
public class ParsingBenchmarks
{
    private string _payload = default!;

    [GlobalSetup]
    public void Setup()
    {
        _payload = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...";
    }

    [Benchmark(Baseline = true)]
    public string SubstringParsing()
    {
        return _payload.Substring(7); // Allokiert neuen String
    }

    [Benchmark]
    public ReadOnlySpan<char> SpanParsing()
    {
        return _payload.AsSpan(7); // 0 B Allokation
    }
}
```
*Tipp:* Benchmarks immer in Release-Konfiguration (`dotnet run -c Release`) ausführen.

---

## 3. EF Core & Datenbank-Query-Tuning

- **`.AsNoTracking()`**:
  - Bei reinen Leseabfragen immer verwenden. Verhindert das Tracking im DbContext und spart bis zu 50% Allokationen und CPU-Zeit:
    ```csharp
    var users = await dbContext.Users.AsNoTracking().Where(u => u.IsActive).ToListAsync(ct);
    ```
- **Projektionen (`.Select(...)`)**:
  - Nur die benötigten Spalten abfragen; keine ganzen Entitäten mit 40 Feldern laden, wenn nur ID und Name benötigt werden.
- **Cartesian Explosion mit `.AsSplitQuery()` verhindern**:
  - Bei Abfragen mit mehreren 1:N `.Include(...)`-Verknüpfungen generiert ein einzelner SQL-Join ein riesiges kartesisches Produkt. `.AsSplitQuery()` trennt die SQL-Queries auf.
- **Chunking bei großen `IN (...)`-Listen**:
  - Datenbanken haben Limits für SQL-Parameter (z. B. SQLite: 999, Oracle: 1000, SQL Server: 2100).
  - Listen in Chunks (z. B. via `IEnumerable.Chunk(500)`) partitionieren.

---

## 4. Asynchronität & Thread-Pool-Starvation

### Symptome von Starvation
- Latenzen steigen sprunghaft an.
- CPU-Auslastung ist niedrig, aber Anfragen stauen sich.
- `ThreadPool.GetAvailableThreads` sinkt kontinuierlich.

### Ursachen & Gegenmaßnahmen
- ❌ **Sync-over-Async vermeiden**:
  ```csharp
  // VERBOTEN:
  var result = DoSomethingAsync().Result;
  var result = DoSomethingAsync().GetAwaiter().GetResult();
  ```
  Blockiert einen Thread-Pool-Worker, während der asynchrone Task auf einen neuen Worker wartet.
- **Asynchron sperren**: Niemals `lock (syncObj)` mit darin liegendem `await` nutzen. Stattdessen `SemaphoreSlim`:
  ```csharp
  await _semaphore.WaitAsync(ct);
  try { await CriticalSectionAsync(ct); }
  finally { _semaphore.Release(); }
  ```
- **`ConfigureAwait(false)`**:
  - In Bibliotheken, Infrastruktur- und Datenzugriffsschichten nutzen, um unnötiges Marshalling auf den Synchronisationskontext zu vermeiden.

---

## 5. Performance-Checkliste

1. [ ] Gibt es unnötige String-Allokationen in Hot-Paths (Schleifen, Parser, Resolver)?
2. [ ] Werden Puffer mit `ArrayPool<T>` oder `MemoryPool<T>` recycelt?
3. [ ] Wurden Read-Only EF-Core-Queries mit `.AsNoTracking()` versehen?
4. [ ] Ist der gesamte I/O-Pfad durchgängig asynchron (kein `Result` / `Wait()`)?
5. [ ] Wurden Microbenchmarks mit `[MemoryDiagnoser]` in Release validiert?
