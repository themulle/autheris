# F-GOV-08: Dynamic Schema Contracts & Tag-basierte Projektion (`@tag` / `@inaccessible`)

**Status:** **100% (GA) ✅ (Implementiert & Security-Audited 2026-10-02)**  
**Komponenten:** [`ISchemaContractManager.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Governance/Contracts/ISchemaContractManager.cs), [`SchemaContractManager.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Governance/Contracts/SchemaContractManager.cs), [`SchemaContractFilter.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Governance/Contracts/SchemaContractFilter.cs), [`SchemaContractMiddleware.cs`](file:///root/lis-git/gql/gql/src/Autheris.Api/Middleware/SchemaContractMiddleware.cs), [`SchemaContractsOptions.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Options/GatewayOptions.cs)  
**Referenzen:** [`implementation-plan-welle-2-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-2-2026-10-02.md), [`security-review-welle-2-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/threat-model/security-review-welle-2-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Unternehmen müssen für unterschiedliche Konsumenten (Partner-APIs, B2B-Kunden, mobile Applikationen, interne Entwickler) separate GraphQL-Gateways oder Sub-Schemas pflegen. Dies verursacht Schema-Drift, Redundanz und Sicherheitsrisiken durch versehentliche Offenlegung interner Felder.

## 2. Architektur & Umsetzung
- **Zentrale Schemadefinition mit Standard-Direktiven:**
  - `@tag(name: String!)`: Markiert Felder, Typen und Enums für bestimmte Zielgruppen (z. B. `partner`, `mobile`, `public`).
  - `@inaccessible`: Schließt interne Schnittstellendetails global aus externen Projektionen aus.
- **Contract Projection Engine (`SchemaContractFilter`):**
  - Schneidet SDL-Slices präzise nach `IncludedTags`, `ExcludedTags` und `ExcludeInaccessible`.
  - Pruned verwaiste Typen (Orphan Types), wenn alle ihre Felder gefiltert wurden (außer `Query`).
  - Entfernt interne Direktiven aus dem Ergebnis-Schema, um kein Governance-Leaking zu verursachen.
  - ReDoS-geschützt durch Regex-Timeouts.
- **Dynamisches Routing & Kontextbindung (`SchemaContractMiddleware`):**
  - Auflösung über HTTP-Header `X-Gateway-Contract`, Query-Parameter `?contract=...` oder Benutzer-Claims.
  - Setzt `context.Items["GatewayContract"]` für nachgelagerte Validierungen; weist unbekannte Verträge mit `400 Bad Request` (`INVALID_SCHEMA_CONTRACT`) ab.

## 3. Konfigurationsbeispiel (`appsettings.json`)
```json
{
  "Gateway": {
    "SchemaContracts": {
      "Enabled": true,
      "DefaultContract": "default",
      "Contracts": {
        "public": {
          "IncludedTags": ["public"],
          "ExcludeInaccessible": true
        },
        "partner": {
          "IncludedTags": ["public", "partner"],
          "ExcludeInaccessible": true
        }
      }
    }
  }
}
```

## 4. Business Value & TCO-Vorteil
- **Single Source of Truth:** Ein einziger Supergraph bedient sicher beliebig viele Zielgruppen ohne Drift.
- **Volle Unabhängigkeit von Apollo GraphOS Contracts:** Gleiche Funktionalität ohne teure Enterprise-Lizenzen.
