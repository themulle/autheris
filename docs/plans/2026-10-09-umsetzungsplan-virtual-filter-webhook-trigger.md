# Umsetzungsplan (csharp-architect): GitOps Webhook Trigger für Virtuelle Filter

**Bezug:** [docs/plans/2026-10-08-virtuelle-filter.md](2026-10-08-virtuelle-filter.md) (Abschnitt 4.2), [docs/plans/2026-10-08-umsetzungsplan-virtuelle-filter.md](2026-10-08-umsetzungsplan-virtuelle-filter.md) (Offene Punkte: "Neuladen per Webhook (GitHub)").  
**Datum:** 2026-10-09  
**Branch:** `feat/ast-target-dialect-generator`

---

## 1. Problemstellung & Ziel

Virtuelle Filter und Access-Profile können deklarativ in einem Git-Repository (GitOps) verwaltet werden. Autheris gleicht den Zustand periodisch per Takt ab. Um Änderungen jedoch ohne Latenz unmittelbar nach einem `git push` zu verarbeiten, benötigt das Gateway einen sicheren Webhook-Empfänger für GitHub (`push`-Ereignis).

### Sicherheits- und Architekturanforderungen:
1. **Timing-Safe HMAC-Validierung:**
   - Validierung des Headers `X-Hub-Signature-256` (`sha256=<hex>`) gegen `VirtualFilterOptions.WebhookSecret` bzw. Env-Var `AUTHERIS_CONFIG_SYNC_WEBHOOK_SECRET`.
   - Vergleich mittels `CryptographicOperations.FixedTimeEquals` zur Abwehr von Timing-Side-Channel-Angriffen.
   - **Keine Bypass-Optionen:** Die bestehenden Entwicklungs-Schalter (`IsWebhookSignatureBypassed`) dürfen für diesen sensiblen Governance-Endpunkt **nicht** greifen.
2. **Replay-Schutz via `X-GitHub-Delivery`:**
   - Der Header `X-GitHub-Delivery` (eindeutige UUID von GitHub) ist zwingend erforderlich. Fehlt er -> `400 Bad Request`.
   - Deduplizierung über ein speicherbeschränktes Zeitfenster (TTL 1 Stunde). Doppelte Lieferungen werden idempotent mit `202 Accepted` beantwortet, ohne den Sync erneut anzustoßen.
3. **Git-Ref-Filterung:**
   - Ist `VirtualFilterOptions.GitRef` konfiguriert (z. B. `refs/heads/main`), werden Pushes auf andere Branches ignoriert (`200 OK` / `202 Accepted` mit `status: "Ignored"`).
4. **Kein SSRF / Bounded Read:**
   - Autheris liest niemals Repo-URLs oder Pfade aus dem Webhook-Body; der Auslöser triggert ausschließlich intern den Sync.
   - Body-Größenbegrenzung: Maximal 2 MB über `EndpointSecurity.TryReadBodyAsync`.
5. **Endpoints:**
   - Kanonischer Pfad: `POST /api/webhooks/config-sync` (`.AllowAnonymous()`).
   - Governance-Alias: `POST /api/v1/governance/virtual-filters/sync/webhook` (`.AllowAnonymous()`).
   - Status-Integration: `/config-sync/status` und `/virtual-filters/sync/status` geben `lastWebhookTriggerAt` und `lastWebhookDeliveryId` aus.

---

## 2. Komponenten & Änderungen

### 2.1 Domain-Optionen (`GatewayOptions.cs`)
In `VirtualFilterOptions`:
- `public string? WebhookSecret { get; init; }` (HMAC Secret)
- `public string? GitRef { get; init; }` (z.B. `refs/heads/main`)

### 2.2 Application Service (`VirtualFilterAdministrationService.cs`)
- `RecordWebhookTrigger(string deliveryId, DateTimeOffset timestamp)`
- `LastWebhookTriggerAt` und `LastWebhookDeliveryId` Properties für Statusabfragen.

### 2.3 API Endpunkte (`VirtualFilterEndpoints.cs`)
- Mapping der beiden Webhook-Routen mit `AllowAnonymous()`.
- Handler `HandleConfigSyncWebhookAsync`:
  - Liest Body bis 2 MB.
  - Prüft `X-GitHub-Delivery`.
  - Prüft HMAC `X-Hub-Signature-256` timing-safe.
  - Prüft `ref` aus JSON-Payload.
  - Ruft `RecordWebhookTrigger` auf.
  - Liefert `202 Accepted`.
- Erweiterung von `ConfigSyncStatusAsync` um Webhook-Informationen.

---

## 3. TDD-Schritte (Test-Driven Development)

1. **Phase 1: Unit- & Endpoint-Tests (Red)**
   - Neue Testklasse `VirtualFilterWebhookTriggerTests.cs` in `tests/Autheris.Tests.Unit/`:
     - `Webhook_MissingDeliveryHeader_ReturnsBadRequest400`
     - `Webhook_MissingSignature_ReturnsUnauthorized401`
     - `Webhook_InvalidSignature_ReturnsUnauthorized401`
     - `Webhook_ValidSignature_MismatchedRef_ReturnsIgnored`
     - `Webhook_ValidSignatureAndMatchingRef_ReturnsAccepted202AndUpdatesStatus`
     - `Webhook_DuplicateDeliveryId_ReturnsAcceptedWithoutDoubleTrigger`
     - `Webhook_BypassOptionsEnabled_StillRejectsInvalidSignature`
2. **Phase 2: Implementierung (Green)**
   - Optionen in `GatewayOptions.cs` ergänzen.
   - `RecordWebhookTrigger` in `VirtualFilterAdministrationService.cs` implementieren.
   - Endpoint-Handler in `VirtualFilterEndpoints.cs` implementieren und registrieren.
   - Status-Antwort erweitern.
3. **Phase 3: Verifikation & Build**
   - Unit-Tests ausführen (`dotnet test`).
   - Gesamtlösung mit `dotnet build /warnaserror` auf 0 Warnungen prüfen.
   - Git commit: `feat(filters): add GitHub webhook trigger with timing-safe HMAC validation for GitOps sync`.
