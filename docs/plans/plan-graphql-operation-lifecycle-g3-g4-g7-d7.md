# Architektur- & Implementierungsplan: GraphQL Operation Lifecycle, Thread-Safe OperationId & Memo Cleanup (G-3, G-4, G-7, D-7, R-GQL-3)

**Autor:** Senior C# / .NET Solution Architect  
**Datum:** 2026-10-07  
**Branch:** `feat/ast-target-dialect-generator`  
**Referenzen:** [ADR-017](file:///root/autheris/docs/adr/017-graphql-schema-and-governed-tree-query-service.md), [ADR-004](file:///root/autheris/docs/adr/004-row-level-security-strategy.md), [status-und-umsetzungsplan-2026-10-07.md](file:///root/autheris/docs/plans/status-und-umsetzungsplan-2026-10-07.md)

---

## 1. Ausgangslage & Problemanalyse

In der GraphQL-Governance-Architektur von Autheris übernimmt [`GovernedTreeQueryService`](file:///root/autheris/src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs) das zentrale Ausführen von GraphQL-Selection-Trees als eine einzige governed SQL-Query. Bei der Analyse der Service-Lebenszyklen und Concurrency-Pfade wurden vier eng miteinander verknüpfte Probleme identifiziert:

### 1.1 G-3 & R-GQL-3: Unbeschränkter Memo- & Audit-Lebenszyklus im Scoped Service
* **Symptom:** In [`GovernedTreeQueryService`](file:///root/autheris/src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs) wurden Tabellenzugriffsentscheidungen (`_accessByOperationAndTable`) und Audit-Einträge (`_auditedByOperation`) in Instanz-Dictionaries gecacht.
* **Ursache:** In HTTP-Requests wird der DI-Scope nach jedem Request verworfen. In langlebigen WebSocket-Verbindungen (`graphql-ws` / Subscriptions) bleibt der Scoped Service jedoch über Stunden oder Tage für die gesamte Socket-Session aktiv.
* **Folge:**
  1. **Memory Leak:** Alle Operation-IDs und Tabellenentscheidungen verbleiben dauerhaft im RAM.
  2. **Security Stale Access:** Ändern sich Berechtigungen oder Tenant-Zugehörigkeiten während einer bestehenden WebSocket-Verbindung, werden veraltete Tabellenentscheidungen endlos wiederverwendet, da keine Invalidierung stattfindet.

### 1.2 G-4: Race Condition bei parallelen Root-Resolvern
* **Symptom:** GraphQL-Queries mit mehreren Root-Feldern (z. B. `query { sales_dbo_customers { id } sales_dbo_orders { order_id } }`) erzeugen getrennte Operation-IDs und duplizieren Audit-Logs und Tabellenauflösungen.
* **Ursache:** HotChocolate führt Root-Field-Resolver parallel aus. In [`CatalogGraphQlTypeModule.cs`](file:///root/autheris/src/Autheris.GraphQL/Catalog/CatalogGraphQlTypeModule.cs) wurde `ctx.ContextData.TryGetValue("AutherisOperationId", ...)` ohne Synchronisation geprüft und gesetzt (Check-Then-Set Race Condition).
* **Folge:** Zwei parallel gestartete Root-Resolver sahen keinen vorhandenen Schlüssel, generierten zwei unterschiedliche GUIDs und liefen in getrennten Operation-Scopes.

### 1.3 G-7 & D-7: Fehlende Mandatory Operation-ID & gefährlicher Scoped Fallback
* **Symptom:** [`IGovernedTreeQueryService`](file:///root/autheris/src/Autheris.Application/Sql/Tree/GovernedTreeQueryService.cs) bot eine Überladung `ExecuteAsync(principal, root, headers, ct)` ohne `operationId` an.
* **Ursache:** Fehlte die `operationId`, fiel der Service auf `_defaultOperationId = Guid.NewGuid().ToString("N")` pro Service-Instanz zurück.
* **Folge:** In WebSocket-Verbindungen teilten sich alle Aufrufe ohne explizite `operationId` denselben Fallback-Schlüssel über die gesamte Lebensdauer der Verbindung. Tabellenzugriffe aus der allerersten Query wurden permanent für alle Folgeanfragen wiederverwendet.

---

## 2. Erkenntnisse aus Framework-Analyse & HotChocolate 16

Bei der experimentellen Verifikation von HotChocolate 16 (`16.6.7` / `16.7`) traten wesentliche architektonische Nuancen zutage:

1. **`IResolverContext.ContextData` ist thread-safe sperrbar:**
   * `ctx.ContextData` ist instanzgebunden an den aktuellen Request (`RequestContext` / `Operation`). Alle Field-Resolver derselben GraphQL-Operation teilen sich dasselbe `ContextData`-Objekt.
   * Ein atomarer `lock (ctx.ContextData)` synchronisiert parallele Root-Resolver zuverlässig.

2. **Grenzen von `IMiddlewareContext.RegisterForCleanup` in HotChocolate 16:**
   * Zunächst wurde evaluiert, die HotChocolate-interne Methode `((IMiddlewareContext)ctx).RegisterForCleanup(..., CleanAfter.Request)` zu nutzen.
   * **Erkenntnis:** In HotChocolate 16 delegiert `CleanAfter.Request` an den internen `OperationResultBuilder`. Dieser überträgt Resolver-registrierte Tasks in Standard-Pipelines nicht an das finale `OperationResult._cleanUpTasks`. Bei `await result.DisposeAsync()` werden daher nur interne Buffer (`ResultDocument`, `MemoryArena`) freigegeben.
   * **Architektur-Bewertung:** Die Kopplung der Geschäftslogik-Bereinigung an undokumentierte, versionsempfindliche Casts interner Framework-Typen (`MiddlewareContext`) widerspricht dem Prinzip **Pragmatismus & Anti-Overengineering**.

3. **Pragmatisches Resolver-Lifetime-Tracking via Referenzzählung:**
   * Da alle Root-Resolver einer Operation über `ctx.ContextData` koordiniert werden, kann der Lebenszyklus einer Operation elegant und framework-agnostisch über ein leichtgewichtiges `OperationLifetimeTracker`-Objekt in `ctx.ContextData` verwaltet werden.
   * `ResolveRootQueryAsync` nutzt ein sauberes `try ... finally`:
     - Beim Start wird der Tracker atomar geholt oder erzeugt (`AddRef()`).
     - Im `finally`-Block dekrementiert `tracker.Release()`.
     - Erreicht der Counter `0` (der letzte Root-Resolver der Operation ist abgeschlossen), wird `treeService.ClearOperation(opId)` **sofort und deterministisch** aufgerufen.

4. **Defense in Depth in `GovernedTreeQueryService`:**
   * Ein robuster Core-Service darf sich niemals darauf verlassen, dass externe Aufrufer Cleanup-Methoden fehlerfrei aufrufen.
   * `GovernedTreeQueryService` implementiert daher zwingend ein **Bounded Cache-Modell mit Sliding-TTL**:
     - Jeder Eintrag in `_memos` erhält einen Zeitstempel (`CreatedAtUtc`).
     - Überschreitet die Anzahl aktiver Memos einen Schwellenwert (z. B. > 50) oder sind Memos älter als 5 Minuten, werden sie automatisch purgiert und disposed.
     - Eine harte Obergrenze (z. B. 200 Einträge) verhindert Speichererschöpfung selbst unter DoS-Bedingungen.

---

## 3. Architektur-Entwurf

```
   GraphQL Request (Single or Multi-Root Field)
                 │
                 ▼
   ┌────────────────────────────────────────────────────────┐
   │ CatalogGraphQlTypeModule.ResolveRootQueryAsync         │
   │                                                        │
   │ 1. lock (ctx.ContextData)                              │
   │    Get or Create OperationLifetimeTracker              │
   │    tracker.AddRef()                                    │
   │                                                        │
   │ 2. Execute GovernedTreeQueryService.ExecuteAsync(...)  │
   │    with mandatory tracker.OperationId                  │
   │                                                        │
   │ 3. finally {                                           │
   │       if (tracker.Release())                           │
   │          treeService.ClearOperation(opId);             │
   │    }                                                   │
   └────────────────────────────────────────────────────────┘
                 │
                 ▼
   ┌────────────────────────────────────────────────────────┐
   │ GovernedTreeQueryService (Scoped)                      │
   │                                                        │
   │ • ConcurrentDictionary<string, OperationMemo> _memos   │
   │ • Mandatory non-empty operationId                      │
   │ • EvictStaleMemosIfNecessary() (TTL & Capacity Cap)   │
   │ • ClearOperation(operationId) -> Dispose & Remove      │
   │ • OperationMemo encapsulates AccessByTable & Audited   │
   └────────────────────────────────────────────────────────┘
```

### 3.1 Komponenten & Verantwortlichkeiten

| Komponente | Verantwortung |
|---|---|
| `OperationLifetimeTracker` | Leichtgewichtiges C#-Klasse zur Zählung aktiver Root-Resolver in `ctx.ContextData`. Trägt die eindeutige `OperationId` und meldet via Interlocked-Counter, wenn der letzte Resolver abschließt. |
| `CatalogGraphQlTypeModule` | Atomares Erzeugen / Binden des Trackers an `ctx.ContextData`; deterministischer Aufruf von `ClearOperation` im `finally`-Block des letzten Resolvers. |
| `IGovernedTreeQueryService` | Einheitliche API ohne Scope-Fallback: `ExecuteAsync(..., string operationId, ...)` und `ClearOperation(string operationId)`. |
| `GovernedTreeQueryService` | Bounded In-Memory-Speicher mit isolierten `OperationMemo`-Instanzen; pro-Operation-Sperre (`SemaphoreSlim`); proaktive Eviction verwaister Einträge (Defense in Depth). |

---

## 4. Detaillierter Implementierungsplan

### Phase 1: `GovernedTreeQueryService` & `IGovernedTreeQueryService` (Application Layer)

1. **Interface bereinigen (`IGovernedTreeQueryService`):**
   * Entfernen der parameterlosen Überladung `ExecuteAsync(principal, root, headers, ct)`.
   * Konsolidieren auf:
     ```csharp
     Task<JsonDocument> ExecuteAsync(
         ClaimsPrincipal? principal,
         TreeQueryNode root,
         IReadOnlyDictionary<string, string[]>? requestHeaders,
         string operationId,
         CancellationToken ct = default);

     void ClearOperation(string operationId);
     ```
2. **Klasse anpassen (`GovernedTreeQueryService`):**
   * Löschen von `_accessByOperationAndTable`, `_auditedByOperation`, `_defaultOperationId` und globalem `_memoLock`.
   * Einführung der privaten Klasse `OperationMemo : IDisposable`:
     ```csharp
     private sealed class OperationMemo : IDisposable
     {
         public ConcurrentDictionary<TableIdentifier, ResolvedTableAccess> AccessByTable { get; } = new();
         public ConcurrentDictionary<(TableIdentifier Table, bool Allowed), byte> Audited { get; } = new();
         public SemaphoreSlim Lock { get; } = new(1, 1);
         public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

         public void Dispose() => Lock.Dispose();
     }
     ```
   * State: `ConcurrentDictionary<string, OperationMemo> _memos = new();`.
   * Validierung: `ArgumentException.ThrowIfNullOrWhiteSpace(operationId)`.
   * `ClearOperation(string operationId)`:
     ```csharp
     public void ClearOperation(string operationId)
     {
         if (string.IsNullOrWhiteSpace(operationId)) return;
         if (_memos.TryRemove(operationId, out var memo))
         {
             memo.Dispose();
         }
     }
     ```
   * `EvictStaleMemosIfNecessary()`:
     - Wenn `_memos.Count > 50`: Entferne Memos älter als 5 Minuten.
     - Wenn `_memos.Count > 200`: Entferne älteste Memos unabhängig vom Alter (FIFO-Cap).
   * `Dispose()`:
     - Disposed alle noch vorhandenen Memos in `_memos` und leert das Dictionary.

---

### Phase 2: `CatalogGraphQlTypeModule` (GraphQL Layer)

1. **Interner Tracker `OperationLifetimeTracker` definieren:**
   ```csharp
   private sealed class OperationLifetimeTracker(string operationId)
   {
       public string OperationId { get; } = operationId;
       private int _activeResolvers = 1;

       public void AddRef() => Interlocked.Increment(ref _activeResolvers);

       public bool Release() => Interlocked.Decrement(ref _activeResolvers) == 0;
   }
   ```
2. **In `ResolveRootQueryAsync` integrieren:**
   ```csharp
   OperationLifetimeTracker tracker;
   lock (ctx.ContextData)
   {
       if (!ctx.ContextData.TryGetValue("AutherisOperationTracker", out var trackerObj) ||
           trackerObj is not OperationLifetimeTracker existingTracker)
       {
           tracker = new OperationLifetimeTracker(Guid.NewGuid().ToString("N"));
           ctx.ContextData["AutherisOperationTracker"] = tracker;
       }
       else
       {
           tracker = existingTracker;
           tracker.AddRef();
       }
   }

   try
   {
       var queryNode = GraphQlTreeBuilder.BuildTree(ctx, table, schema, maxResponseRows);
       using var doc = await treeService.ExecuteAsync(principal, queryNode, headers, tracker.OperationId, ctx.RequestAborted)
           .ConfigureAwait(false);

       var root = doc.RootElement.Clone();
       if (root.ValueKind != JsonValueKind.Array)
       {
           return Array.Empty<JsonElement>();
       }

       var list = new List<JsonElement>(root.GetArrayLength());
       foreach (var item in root.EnumerateArray())
       {
           list.Add(item);
       }
       return list;
   }
   finally
   {
       if (tracker.Release())
       {
           treeService.ClearOperation(tracker.OperationId);
       }
   }
   ```

---

### Phase 3: Test-Suiten & TDD-Verifikation

1. **`GovernedTreeQueryServiceTests`:**
   * `ExecuteAsync` mit leerer/Whitespace `operationId` wirft `ArgumentException`.
   * `ClearOperation` entfernt Memos und Audit-Flags; erneuter Aufruf evaluiert Tabellenzugriff frisch.
   * Unterschiedliche `operationId`s teilen keinen Tabellencache.
   * `EvictStaleMemosIfNecessary` bereinigt abgelaufene Memos automatisch bei Überschreitung des Schwellenwerts.
2. **`CatalogGraphQlSchemaTests`:**
   * Test `ExecuteQuery_WithMultipleRootFields_SharesSingleOperationId_AndClearsOperationOnCleanup`:
     - Führt eine Query mit zwei Root-Feldern aus (`sales_dbo_customers` und `sales_dbo_orders`).
     - Verifiziert: Beide Aufrufe von `ExecuteAsync` erhalten exakt dieselbe `operationId`.
     - Verifiziert: Nach Abschluss der Abfrage wird `_treeService.Received(1).ClearOperation(capturedOpId)` exakt einmal aufgerufen.
   * Test auf Fehlerfall:
     - Schlägt ein Root-Resolver fehl, führt der `finally`-Block den Counter-Release trotzdem sauber aus.

---

## 5. Verifikation & Qualitätskriterien

1. **Compilation:** `dotnet build` ohne Fehler oder Warnungen.
2. **Unit Tests:** `dotnet test tests/Autheris.Tests.Unit/Autheris.Tests.Unit.csproj` (alle Tests grün).
3. **Architekturtests:** `dotnet test tests/Autheris.Tests.Architecture/Autheris.Tests.Architecture.csproj` (Scope-Integrität und Layering gewahrt).
4. **Keine Captive Dependencies:** `GovernedTreeQueryService` bleibt Scoped, besitzt aber keinen statischen oder globalen Zustand mehr.
