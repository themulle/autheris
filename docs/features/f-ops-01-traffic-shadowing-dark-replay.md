# F-OPS-01: AST-Aware Production Traffic Shadowing & Dark Replay

**Status:** **100% (GA) ✅ (Implementiert & Security-Audited 2026-10-02)**  
**Komponenten:** [`TrafficShadowingMiddleware.cs`](file:///root/lis-git/gql/gql/src/Autheris.Api/Middleware/TrafficShadowingMiddleware.cs), [`AstShadowingFilter.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Operations/Shadowing/AstShadowingFilter.cs), [`PiiShadowingRedactor.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Operations/Shadowing/PiiShadowingRedactor.cs), [`TrafficShadowingService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Operations/Shadowing/TrafficShadowingService.cs), [`TrafficShadowingOptions.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Options/GatewayOptions.cs)  
**Referenzen:** [`implementation-plan-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-1-2026-10-02.md), [`security-review-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/threat-model/security-review-welle-1-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Syntaktische Schema-Validierungen erkennen keine Latenz-Regressionen, Lock-Contention auf der Datenbank oder fehlerhafte Datenaggregationen unter realer Produktionslast. Ein Dark-Replay-Mechanismus ist essenziell für risikolose Upgrades und Canary-Deployments.

## 2. Architektur & Umsetzung
- **AST Mutation Guard:** Der `AstShadowingFilter` analysiert den Query-AST (GraphQL) und SQL-Statements vor der Weiterleitung. Sämtliche schreibenden Verben (`mutation`, `INSERT`, `UPDATE`, `DELETE`, `DROP`, `TRUNCATE`, `ALTER`, `MERGE`, `EXEC`, `CALL`, `GRANT`, `COPY`) werden ausnahmslos blockiert (`IsSafeToReplay = false`).
- **PII- & Credential-Redaction:** Der `PiiShadowingRedactor` filtert sensible Header (`Authorization`, `Cookie`, `X-Api-Key`, Client-Zertifikate, Vault-Token) und ersetzt Tokens durch synthetische Staging-Credentials (`Bearer staging-shadow-synthetic-token`).
- **Bounded Buffering DoS-Schutz:** Payloads über 2 MB werden im Shadowing-Pfad ignoriert; asynchrone Fire-and-Forget-Warteschlangen belasten den kritischen Client-Pfad mit < 1 µs Overhead.
- **Konfigurierbare Sampling-Rate:** Präzise Steuerung von 0.0 bis 1.0 (z. B. 0.05 für 5% Shadowing).

## 3. Konfigurationsbeispiel (`appsettings.json`)
```json
{
  "Gateway": {
    "Operations": {
      "TrafficShadowing": {
        "Enabled": true,
        "TargetBaseUrl": "https://staging-cluster.internal:5001",
        "SampleRate": 0.1,
        "DropMutations": true,
        "StripPiiHeaders": true
      }
    }
  }
}
```

## 4. Business Value
- **Zero-Downtime Releases:** Risikofreies Testen neuer Gateway-Versionen, Fusion-Subgraphs und Datenbank-Indizes mit echten Produktionsanfragen.
- **Automatisierte Qualitätskontrolle:** Frühzeitige Erkennung von Performance- und Antwort-Divergenzen vor dem Produktivgang.
