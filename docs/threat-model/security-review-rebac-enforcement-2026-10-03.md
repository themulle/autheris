# Security Review & Härtungs-Vorgaben: Universelle ReBAC-Durchsetzung über alle Datenpunkte

**Datum:** 2026-10-03  
**Rolle:** Security Expert  
**Status:** Genehmigt mit verbindlichen Auflagen  
**Referenz:** [`implementation-plan-rebac-enforcement-all-datapoints-2026-10-03.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-rebac-enforcement-all-datapoints-2026-10-03.md)

---

## 1. Bedrohungsanalyse (STRIDE) für universelles ReBAC

| STRIDE | Bedrohungsszenario | Erforderliche Gegenmaßnahme |
| :--- | :--- | :--- |
| **Spoofing** | Angreifer fälscht Tenant-Header oder User-SID, um fremde Objekte in ReBAC abzufragen. | Strikte Bindung an authentifizierte Claims (`ClaimsPrincipal`). Tenant-Header wird nur bei `ClusterAdmin` akzeptiert, andernfalls zwingend Tenant aus dem signierten JWT-Token. |
| **Tampering** | Manipulation der `objectId` im GraphQL-Argument oder REST-Route (z. B. Path Traversal oder Sonderzeichen). | Sanitizing und Validierung: `objectId` und `relation` dürfen keine Zeilenumbrüche, Null-Bytes oder unzulässige Trennzeichen enthalten. |
| **Repudiation** | ReBAC-Entscheidungen werden bei Denial lautlos verworfen, ohne Audit-Spur. | Strukturierte Security-Warnlogs bei `Access Denied` mit Tenant, Caller-Identity, Zielobjekt und Relation. |
| **Information Disclosure** | Detaillierte Fehlermeldungen verraten Graphtraversierungs-Topologie (z. B. "Ordner XYZ existiert, aber User hat keine Rechte"). | Einheitliche Fehler: HTTP `403 Forbidden` bzw. GraphQL-Code `AUTH_NOT_AUTHORIZED` ohne interne Graphdetails (Generic Denial). |
| **Denial of Service** | Verschachtelte GraphQL-Abfragen mit `@rebac` führen zu rekursiven Graphbomben oder DoS durch $N+1$-Abfragen. | Zwingende Nutzung des `IRebacBatchDataLoader` im Request-Scope. Maximale Traversierungstiefe (`MaxTraversalDepth = 10`) mit zirkulärem Abbruch (Fail-Closed). |
| **Elevation of Privilege** | Bypassen von ReBAC durch Nicht-Angabe des Pflichtarguments oder Ausführen ungeschützter Endpunkte. | **Fail-Closed Default:** Wenn Identität, Mandant oder Zielobjekt nicht aufgelöst werden können, wird der Zugriff sofort verweigert (Deny by Default). |

---

## 2. Verbindliche Härtungsregeln für die Implementierung

1. **Fail-Closed by Default:**
   * Ist kein authentifizierter Benutzer vorhanden $\rightarrow$ Sofortiger Abbruch (HTTP 401 / GraphQL `AUTH_NOT_AUTHENTICATED`).
   * Kann das Ziel-Objekt aus den Argumenten / der Route nicht extrahiert werden $\rightarrow$ Sofortige Verweigerung (HTTP 403 / GraphQL `AUTH_NOT_AUTHORIZED`).
   * Ist das ReBAC-Feature in den `GatewayOptions` deaktiviert, dürfen als `@rebac` oder `RequireRebac` markierte Routen **nicht** schutzlos geöffnet werden, sondern müssen restriktiv konfigurierbar sein (Standard: Fail-Closed).

2. **Mandanten-Isolation (Anti-IDOR):**
   * Alle Prüfungen werden strikt mit der `tenantId` des aufrufenden Tokens ausgeführt.
   * Ein Aufrufer von Mandant A kann unter keinen Umständen Berechtigungen für Mandant B erlangen.

3. **Information Disclosure Prevention:**
   * Fehlerantworten im GraphQL-Bereich:
     ```json
     {
       "errors": [
         {
           "message": "Access denied by ReBAC policy.",
           "extensions": {
             "code": "AUTH_NOT_AUTHORIZED"
           }
         }
       ]
     }
     ```
   * Keine Offenlegung von Pfaden, Parent-Ketten oder Tupel-Definitionen.

---

## 3. Geforderte Security Unit- & Integrationstests

Der Entwickler muss vor dem Release folgende Security-Tests implementieren und nachweisen:

### 3.1 GraphQL-Tests (`RebacGraphQLSecurityTests.cs`)
1. **Direct Relation Permitted:** Authentifizierter User mit Relation `"viewer"` auf `document:123` darf GraphQL-Feld abrufen $\rightarrow$ 200 OK mit Daten.
2. **Missing Relation Denied:** Authentifizierter User ohne Relation wird blockiert $\rightarrow$ GraphQL-Fehler `AUTH_NOT_AUTHORIZED`.
3. **Transitive Inheritance Permitted:** User in `group:finance` mit Relation `"viewer"` auf `folder:reports` (Parent von `document:456`) erhält Zugriff auf `document:456`.
4. **Cross-Tenant Attack Blocked (IDOR):** User aus Tenant A versucht Dokument aus Tenant B mit gültiger Relation in Tenant A abzurufen $\rightarrow$ Blockiert (`AUTH_NOT_AUTHORIZED`).
5. **Fail-Closed on Missing Argument:** Argument für Objekt-ID fehlt in der Query $\rightarrow$ Blockiert (`AUTH_NOT_AUTHORIZED`).
6. **Cycle Resilience:** Zirkuläre Relation löst keine Endlosschleife aus $\rightarrow$ Fail-Closed nach MaxDepth.

### 3.2 HTTP/REST Endpoint Tests (`RebacHttpEndpointSecurityTests.cs`)
1. **RequireRebac Allowed:** User mit Relation `"reader"` auf `dataset:99` erhält HTTP 200.
2. **RequireRebac Denied:** User ohne Relation erhält HTTP 403 Forbidden.
3. **Unauthenticated Caller:** Nicht-authentifizierter Aufruf erhält HTTP 401 Unauthorized.
4. **Missing Route Parameter:** Route-Parameter für Objekt-ID fehlt $\rightarrow$ HTTP 403 Forbidden (Fail-Closed).
5. **Cross-Tenant Boundary:** Aufruf mit abweichender Tenant-ID wird abgewiesen.

---

## 4. Freigabe
Die Architektur wird unter Erfüllung aller oben genannten Punkte für die Entwickler-Implementierung freigegeben.
