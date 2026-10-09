# Implementierungsplan: Supply-Chain-Sicherheit, CI/CD-Härtung & Deployment-Posture

**Dokument-ID:** `PLAN-SEC-01-SUPPLY-CHAIN-CI-DEPLOY`  
**Referenzen:** [Security Review Build, Deploy & Supply Chain 2026-10-09](2026-10-09-security-review-supply-chain-deploy.md) (Befunde SC-01 bis SC-18)  
**Rolle:** C# & .NET Solution Architect / AppSec Engineer  
**Status:** Bereit zur Umsetzung ⏳  

---

## 1. Übersicht & Zielsetzung

Das Sicherheitsaudit der Build-, Deploy- und Supply-Chain-Infrastruktur bestätigte eine solide Basis (gepinnte Aktionen, non-root User, locked package restore). Dennoch wurden 18 konkrete Schwachstellen und Härtungspotenziale (1 Hoch, 6 Mittel, 11 Niedrig/Info) identifiziert.

Dieser Plan spezifiziert die lückenlose Schließung dieser Punkte zur Erreichung höchster Compliance- und Supply-Chain-Sicherheitsstandards (SLSA Level 3, NIST SP 800-218 SSDF, BSI OPS.1.1.5).

---

## 2. Matrix der Supply-Chain-Befunde (SC-01 bis SC-18)

| ID | Schwere | Bereich | Kernbefund | Architektur-Lösung |
|---|---|---|---|---|
| **SC-01** | Hoch | CI/CD | Image-Push mit mutierbaren Tags ohne Vulnerability-Scan & Cosign | Trivy-Container-Scan in CI, Cosign-Signierung (Keyless OIDC), Digest-Pinning in Compose. |
| **SC-02** | Mittel | Release | Release-Binaries ohne Build-Attestation oder abgetrennte Signaturen | GitHub Attestations (`actions/attest-build-provenance`), Minisign/Cosign für Assets, CycloneDX SBOM. |
| **SC-03** | Mittel | CI Gates | Fehlende SAST-, Secret-Scanning- und Coverage-Schranken | Integration von CodeQL (C#), Gitleaks, Dependency-Review-Action, `CODEOWNERS` und `SECURITY.md`. |
| **SC-04** | Mittel | AppSec | `TestAuthHandler` im Produktions-Binary kompiliert | Verlagerung in Test-Assembly oder `#if DEBUG`-Kompilierungsschranke; Ausschluss bei Release-Publish. |
| **SC-05** | Mittel | Benchmark | Benchmark-Image läuft als root, setzt `Development` und hat `danger_bypass` aktiv | Non-root User erzwingen, strikte Trennung von Benchmark- und Produktions-Images, Port-Isolation. |
| **SC-06** | Mittel | Container | Benchmark `appsettings` deaktiviert TLS-Prüfungen | Explizite TLS-Konfiguration für Testcontainer, Beseitigung ungültiger Environment-Kopien. |
| **SC-07** | Mittel | Secrets | Ein einzelnes Secret (`GATEWAY_HMAC_SECRET`) wird für Maskierung und Webhooks geteilt | Getrennte Secrets für Maskierung vs. Webhooks; Unterstützung von Podman/Docker Secret-Files. |
| **SC-08** | Mittel | Reverse Proxy | Nginx-Proxy lauscht auf Plain-HTTP; `/metrics` ungeschützt | TLS-Absicherung oder Loopback-Bindung; Zugriffsschutz auf Prometheus-Metriken. |
| **SC-09** | Niedrig | Container | Fehlende Docker-Sicherheitsoptionen | `cap_drop: [ALL]`, `no-new-privileges: true`, `read_only: true` mit tmpfs in Compose-Dateien. |
| **SC-10** | Niedrig | Container | Ungepinnte Images (`:latest`, mutable Tags) | Digest-Pinning (`image@sha256:...`) für Postgres, Redis, Prometheus, Grafana etc. |
| **SC-11** | Niedrig | Container | Mock-Container laufen als root; Passwörter auf CLI sichtbar | Non-root User für alle Hilfscontainer; `SQLCMDPASSWORD` statt CLI-Argument `-P`. |
| **SC-12** | Niedrig | Build | Benchmark-Build umgeht `--locked-mode` und Warnungen-als-Fehler | Standardisierung des Benchmark-Builds auf denselben strikten Standard wie das Core-Gateway. |
| **SC-13** | Niedrig | Secrets | Statische Entwicklungs-Passwörter in Dateien (`Password123!`) | Dynamische Generierung oder Platzhalter; Gitleaks-Ausnahmeregeln dokumentieren. |
| **SC-14** | Niedrig | Doku | Beispielpasswörter in Dokumenten wirken echt | Standardisierte Platzhalter (`<GENERATE_STRONG_SECRET>`) in allen Markdown-Guides. |
| **SC-15** | Niedrig | Dependabot | Lücken bei Pip, Hilfs-Containerfiles und Tool-Projekten | Erweiterung der `dependabot.yml` auf alle Containerfiles und Tool-Projekte. |
| **SC-16** | Info | Frontend | `swagger-ui-dist` wird statisch ohne Versionsüberwachung ausgeliefert | NPM-Paketüberwachung oder automatisiertes Re-Vendoring bei Releases. |
| **SC-17** | Info | CI | Vulnerability-Audit parst Text statt JSON | `dotnet list package --vulnerable --format json` im CI-Workflow verwenden. |
| **SC-18** | Info | Cloud-Init | Hetzner Setup-Skripte ohne Checksummen-Validierung | Checksummen-Prüfung bei curl-Downloads im Provisioning. |

---

## 3. Technische Umsetzung im Detail

### 3.1 SC-04: Eliminierung des TestAuthHandlers im Release-Publish

Der `TestAuthHandler` darf in Produktions-Binaries unter keinen Umständen existieren:
```csharp
#if DEBUG
namespace Autheris.Api.Security;

/// <summary>
/// Strictly test-only authentication handler. Never compiled in Release mode.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    // ...
}
#endif
```
Zusätzlich in `GatewayServiceCollectionExtensions.cs`:
```csharp
#if DEBUG
if (isDevelopment && options.Dev.EnableTestAuthHandler)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestAuth", _ => { });
}
#endif
```

### 3.2 SC-01 & SC-02: Cosign-Signierung & Attestations in GitHub Actions

Erweiterung von `.github/workflows/docker-publish.yml` und `release.yml`:
```yaml
- name: Run Trivy Vulnerability Scanner
  uses: aquasecurity/trivy-action@master
  with:
    image-ref: ${{ env.IMAGE_NAME }}:${{ env.IMAGE_TAG }}
    format: 'sarif'
    output: 'trivy-results.sarif'
    severity: 'CRITICAL,HIGH'
    exit-code: '1'

- name: Sign Container Image with Cosign
  uses: sigstore/cosign-installer@v3
- run: |
    cosign sign --yes ${{ env.IMAGE_NAME }}@${{ steps.build-and-push.outputs.digest }}

- name: Attest Build Provenance
  uses: actions/attest-build-provenance@v1
  with:
    subject-name: ${{ env.IMAGE_NAME }}
    subject-digest: ${{ steps.build-and-push.outputs.digest }}
    push-to-registry: true
```

### 3.3 SC-07: Getrennte Geheimnisse & Docker/Podman Secret Files

In `deploy/podman-compose.yaml` und `GatewayOptions`:
- `GATEWAY_MASKING_HMAC_KEY`: Exklusiv für Datenpseudonymisierung.
- `GATEWAY_WEBHOOK_SECRET_OPENMETADATA`: Dediziertes Webhook-Secret.
- `GATEWAY_WEBHOOK_SECRET_ITSM`: Dediziertes ITSM-Secret.
- Unterstützung für `*_FILE`-Umgebungsvariablen (z. B. `GATEWAY_MASKING_HMAC_KEY_FILE=/run/secrets/masking_key`).

### 3.4 SC-09 & SC-11: Container-Härtung (Read-Only Root & Drop Caps)

Standardisierung in `deploy/podman-compose.yaml` und `docker-compose.yml`:
```yaml
security_opt:
  - no-new-privileges:true
cap_drop:
  - ALL
read_only: true
tmpfs:
  - /tmp:rw,noexec,nosuid,size=64m
```

---

## 4. Phasenplan

1. **Phase 1 (Code & Compiler-Schranken):** SC-04 (`#if DEBUG` für `TestAuthHandler`), SC-07 (Secret-Trennung), SC-13 & SC-14 (Passwort-Bereinigung in Code/Doku).
2. **Phase 2 (Container & Compose):** SC-05, SC-06, SC-08, SC-09, SC-10, SC-11 (Root-Beseitigung, Cap-Drop, Digest-Pinning).
3. **Phase 3 (CI/CD & Workflows):** SC-01, SC-02, SC-03, SC-12, SC-15, SC-17 (CodeQL, Gitleaks, Trivy, Cosign, Provenance).

---

## 5. Abnahmekriterien

- [ ] Release-Binaries enthalten keinen `TestAuthHandler`-Typ mehr (Geprüft via Reflection-Test).
- [ ] CI bricht bei Fund von Secrets (Gitleaks) oder kritischen Schwachstellen (Trivy) zuverlässig ab.
- [ ] Veröffentlichte Container-Images sind kryptografisch signiert und mit SLSA-Provenance versehen.
- [ ] Kein Container im Compose-Setup läuft als `root` oder besitzt unnötige Linux-Capabilities.
- [ ] Alle Docker-Images in Compose- und Deployment-Dateien sind per SHA-256-Digest fixiert.

---

## 6. Security Architecture Review & Ergänzungen (Security Expert)

> [!IMPORTANT]
> **Sicherheits-Invariante 1: Doppelter Schutz gegen Test-Auth-Bypass (Compile-Time & Unit-Gate)**  
> Der `TestAuthHandler` stellt bei Fehlkonfiguration einen vollständigen Authentifizierungs-Bypass dar.  
> Neben der bedingten Kompilierung (`#if DEBUG`) wird ein verbindlicher Architektur-Test (`SecurityReleaseBinarySanityTests.cs`) implementiert, der das kompilierte Release-Assembly per Reflection scannt und fehlschlägt, falls ein Typ namens `TestAuthHandler` oder der Scheme-Name `"TestAuth"` darin gefunden wird.

> [!CAUTION]
> **Sicherheits-Invariante 2: Notfall-Schlüsselrotation für Masking-Secrets (Zero-Downtime Key Ring)**  
> Wird das getrennte `GATEWAY_MASKING_HMAC_KEY` kompromittiert, muss eine Rotation ohne Datenverlust möglich sein.  
> **Vorgabe:** Der `ColumnMaskingProvider` muss einen Schlüsselring unterstützen (`CurrentKeyId` und `PreviousKeyId`). Bestehende pseudonymisierte Cache-Einträge und Abfragen können so während einer definierten Übergangszeit validiert werden, bevor alte Schlüssel endgültig verworfen werden.

> [!TIP]
> **Sicherheits-Invariante 3: Sigstore / Cosign Admission Gate**  
> Das Erzeugen von Signaturen in CI ist nur die halbe Miete. In `deploy/kubernetes/` bzw. `deploy/podman/` wird eine `policy.json` / Kyverno-Policy bereitgestellt, die das Starten von Containern verweigert, wenn deren Signatur nicht von der GitHub Actions OIDC-Identität `https://github.com/themulle/autheris/.github/workflows/docker-publish.yml@refs/heads/main` stammt.

