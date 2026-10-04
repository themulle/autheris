# F-PERF-12: Incremental Delivery via `@defer` & `@stream`

**Status:** **100% (GA) ✅ (Implementiert & Security-Audited 2026-10-02)**  
**Komponenten:** [`IIncrementalDeliveryFormatter.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Performance/IncrementalDelivery/IncrementalDeliveryFormatter.cs), [`IncrementalDeliveryManager.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Performance/IncrementalDelivery/IncrementalDeliveryManager.cs), [`IncrementalDeliveryMiddleware.cs`](file:///root/lis-git/gql/gql/src/Autheris.Api/Middleware/IncrementalDeliveryMiddleware.cs), [`IncrementalDeliveryOptions.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Options/GatewayOptions.cs)  
**Referenzen:** [`implementation-plan-welle-3-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-3-2026-10-02.md), [`security-review-welle-3-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/threat-model/security-review-welle-3-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Langsame Teilabfragen, aggregierte Finanzmetriken oder externe Subgraphs blockieren traditionell die gesamte GraphQL-HTTP-Antwort. Frontends (Web/Mobile) bleiben bis zur vollständigen Fertigstellung im Ladezustand ("White Screen").

## 2. Architektur & Umsetzung
- **Standardisiertes Multipart/Mixed Chunk Streaming:**
  - `Content-Type: multipart/mixed; boundary="-"` nach der GraphQL Incremental Delivery Spezifikation.
  - Initialer Chunk liefert Primärdaten sofort mit `hasNext: true`.
  - Asynchrone Fragmente folgen in Streaming-Chunks mit Pfad-Zuordnung (`incremental: [...]`).
  - Finaler Boundary-Marker schließt den Stream sauber ab (`-----`).
- **Slowloris- & Concurrency-Schutz (`IncrementalDeliveryManager`):**
  - Strikte Begrenzung gleichzeitiger Streaming-Verbindungen pro Client (`MaxConcurrentStreamsPerClient`, Standard: 10). Bei Überschreitung erfolgt HTTP 429 (`INCREMENTAL_STREAM_LIMIT_EXCEEDED`).
  - Globales Gesamt-Timeout (`MaxDeferredExecutionTimeMs`, Standard: 30.000 ms) bricht verwaiste Streams ab.
  - Sofortiger Abbruch aller Hintergrund-Tasks bei vorzeitigem Client-Verbindungsabbruch (`RequestAborted`).

## 3. Konfigurationsbeispiel (`appsettings.json`)
```json
{
  "Gateway": {
    "IncrementalDelivery": {
      "Enabled": true,
      "MaxDeferredExecutionTimeMs": 30000,
      "MaxConcurrentStreamsPerClient": 10,
      "MaxIncrementalChunks": 100
    }
  }
}
```

## 4. Business Value
- **Radikal verbesserte Time-to-First-Byte (TTFB):** Schnelle Primärdaten werden in wenigen Millisekunden gerendert.
- **Optimale Mobile-Experience:** Reduzierter Speicherbedarf und flüssige UI-Updates für Mobil-Apps.
