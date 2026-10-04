# Master-Implementierungsplan: Welle 3 (`F-PERF-12` & `F-SEC-04`)

**Dokument-ID:** `PLAN-WELLE-3-2026-10-02`  
**Datum:** 2026-10-02  
**Autor:** Enterprise Principal Architect  
**Genehmigt von:** Enterprise Security Specialist Team  
**Scope:**  
1. `F-PERF-12`: Incremental Delivery via `@defer` & `@stream` (Multipart Chunked Egress & TTFB Optimierung)  
2. `F-SEC-04`: Relationship-Based Access Control (ReBAC via OpenFGA / Google Zanzibar Model mit Batch-DataLoader)

---

## 1. Ausgangslage & Architekturbild

### 1.1 `F-PERF-12`: Incremental Delivery via `@defer` & `@stream`
- **Problem:** Bei komplexen GraphQL-Queries oder kaskadierenden Subgraph-Aufrufen (z. B. langsame Finanz-Ratings oder Inventar-Live-Status) blockiert das langsamste Feld die gesamte HTTP-Antwort. Frontends (Web/Mobile) erhalten keinen First-Paint ("White Screen of Death").
- **Lösung:** Unterstützung des GraphQL Incremental Delivery RFCs (`@defer` auf Fragmenten, `@stream` auf Listenfeldern):
  - Primärdaten (`initial chunk`) werden unmittelbar als HTTP 200 Chunk mit `hasNext: true` übergeben.
  - Asynchrone Teilbäume folgen in nachgelagerten Chunks (`incremental: [...]`, `hasNext: false`) über dieselbe Verbindung mit `Content-Type: multipart/mixed; boundary="-"`.
  - Strikte Timeouts und Obergrenzen (`MaxConcurrentStreamsPerClient`, `MaxIncrementalChunks`) schützen den Kestrel-Server vor Verbindungserschöpfung.

### 1.2 `F-SEC-04`: ReBAC (Relationship-Based Access Control)
- **Problem:** Rollenbasierte Modelle (RBAC/ABAC) können verschachtelte Berechtigungsstrukturen (z. B. "User A ist Editor von Ordner X, Ordner X enthält Dokument Y $\rightarrow$ User A darf Dokument Y lesen") nur über komplexe, langsame SQL-Joins abbilden. Bei GraphQL-Listenabfragen mit hunderten Elementen führt dies zum klassischen $N+1$-Autorisierungs-Bottleneck.
- **Lösung:** Google Zanzibar / OpenFGA Beziehungsmodell:
  - Tupel-Format: `(TenantId, User, Relation, Object)` (z. B. `tenant-1, user:alice, viewer, doc:101`).
  - Graph-basierte Relationsauflösung mit transitiver Vererbung (`viewer` $\leftarrow$ `editor` $\leftarrow$ `owner`).
  - **Batch-Resolution & DataLoader-Integration:** Anfragen bündeln hunderte Tupel in einem einzigen Batch-Check (`BatchCheckAsync`), gepuffert durch einen lokalen Lock-free Cache (`MemoryCache` mit TTL).
  - **Multi-Tenant-Boundary:** Tupel verschiedener Mandanten sind strikt getrennt; zyklische Beziehungsdefinitionen werden durch einen Recursion-Guard (`MaxDepth = 10`) abgefangen.

---

## 2. Detaillierte Komponenten-Architektur

### 2.1 Paket- & Klassenstruktur

```
src/Autheris.Domain/
├── Options/
│   └── GatewayOptions.cs                // Erweiterung um IncrementalDeliveryOptions & RebacOptions
└── Model/
    └── RebacModels.cs                   // RebacTuple, RebacCheckRequest, RebacCheckResult

src/Autheris.Application/
├── Performance/
│   └── IncrementalDelivery/
│       ├── IIncrementalDeliveryService.cs
│       └── IncrementalDeliveryService.cs
└── Security/
    └── Rebac/
        ├── Interfaces/
        │   ├── IRebacStore.cs
        │   ├── IRebacEvaluator.cs
        │   └── IRebacBatchDataLoader.cs
        ├── Services/
        │   ├── InMemoryRebacStore.cs
        │   ├── ZanzibarRebacEvaluator.cs
        │   └── RebacBatchDataLoader.cs
        └── Directives/
            └── RebacAuthorizeAttribute.cs

src/Autheris.Api/
├── Middleware/
│   ├── IncrementalDeliveryMiddleware.cs
│   └── RebacAuthorizationMiddleware.cs
└── Endpoints/
    └── RebacEndpoints.cs                // REST APIs: /api/v1/rebac/tuples, /api/v1/rebac/check
```

---

## 3. Sicherheits- & Härtungsvorgaben (Security Expert Alignment)

1. **Slowloris & Connection Exhaustion Guard (`F-PERF-12`):**
   - Jede Deferred-Connection muss durch ein striktes Gesamt-Timeout (`MaxDeferredExecutionTimeMs`, Standard: 30.000 ms) begrenzt sein.
   - Client-Disconnects (`HttpContext.RequestAborted`) müssen sofort alle laufenden Hintergrund-Tasks hart abbrechen.
   - Pro IP/Tenant gilt ein Limit paralleler Streaming-Verbindungen (`MaxConcurrentStreamsPerClient`, Standard: 10).
2. **ReDoS & Multipart Injection Protection (`F-PERF-12`):**
   - Boundary-Strings werden synthetisch erzeugt (`-`); Benutzereingaben dürfen niemals als Boundary-Marker interpretiert werden.
3. **ReBAC Recursion Guard & Cyclic Graph DoS (`F-SEC-04`):**
   - Graph-Traversierung limitiert auf `MaxTraversalDepth = 10`. Bei Überschreitung oder Erkennung von Zyklen erfolgt sofort `Decision.Denied` (Fail-Closed).
4. **Mandanten-Isolation & IDOR-Prävention (`F-SEC-04`):**
   - Endpunkte `/api/v1/rebac/tuples` und `/api/v1/rebac/check` erzwingen Mandanten-Zugehörigkeit. Nur `ClusterAdmin` darf mandantenübergreifend agieren.
5. **Cache-Poisoning & Invalidation:**
   - ReBAC-Cache-Keys setzen sich aus `(TenantId, User, Relation, Object)` zusammen. Bei Tupel-Schreiboperationen (`WriteTuplesAsync` / `DeleteTuplesAsync`) wird der lokale Cache des Mandanten invalidiert.

---

## 4. Phasenplan & Abnahmekriterien

- **Phase 1 (Architektur & Security Härtung):** Vollständige Definition der Modelle, Interfaces, Sicherheitsanforderungen und Unit-Test-Spezifikationen.
- **Phase 2 (Implementierung):** Umsetzung im Core Gateway (`src/Autheris.Domain`, `Application`, `Api`).
- **Phase 3 (Testing & Verifikation):** Erstellung von mindestens 15 dedizierten Security- und Funktions-Unit-Tests (`IncrementalDeliveryTests.cs`, `RebacZanzibarTests.cs`).
- **Phase 4 (Review & Commit):** Code- & Security-Review durch den Security Expert und finaler Commit.

---

## 5. Security Expert Hardening Addendum: Verbindlicher Security-Testkatalog

Der **Lead Enterprise Security Specialist** fordert folgende zwingende Sicherheits-Unit-Tests vor Abnahme und Freigabe:

### 5.1 Sicherheitsprüfungen für `F-PERF-12` (Incremental Delivery)
1. **`SEC-PERF-01` Cancellation & Resource Cleanup:**
   - *Test:* `IncrementalDelivery_AbortsBackgroundProcessing_WhenClientCancelsToken`
   - *Ziel:* Verhindert Thread- und Socket-Leaks beim Abbruch langsamer Mobil-Clients.
2. **`SEC-PERF-02` Execution Timeout Enforcement:**
   - *Test:* `IncrementalDelivery_EnforcesMaxExecutionTimeout_AndClosesStreamGracefully`
   - *Ziel:* Abwehr von Slowloris-Angriffen über künstlich verzögerte Subgraphs.
3. **`SEC-PERF-03` Max Chunks Limit Guard:**
   - *Test:* `IncrementalDelivery_TerminatesWithWarning_WhenMaxChunksExceeded`
   - *Ziel:* Schutz vor Unbounded Chunk Flooding DoS.
4. **`SEC-PERF-04` Multipart Header Sanitization:**
   - *Test:* `IncrementalDelivery_FormatsMultipartChunk_WithStrictBoundaryEscaping`
   - *Ziel:* Ausschluss von Header- und Boundary-Injection in HTTP-Streams.

### 5.2 Sicherheitsprüfungen für `F-SEC-04` (ReBAC / OpenFGA)
1. **`SEC-REBAC-01` Cyclic Graph Recursion Guard (Fail-Closed):**
   - *Test:* `RebacEvaluator_PreventsStackOverflow_OnCyclicRelationships_AndDenies`
   - *Ziel:* Verhindert Denial of Service (StackOverflowException / CPU-Sättigung) bei zirkulären Tupeln (`docA parent docB parent docA`).
2. **`SEC-REBAC-02` Multi-Tenant Tuple Isolation (IDOR-Schutz):**
   - *Test:* `RebacStore_EnforcesStrictTenantBoundary_DeniesCrossTenantTupleAccess`
   - *Ziel:* Verhindert, dass Mandant A Beziehungen auf Objekten von Mandant B definiert oder abfragt.
3. **`SEC-REBAC-03` Transitive Permission Inheritance:**
   - *Test:* `RebacEvaluator_CorrectlyInheritsPermissions_AcrossRelationHierarchy`
   - *Ziel:* Verifiziert, dass `owner -> editor -> viewer` korrekt vererbt wird, während nicht deklarierte Relationen strikt abgelehnt werden.
4. **`SEC-REBAC-04` Batch DataLoader De-Duplication & Zero-N+1:**
   - *Test:* `RebacDataLoader_BatchesMultipleTupleChecks_IntoSingleEvaluation`
   - *Ziel:* Garantiert die Eliminierung von $N+1$-Netzwerklatenzen bei Objektlisten.
5. **`SEC-REBAC-05` Cache Poisoning & Invalidation Integrity:**
   - *Test:* `RebacEvaluator_InvalidatesCachedDecisions_WhenTuplesAreDeleted`
   - *Ziel:* Verhindert verwaiste Berechtigungen im Cache nach dem Entzug von Rollen/Rechten.
