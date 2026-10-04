# F-SEC-04: Relationship-Based Access Control (ReBAC via OpenFGA / Zanzibar)

**Status:** **100% (GA) ✅ (Universell über alle Datenpunkte implementiert & Security-Audited 2026-10-03)**  
**Komponenten:**
- **Core Engine:** [`IRebacStore.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Security/Rebac/Interfaces/IRebacStore.cs), [`InMemoryRebacStore.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Security/Rebac/Services/InMemoryRebacStore.cs), [`IRebacEvaluator.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Security/Rebac/Interfaces/IRebacEvaluator.cs), [`ZanzibarRebacEvaluator.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Security/Rebac/Services/ZanzibarRebacEvaluator.cs), [`IRebacBatchDataLoader.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Security/Rebac/Interfaces/IRebacBatchDataLoader.cs), [`RebacBatchDataLoader.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Security/Rebac/Services/RebacBatchDataLoader.cs), [`RebacOptions.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Options/GatewayOptions.cs)
- **GraphQL-Enforcement:** [`RebacDirectiveType.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/Directives/RebacDirectiveType.cs) (`@rebac`-Direktive & `RebacFieldMiddleware`)
- **HTTP / REST-Enforcement:** [`RebacEndpointFilter.cs`](file:///root/lis-git/gql/gql/src/Autheris.Api/Security/RebacEndpointFilter.cs) (`.RequireRebac(...)` & `RebacEndpointFilter`)
- **Streaming-Enforcement:** [`StreamRlsPolicyEnforcer.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs) (ReBAC Table/Topic-Check)
- **REST-Management:** [`RebacEndpoints.cs`](file:///root/lis-git/gql/gql/src/Autheris.Api/Endpoints/RebacEndpoints.cs)
**Referenzen:** [`implementation-plan-rebac-enforcement-all-datapoints-2026-10-03.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-rebac-enforcement-all-datapoints-2026-10-03.md), [`security-review-rebac-enforcement-2026-10-03.md`](file:///root/lis-git/gql/gql/docs/threat-model/security-review-rebac-enforcement-2026-10-03.md)

---

## 1. Übersicht & Problemstellung
Klassische RBAC- und ABAC-Systeme scheitern an komplexen, verschachtelten Unternehmenshierarchien (z. B. "User A ist Mitglied von Team B, Team B besitzt Ordner C, Ordner C enthält Dokument D $\rightarrow$ User A darf Dokument D lesen"). Dies führt zu unlösbaren SQL-Joins oder massiven $N+1$-Latenzen bei Abfragen.

## 2. Universelle Durchsetzung über alle Datenpunkte

### A. GraphQL-Datenpunkt (`@rebac`)
* Deklarative Schema-Annotation:
  ```graphql
  type Query {
    getDocument(id: ID!): Document @rebac(relation: "viewer", objectType: "document", objectArg: "id")
  }
  ```
* Die `RebacFieldMiddleware` fängt den Feldaufruf vor dem Resolver ab, ermittelt Identität und Mandant aus dem Request-Kontext und führt über den `IRebacBatchDataLoader` einen Zero-$N+1$-gebatchten Autorisierungscheck durch.
* Bei Verweigerung: Standardisierter Fehlercode `AUTH_NOT_AUTHORIZED` bzw. `AUTH_NOT_AUTHENTICATED`.

### B. HTTP / REST-Datenpunkte (`RequireRebac`)
* Fluent Endpoint Extension für ASP.NET Core Minimal APIs:
  ```csharp
  app.MapGet("/api/v1/datasets/{id}", (string id) => ...)
     .RequireRebac(relation: "reader", objectType: "dataset", paramName: "id", source: RebacParameterSource.Route);
  ```
* `RebacEndpointFilter` prüft Claims, Tenant und Parameter (Route, Query oder Header) und liefert `HTTP 403 Forbidden` oder `HTTP 401 Unauthorized` bei fehlender Berechtigung.

### C. Streaming & WebSocket-Datenpunkt
* `StreamRlsPolicyEnforcer` integriert die Google-Zanzibar-Traversierung in CDC- und Event-Streams für Tabellen- und Themen-Subskriptionen (`subscriber`-Relation).

---

## 3. Sicherheits- & DoS-Härtung
* **Fail-Closed Default:** Fehlende Authentifizierung, unbekannte Objekte oder ungültige Mandanten führen ausnahmslos zur Zugriffsverweigerung.
* **Mandanten-Isolation (Anti-IDOR):** Strikt getrennte Namensräume (`tenant:{id}:...`). Cross-Tenant-Angriffe werden verlässlich geblockt.
* **Cyclic Graph Recursion Guard:** `MaxTraversalDepth = 10` mit strikter Cycle-Detection. Verhindert StackOverflow- und CPU-Exhaustion-Angriffe (Fail-Closed).
* **Information Disclosure Protection:** Fehlermeldungen offenbaren keine internen Graphen-Topologien oder Ordnerstrukturen.

---

## 4. Konfigurationsbeispiel (`appsettings.json`)
```json
{
  "Gateway": {
    "Rebac": {
      "Enabled": true,
      "MaxTraversalDepth": 10,
      "CacheTtlSeconds": 60,
      "MaxCachedDecisions": 50000,
      "EnforceOnStreaming": true
    }
  }
}
```

---

## 5. Security Unit- & Integrationstests
* [`RebacZanzibarTests.cs`](file:///root/lis-git/gql/gql/tests/Autheris.Tests.Unit/Security/RebacZanzibarTests.cs) (8 Tests): Core Engine, Transitive Vererbung, Zyklenschutz, Mandanten-Isolation.
* [`RebacGraphQLSecurityTests.cs`](file:///root/lis-git/gql/gql/tests/Autheris.Tests.Unit/Security/RebacGraphQLSecurityTests.cs) (6 Tests): `@rebac`-Direktive, Direct & Transitive Access, IDOR-Protection, Unauthenticated Denial, Fail-Closed on Missing Arguments.
* [`RebacHttpEndpointSecurityTests.cs`](file:///root/lis-git/gql/gql/tests/Autheris.Tests.Unit/Security/RebacHttpEndpointSecurityTests.cs) (5 Tests): HTTP Endpoint Filter, 200/401/403 Status-Codes, Multi-Tenant Boundaries, Parameter-Sources.
* Gesamtergebnis: **19 / 19 ReBAC-Tests erfolgreich; 1.575 / 1.575 Gesamt-Tests grün**.
