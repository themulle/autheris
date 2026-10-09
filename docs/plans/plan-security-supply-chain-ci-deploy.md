# Implementierungsplan: Supply-Chain-Sicherheit, CI/CD-Härtung & Deployment-Posture

**Dokument-ID:** `PLAN-SEC-01-SUPPLY-CHAIN-CI-DEPLOY`  
**Referenzen:** [Security Review Build, Deploy & Supply Chain 2026-10-09](2026-10-09-security-review-supply-chain-deploy.md) (Befunde SC-01 bis SC-18)  
**Rolle:** C# & .NET Solution Architect / AppSec Engineer  
**Status:** Bereit zur Umsetzung ⏳ (Revision 2: Plan-Review 2026-10-09 eingearbeitet)  

---

## 1. Übersicht & Zielsetzung

Das Sicherheitsaudit der Build-, Deploy- und Supply-Chain-Infrastruktur bestätigte eine solide Basis (SHA-gepinnte Actions, digest-gepinnte Basis-Images, non-root User im Haupt-Image, Locked Restore, NuGet-Audit als Fehler). Dennoch wurden 18 konkrete Schwachstellen und Härtungspotenziale identifiziert (1 Hoch, 7 Mittel, 7 Niedrig, 3 Info; die Zählung „6 Mittel / 8 Niedrig/Info" im Review-Text ist inkonsistent zur Befundtabelle).

Dieser Plan spezifiziert die Schließung dieser Punkte sowie zusätzlicher Lücken aus dem Plan-Review (Abschnitt 3.12). Zielbild: SLSA Build L3-fähige Pipeline, NIST SP 800-218 SSDF (PS.2, PS.3, PW.4, RV.1), BSI OPS.1.1.5 / SYS.1.6.

**Leitplanken für alle Workflow-Änderungen:**
- Jede neue Action wird auf den **vollständigen Commit-SHA** gepinnt (`uses: owner/action@<40-hex-SHA> # vX.Y.Z`), analog zum Bestand (`ci.yml:17,20`, `docker-publish.yml:24,27,45,48,90`). Tags wie `@master`, `@v3`, `@v1` sind verboten (Bestand würde sonst regressieren). SHAs werden bei Umsetzung per `gh api repos/<owner>/<action>/git/ref/tags/<tag>` ermittelt und von Dependabot (`github-actions`) gepflegt. Die SHAs in diesem Plan wurden am 2026-10-09 per `git ls-remote --tags` ermittelt, bei annotierten Tags als gepeelter `^{}`-Commit. `checkout`/`build-push-action` sind identisch mit dem Bestand.
- `permissions` werden **pro Job** minimal vergeben, nicht workflow-weit.
- `actions/checkout` mit `persist-credentials: false`, wo kein Push nötig ist.

---

## 2. Matrix der Supply-Chain-Befunde (SC-01 bis SC-18)

| ID | Schwere | Bereich | Kernbefund | Architektur-Lösung | Abschnitt / Phase |
|---|---|---|---|---|---|
| **SC-01** | Hoch | CI/CD | Image-Push mit mutierbaren Tags ohne Vulnerability-Scan, Signatur und CI-Gate | Build → Trivy-Scan (Gate) → Push → Cosign keyless → Verify; `latest` nur aus `v*`-Tags; Digest-Pinning in `docker-compose.yml:13`. | 3.2 / P3 |
| **SC-02** | Mittel | Release | Release-Binaries ohne Attestation/Signatur/SBOM; `SHA256SUMS` im selben Release; Job mit `contents: write` | Build-Job (read) und Release-Job (write) trennen; `actions/attest-build-provenance` mit `subject-path`; `cosign sign-blob` für `SHA256SUMS`; CycloneDX-SBOM je RID. | 3.3 / P3 |
| **SC-03** | Mittel | CI Gates | Kein SAST, kein Secret-Scanning, keine Dependency-Review, kein Dockerfile-Lint, keine Coverage-Schranke, kein `CODEOWNERS`/`SECURITY.md` | CodeQL (C#), Gitleaks, `dependency-review-action`, Hadolint, Coverlet-Schranke, `CODEOWNERS`, `SECURITY.md`, Branch-Ruleset. | 3.4 / P3 |
| **SC-04** | Mittel | AppSec | `TestAuthHandler` im Produktions-Binary kompiliert | Eigenes Build-Symbol `AUTHERIS_TEST_AUTH` (nicht `DEBUG`), Release-Publish ohne Symbol, Reflection-Gate auf dem **Publish-Output**. | 3.1 / P1 |
| **SC-05** | Mittel | Benchmark | Bench-Image root, `Development`, `danger_bypass` aktiv, Config-Datei-Mismatch | `USER $APP_UID`, `ASPNETCORE_ENVIRONMENT=Benchmark`, Datei als `appsettings.Benchmark.json`, Image nie in Registry. | 3.6 / P2 |
| **SC-06** | Mittel | Container | Bench-`appsettings` schaltet TLS-Prüfungen ab; selbe Datei unter drei Namen | Nur `appsettings.Benchmark.json` kopieren; explizites, geloggtes `danger_allow_insecure_transport` nur im Bench-Profil. | 3.6 / P2 |
| **SC-07** | Mittel | Secrets | `GATEWAY_HMAC_SECRET` für Masking, 3 Webhooks und Bench-Key geteilt; Secrets als Env | Fünf getrennte Secrets; Auslieferung als Podman/Docker-Secret-Files über den **bestehenden** `IKeyVaultSecretProvider`/`*KeyVaultRef`-Mechanismus. | 3.5 / P1 |
| **SC-08** | Mittel | Reverse Proxy | Plain-HTTP, `/metrics` über nginx, `X-Benchmark-Role: Admin` für ganzes `10.89.77.0/24` | `/metrics` in nginx sperren, Admin-Mapping auf Loopback/entfernen, Netzwerk `internal`, Banner „nie internet-facing" oder TLS. | 3.7 / P2 |
| **SC-09** | Niedrig | Container | Keine Compose-Sicherheitsoptionen, kein `HEALTHCHECK`, keine Limits | `cap_drop`, `no-new-privileges`, `read_only` + tmpfs, `pids/mem/cpu`-Limits, Healthcheck `/health/live` – **pro Service** abgestimmt. | 3.8 / P2 |
| **SC-10** | Niedrig | Container | Ungepinnte/veraltete Images, OpenSearch ohne Security-Plugin mit Host-Port 9200 | Digest-Pinning `tag@sha256:…`, Update MinIO/OpenSearch, Port 9200 nicht publizieren. | 3.9 / P2 |
| **SC-11** | Niedrig | Container | Hilfscontainer als root; `sqlcmd -P` | Non-root User; `SQLCMDPASSWORD`. | 3.8 / P2 |
| **SC-12** | Niedrig | Build | Bench-Builds ohne `--locked-mode`, `TreatWarningsAsErrors=false`, Quellen außerhalb des Repos | Lock-Files + `NuGet.config` in Build-Kontext kopieren, `--locked-mode`, Warnungen als Fehler, nur committeter Tree (`stage_sources.sh` entfernen). | 3.10 / P2 |
| **SC-13** | Niedrig | Secrets | Statische Dev-/Bench-Passwörter in Code/Skripten | Generierte Werte/Platzhalter; dokumentierte Gitleaks-Allowlist. | 3.11 / P1 |
| **SC-14** | Niedrig | Doku | Echt wirkende Beispielpasswörter in Doku | Platzhalter `<GENERATE_STRONG_SECRET>` + Hinweis `openssl rand -base64 32`. | 3.11 / P1 |
| **SC-15** | Niedrig | Dependabot | Lücken: Containerfiles, pip, `tools/*`, `benchmarks/*` | `directories:`-Liste je Ökosystem, Gruppierung, Security-Updates. | 3.4 / P3 |
| **SC-16** | Info | Frontend | `swagger-ui-dist 5.18.2` vendored ohne Versionsüberwachung | `package.json` mit exakter Version + Dependabot-npm-Eintrag; Re-Vendoring-Skript mit SRI-Hash-Prüfung. | 3.11 / P3 |
| **SC-17** | Info | CI | Audit-Gate greppt Text | `--format json --output-version 1` + `jq`-Auswertung; Gate-Selbsttest. | 3.4 / P3 |
| **SC-18** | Info | Cloud-Init | Installer ohne Checksummen, keine Hetzner-Firewall | Versions-Pinning, SHA-256-Prüfung `dotnet-install.sh`, apt-Repos mit Signed-By, `hcloud firewall create`. | 3.11 / P2 |

---

## 3. Technische Umsetzung im Detail

### 3.1 SC-04: Eliminierung des TestAuthHandlers im Release-Publish

**Achtung (Korrektur gegenüber Rev. 1):** Ein `#if DEBUG`-Schalter ist **nicht** umsetzbar, da CI alle Tests mit `-c Release` ausführt (`ci.yml:36`, `docker-publish.yml:35`, `release.yml:34`) und 30+ Testdateien den Handler per `EnableTestAuthHandler` nutzen (z. B. `tests/Autheris.Tests.Integration/EndToEndSqliteTests.cs:49`). Der Release-Testlauf würde brechen bzw. die Tests würden still ihre Auth-Annahme verlieren.

Lösung: eigenes Kompiliersymbol, das nur Test-Builds setzen.

```xml
<!-- src/Autheris.Api/Autheris.Api.csproj -->
<PropertyGroup Condition="'$(AutherisIncludeTestAuth)' == 'true'">
  <DefineConstants>$(DefineConstants);AUTHERIS_TEST_AUTH</DefineConstants>
</PropertyGroup>
```
- Test-Projekte setzen `AutherisIncludeTestAuth=true` für die referenzierte Api (via `Directory.Build.props` in `tests/` bzw. `AdditionalProperties` an der `ProjectReference`). **Wichtig:** Da `dotnet test` und `dotnet publish` dieselbe `obj/`-Ausgabe nutzen, muss der Publish in einem separaten Schritt mit `--no-build` **unterbleiben**; Publish baut neu ohne das Symbol (Alternative, robuster: Handler in eigenes Assembly `Autheris.Api.TestAuth` verschieben, das nur von Testprojekten referenziert und per `IHostingStartup`/Hook registriert wird).
- Betroffene Stellen hinter `#if AUTHERIS_TEST_AUTH`: `Security/TestAuthHandler.cs`, `GatewayServiceCollectionExtensions.cs:777-784, 965, 1422`. Option `Authentication.EnableTestAuthHandler` bleibt im Options-Modell; der Validator (`GatewayServiceCollectionExtensions.cs:122`) wird verschärft: ohne Symbol ⇒ `EnableTestAuthHandler=true` ist **immer** ein Startfehler. `DevConfiguration.cs:85` und `DevEndpoints.cs:156` entsprechend anpassen.
- Laufzeit-Guards (`IsDevelopment`, Container-Opt-in) bleiben als zweite Schicht.

**Verifikation:**
- CI-Schritt nach `dotnet publish`: Reflection-/Metadaten-Check auf `dist/publish/Autheris.Api.dll` (z. B. kleines Tool in `tools/` oder `SecurityReleaseBinarySanityTests` mit Pfad auf den Publish-Output via Env `AUTHERIS_PUBLISH_DIR`), schlägt fehl bei Typ `*TestAuthHandler` oder String-Literal `"TestAuth"` als Scheme. **Nicht** gegen das Test-Build-Assembly prüfen (dort ist der Handler gewollt).
- Gleicher Check in `release.yml` für jede RID (Single-File: `dotnet` kann die DLL nicht direkt laden → Check auf das Nicht-Single-File-Zwischenergebnis oder `-p:IncludeAllContentForSelfExtract` vermeiden; Prüfung vor dem Bündeln).
- Negativtest: Release-Image mit `ASPNETCORE_ENVIRONMENT=Development`, `AUTHERIS_ALLOW_DEV_IN_CONTAINER=true`, `Gateway__Authentication__EnableTestAuthHandler=true` starten ⇒ Startabbruch; Request mit `X-Test-User-Sid` ⇒ 401.

### 3.2 SC-01: Image-Pipeline (Scan-Gate, Signatur, Verify, Tags)

Korrekturen gegenüber Rev. 1: Rev. 1 nutzte ungepinnte Actions (`trivy-action@master`, `cosign-installer@v3`, `attest-build-provenance@v1`), referenzierte die nicht existierende Step-ID `build-and-push` und undefinierte `env.IMAGE_NAME/IMAGE_TAG`, scannte erst **nach** dem Push und ließ die nötigen OIDC-Permissions weg.

Zielablauf in `.github/workflows/docker-publish.yml`:

```yaml
on:
  push:
    tags: [ 'v*' ]
  workflow_run:                       # SC-01 Gate: nur nach grünem CI auf main
    workflows: [ "CI Build & Test" ]
    types: [ completed ]
    branches: [ main ]
  workflow_dispatch: { ... }          # unverändert, Regex-Validierung bleibt

permissions: {}                        # Default: nichts

jobs:
  build-and-push:
    if: github.event_name != 'workflow_run' || github.event.workflow_run.conclusion == 'success'
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write                  # GHCR-Push
      id-token: write                  # Cosign keyless + Attestation (Fulcio/Rekor)
      attestations: write              # actions/attest-build-provenance
      security-events: write           # SARIF-Upload
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          persist-credentials: false
          ref: ${{ github.event.workflow_run.head_sha || github.sha }}   # exakt der getestete Commit
      # ... setup-dotnet, restore --locked-mode, test, publish (Bestand) + TestAuth-Check (3.1)

      - name: Build image (local, not pushed)
        id: build-local
        uses: docker/build-push-action@c3c9e263c25d99ce0380d002d59b67737d91b0dc # v7.4.0
        with:
          context: .
          load: true
          push: false
          tags: autheris:scan

      - name: Trivy scan (gate)
        uses: aquasecurity/trivy-action@ed142fd0673e97e23eac54620cfb913e5ce36c25 # v0.36.0 – nur gepinnter SHA, nie @master/Tag (Tag-Hijack März 2026)
        with:
          image-ref: autheris:scan
          severity: CRITICAL,HIGH
          ignore-unfixed: true          # sonst dauerhaft rot durch unbehebbare Base-Image-CVEs
          trivyignores: .trivyignore    # jeder Eintrag mit Begründung + Ablaufdatum (Review-Pflicht)
          exit-code: '1'
          format: sarif
          output: trivy-results.sarif
      - name: Upload SARIF
        if: always()
        uses: github/codeql-action/upload-sarif@24c54180a607b1449ed407dd24f251e4e9147c8d # v4.38.3
        with: { sarif_file: trivy-results.sarif }

      - name: Build and push
        id: push
        uses: docker/build-push-action@c3c9e263c25d99ce0380d002d59b67737d91b0dc # v7.4.0
        with:
          context: .
          push: true
          provenance: mode=max
          sbom: true
          tags: ${{ steps.meta.outputs.tags }}

      - uses: sigstore/cosign-installer@6f9f17788090df1f26f669e9d70d6ae9567deba6 # v4.1.2
      - name: Sign by digest (keyless)
        env:
          IMAGE: ghcr.io/${{ github.repository }}
          DIGEST: ${{ steps.push.outputs.digest }}
        run: cosign sign --yes "${IMAGE,,}@${DIGEST}"

      - name: Verify signature (self-check)
        env:
          IMAGE: ghcr.io/${{ github.repository }}
          DIGEST: ${{ steps.push.outputs.digest }}
        run: |
          cosign verify "${IMAGE,,}@${DIGEST}" \
            --certificate-oidc-issuer https://token.actions.githubusercontent.com \
            --certificate-identity-regexp '^https://github\.com/themulle/autheris/\.github/workflows/docker-publish\.yml@refs/(heads/main|tags/v.+)$'
          echo "Published digest: ${DIGEST}" >> "$GITHUB_STEP_SUMMARY"
```

Hinweise:
- Der zweite Build nutzt den Buildx-Cache; da `Dockerfile` nur `COPY dist/publish/` macht, ist er bitgleich zum gescannten Layer-Inhalt. Alternativ: einmal pushen unter temporärem Tag `sha-<commit>`, scannen, und erst danach `latest`/`getting-started` per `docker buildx imagetools create` setzen (verhindert Doppelbuild).
- Die Buildx-Attestation (`provenance: mode=max`, `sbom: true`) bleibt; `actions/attest-build-provenance` ist für das Image **optional** (Duplikat). Wenn genutzt: `subject-name: ghcr.io/${{ github.repository }}` (lowercase!), `subject-digest: ${{ steps.push.outputs.digest }}`, `push-to-registry: true`.
- **Tag-Politik:** `latest` und `getting-started` nur noch bei `v*`-Tags; Push auf `main` erzeugt nur `sha-<commit>` und `main`. `docker-compose.yml:13` wird auf `ghcr.io/themulle/autheris:<version>@sha256:<digest>` umgestellt (Release-Prozess aktualisiert den Digest per PR).
- Ein Workflow-Ausfall von Trivy-DB-Downloads (Rate-Limit `ghcr.io/aquasecurity/trivy-db`) ist zu erwarten → DB-Cache (`actions/cache`) bzw. `TRIVY_DB_REPOSITORY`-Mirror vorsehen.

**Verifikation:**
- Testlauf auf Branch mit absichtlich verwundbarem Base-Image ⇒ Job rot, **kein** Push (Registry prüfen).
- `cosign verify …` (s. o.) und `gh attestation verify oci://ghcr.io/themulle/autheris@sha256:… --owner themulle` lokal erfolgreich; `cosign tree` zeigt Signatur + SBOM.
- Push auf `main` erzeugt **kein** `latest`.

### 3.3 SC-02: Release-Binaries (Attestation, Signatur, SBOM, Least Privilege)

`release.yml` wird in zwei Jobs geteilt:

1. `build` (`permissions: contents: read, id-token: write, attestations: write`): Restore, Test, Publish je RID, TestAuth-Check (3.1), SBOM, `SHA256SUMS`, Attestation, `cosign sign-blob`; Upload als Workflow-Artefakt.
2. `release` (`needs: build`, `permissions: contents: write`): lädt Artefakte und erstellt das Release – **kein** Build-Code mit Schreibrechten.

```yaml
- name: SBOM je RID (CycloneDX)
  run: |
    dotnet tool restore            # CycloneDX als gepinntes Tool in .config/dotnet-tools.json
    dotnet CycloneDX src/Autheris.Api/Autheris.Api.csproj -o artifacts -fn "Autheris-${RID}.cdx.json" -j
- uses: actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8 # v4.2.2
  with:
    subject-path: 'artifacts/Autheris-*'
- uses: actions/attest-sbom@c604332985a26aa8cf1bdc465b92731239ec6b9e # v4.1.0   (optional: SBOM an Binary binden)
- name: Sign checksums (keyless)
  run: cosign sign-blob --yes --bundle artifacts/SHA256SUMS.sigstore.json artifacts/SHA256SUMS
```

**Zusätzlicher Befund (Plan-Review):** `RestoreLockedMode` greift laut `Directory.Build.props:14` nur, wenn `RuntimeIdentifier` leer ist. Der RID-spezifische `dotnet publish -r <RID>` in `release.yml:54-59` restored implizit **ohne** Lock-Erzwingung. Lösung: Lock-Files mit `RuntimeIdentifiers=linux-x64;linux-arm64;win-x64;osx-arm64` im csproj erzeugen und die Bedingung entfernen, oder vorab `dotnet restore -r <RID> --locked-mode` und Publish mit `--no-restore`.

**Verifikation:**
- `gh attestation verify Autheris-linux-x64.tar.gz --owner themulle` erfolgreich.
- `cosign verify-blob --bundle SHA256SUMS.sigstore.json --certificate-oidc-issuer https://token.actions.githubusercontent.com --certificate-identity-regexp '^https://github\.com/themulle/autheris/\.github/workflows/release\.yml@refs/tags/v.+$' SHA256SUMS` erfolgreich, anschließend `sha256sum -c SHA256SUMS`.
- Release-Notes enthalten den Verifikationsbefehl (README-Abschnitt „Verifying releases").
- Lock-Drift-Test: absichtlich geänderte Paketversion ohne Lock-Update ⇒ Release-Build rot.

### 3.4 SC-03, SC-15, SC-17: CI-Gates, Dependabot, Audit-Parsing

**SC-03 – neue Workflows/Jobs (alle Actions SHA-gepinnt, Job-Permissions minimal):**
- `codeql.yml`: `github/codeql-action/init|analyze` für `csharp` (build-mode `manual` mit `dotnet build -c Release`), Trigger PR + `main` + wöchentlich; `permissions: security-events: write, contents: read`. Hinweis: Bei privatem Repo ohne GitHub Advanced Security nicht verfügbar → dann Semgrep CE als Fallback.
- Gitleaks: CLI in gepinnter Version mit SHA-256-Prüfung des Release-Tarballs (die `gitleaks-action` verlangt für Organisationen eine Lizenz) über die volle Historie (`fetch-depth: 0`); Konfiguration `.gitleaks.toml` mit dokumentierter Allowlist (SC-13).
- `actions/dependency-review-action` auf `pull_request`, `fail-on-severity: moderate`, `deny-licenses` gem. Lizenzpolitik.
- Hadolint für `Dockerfile`, `deploy/containers/*/Containerfile`, `benchmarks/load/docker/Dockerfile.*`; `actionlint` + `zizmor` für `.github/workflows/*`.
- Coverage: `dotnet test --collect:"XPlat Code Coverage"`, Auswertung mit `reportgenerator` (gepinntes dotnet-Tool); Schranke zunächst als Baseline ohne Absenkung (Ratchet), separat ≥ 80 % Zeilen für `Autheris.Api.Security` und `Autheris.Application.Security`.
- `.github/CODEOWNERS` für `src/Autheris.Api/Security/**`, `.github/**`, `deploy/**`, `Dockerfile`, `Directory.Build.props`, `NuGet.config`, `**/packages.lock.json`; `SECURITY.md` mit Meldeweg (Private Vulnerability Reporting aktivieren).
- **Branch-Ruleset auf `main`** (Repo-Einstellung, als Code dokumentiert): Required Checks (CI, CodeQL, Gitleaks, Dependency Review, Hadolint), Required Review inkl. Code-Owner, keine Force-Pushes, signierte Commits optional. Ohne Ruleset sind alle Gates nur beratend.

**SC-17 – Audit-Gate robust machen (`ci.yml:27-33`):**
```bash
dotnet list Autheris.sln package --vulnerable --include-transitive --format json --output-version 1 > vulnerable.json
jq -e '[.projects[].frameworks[]? | (.topLevelPackages // []) + (.transitivePackages // []) | .[]] | length == 0' vulnerable.json \
  || { echo "::error::Vulnerable NuGet packages detected"; jq . vulnerable.json; exit 1; }
```
Zusätzlich `global.json` mit fixierter SDK-Version (`rollForward: latestFeature`) anlegen – aktuell fehlt sie und `setup-dotnet` nutzt `10.0.x` (nicht reproduzierbar, Ausgabeformat kann wechseln). Gate-Selbsttest: Workflow-Job, der gegen ein Fixture-JSON mit einer Vulnerability läuft und rot sein **muss**. Dasselbe Audit auch in `docker-publish.yml` und `release.yml` (bisher nur in `ci.yml`).

**SC-15 – `.github/dependabot.yml`:**
- `docker`: `directories: ["/", "/deploy/containers/*", "/benchmarks/load/docker"]`; `docker-compose`-Ökosystem für `/`, `/deploy`, `/benchmarks/load/docker` (Digest-Updates der Compose-Images aus 3.9).
- `nuget`: `directories: ["/", "/tools/*", "/benchmarks/*"]`.
- `pip`: `/deploy/containers/*` nach Einführung von `requirements.txt` mit `--require-hashes` (statt Inline-`pip install boto3==…` in `lakehouse-seed/Containerfile:10`).
- `npm`: `/src/Autheris.Api/Assets/swagger-ui` (SC-16).
- `groups` je Ökosystem, Security-Updates ungruppiert; `open-pull-requests-limit` setzen.

**Verifikation:** Je Gate ein Canary-PR (Dummy-Secret, verwundbares Paket, CodeQL-Testfund, Dockerfile mit `:latest`) ⇒ jeweils roter Required Check, Merge blockiert. Dependabot „Last checked" für alle Verzeichnisse in den Insights sichtbar.

### 3.5 SC-07: Getrennte Geheimnisse & Secret-Files

Korrektur gegenüber Rev. 1: Die Namen `GATEWAY_MASKING_HMAC_KEY` etc. existieren nicht. Masking liest in Nicht-Development **ausschließlich** über `IKeyVaultSecretProvider` mit `Gateway:DataMasking:HmacSecretKeyVaultRef` (`ColumnMaskingProvider.cs:26-32`, Validator `GatewayServiceCollectionExtensions.cs:138-140`).

**Geklärt (Phase 0, Code-Analyse):** `Gateway__DataMasking__HmacSecret` (`deploy/podman-compose.yaml:232`) ist im Bench-Stack **wirkungslos**; der Masking-Key kommt faktisch aus `HMAC_SECRET_KEY` (`podman-compose.yaml:230`).
- Options-Binding: `AddOptions<GatewayOptions>().Bind(GetSection("Gateway"))` (`GatewayServiceCollectionExtensions.cs:90-91`); `DataMaskingOptions` hat keine Property `HmacSecret` (`GatewayOptions.cs:797-803`), der Binder ignoriert den Key still (kein `ErrorOnUnknownConfiguration`).
- `IKeyVaultSecretProvider` = `DefaultEnvironmentSecretProvider` (`GatewayServiceCollectionExtensions.cs:340`). Der liest `Gateway:DataMasking:HmacSecret` nur als Well-known-Alias, wenn die Referenz **exakt** `hmac-masking-secret`, `HMAC_SECRET` oder `HMAC_SECRET_KEY` lautet (`DefaultEnvironmentSecretProvider.cs:77-85`).
- Der Bench-Container überschreibt `appsettings.json`/`.Production.json` mit `appsettings.Benchmark.json` (`deploy/containers/gqlgateway-api/Containerfile:50-52`, `ASPNETCORE_ENVIRONMENT=Production` in `podman-compose.yaml:228`). Dort gilt `HmacSecretKeyVaultRef: "hmac-secret-key"` (`appsettings.Benchmark.json:122`). Das ist kein Alias, aber der normalisierte Kandidat `HMAC_SECRET_KEY` (`DefaultEnvironmentSecretProvider.cs:54-55`) trifft `IConfiguration["HMAC_SECRET_KEY"]` (Env-Provider von `WebApplication.CreateBuilder`, `Program.cs:14`) ⇒ Wert von `HMAC_SECRET_KEY`.
- Produktiv-Default `GQL-HMAC-SECRET-KEY` (`src/Autheris.Api/appsettings.json:89`) ⇒ Kandidat `GQL_HMAC_SECRET_KEY`; `Gateway__DataMasking__HmacSecret` greift auch dort nicht.
- Derselbe Ref wird zusätzlich als Master-Key für Audit-HMAC (`*GovernanceRepository.cs:138-143`), Consent-Cache (`ConsentCacheService.cs:416`), HitL (`HitLStepUpApprovalService.cs:30`) und WebSQL-In-DB-Masking (`GovernedSqlExecutionService.cs:1919-1928`) genutzt. Wer `HMAC_SECRET_KEY` rotiert, rotiert auch diese.

**Plan-Konsequenz:** Zeile `Gateway__DataMasking__HmacSecret` in `podman-compose.yaml:232` ersatzlos streichen. Der Masking-Key wird über `HmacSecretKeyVaultRef` → (nach 3.5) `file:/run/secrets/masking_hmac_key` geliefert. `HMAC_SECRET_KEY` entfällt dann. Den Alias-Zweig `DefaultEnvironmentSecretProvider.cs:77-85` nicht erweitern; er sollte langfristig entfallen. Zusätzlich ein Startup-Warnlog (einmalig, kein Hot-Path), wenn ein `Gateway:DataMasking:HmacSecret`-Key gesetzt ist, aber nicht gelesen wird. **Performance:** Secrets werden nur einmal im Konstruktor gelesen (`ColumnMaskingProvider.cs:26-28`, Singleton). Der `file:`-Provider liest ebenfalls nur beim Start und cacht nichts pro Request, daher keine Laufzeitkosten.

- In `deploy/podman-compose.yaml:230-238` und `deploy/.env.example`/`generate-env.sh` fünf getrennte Secrets: `MASKING_HMAC_KEY`, `OPENMETADATA_WEBHOOK_SECRET`, `CATALOG_WEBHOOK_SECRET`, `ITSM_WEBHOOK_SECRET`, `BENCHMARK_SECRET_KEY` (je ≥ 32 Byte, `openssl rand`).
- Auslieferung als Podman/Compose-`secrets:` (gemountet unter `/run/secrets/<name>`, Mode 0400, Owner = App-UID) statt Env.
- `DefaultEnvironmentSecretProvider` (`src/Autheris.Infrastructure/Security/DefaultEnvironmentSecretProvider.cs`) um einen `file:`-Referenztyp erweitern (`HmacSecretKeyVaultRef=file:/run/secrets/masking_hmac_key`), mit Pfad-Allowlist (`/run/secrets/`), ohne Symlink-Folgen außerhalb, Trim des abschließenden Newlines. Kein generisches `*_FILE` für beliebige Keys (verhindert Pfad-Injection über Konfiguration).
- **Migrationshinweis:** Masking ist deterministische Pseudonymisierung; ein neuer Masking-Key ändert alle Pseudonyme (Joins/Caches gegen alte Pseudonyme brechen). Für den Bench-Stack unkritisch; für Produktiv-Deployments den bisherigen Wert als Masking-Key weiterverwenden und nur die Webhook-Secrets neu erzeugen.

**Verifikation:** Unit-Test `file:`-Provider (Pfad außerhalb Allowlist ⇒ Exception; Symlink ⇒ Exception); `podman inspect gqlgateway-api | grep -i -E 'hmac|webhook|secret'` liefert keine Werte; Integrationstest: Webhook mit Masking-Key signiert ⇒ 401.

### 3.6 SC-05 & SC-06: Benchmark-Images

**Geklärt (Phase 0): Es gibt zwei getrennte Bench-Startpfade.** Beide müssen gehärtet werden.

| Pfad | Einstieg | Compose / Image | Effektive Umgebung |
|---|---|---|---|
| **A – lokaler Podman-Stack** (`deploy/`) | `make -C deploy bench` / `make up` (`deploy/Makefile:10-12,45-49`) → `deploy/run-benchmark.sh` (`:131` `COMPOSE_FILE=podman-compose.yaml`, `:157` `stage_sources.sh`, `:173` `up -d --build`) | `deploy/podman-compose.yaml` (+ optional `podman-compose.openmetadata.yaml`, `run-benchmark.sh:103`), Image `deploy/containers/gqlgateway-api/Containerfile` | `Production` (`podman-compose.yaml:228`, `Containerfile:57`) mit `appsettings.Benchmark.json` als `appsettings.json`/`.Production.json`/`.Development.json` (`Containerfile:50-52`), User `appuser` (`Containerfile:54`) |
| **B – Hetzner-Vergleichsbench** (`benchmarks/load/`) | `benchmarks/load/run_single_host.sh` → `scripts/01_hetzner_setup.sh`, `02_init_database.sh`, `03_start_gateways.sh` (`:28` `docker compose … up -d --build`), `04_run_benchmarks.sh` (`run_single_host.sh:118-141`) | `benchmarks/load/docker/docker-compose.yml` bzw. `docker-compose.gateway.yml` bei Remote-DB (`03_start_gateways.sh:10-13`), Image `Dockerfile.gql` | `Development` (`Dockerfile.gql:32`, `docker-compose.yml:150`) mit `EnableTestAuthHandler=true` und `danger_bypass_consent_checks=true` (`docker-compose.yml:159-160`), läuft als root (kein `USER`). `appsettings.bench.json` wird als `appsettings.Production.json` kopiert (`Dockerfile.gql:43`) und deshalb **nie geladen**. |

Befund Pfad A: `deploy/scripts/stage_sources.sh:7-11` sucht die Quellen in `../gql`, `/root/gql`, `deploy/gql` und **nicht** im eigenen Repo. Auf dem Entwicklerhost existiert `/root/gql`, und der Stack baut damit einen **fremden Checkout**. Ist `deploy/src_build/src` schon vorhanden, wird er ohne Prüfung wiederverwendet (`stage_sources.sh:15-18`). Damit misst der Bench nicht zuverlässig den aktuellen Code. Die Entfernung aus 3.10 ist deshalb Voraussetzung für jeden Smoke-Test in Phase 2.

Konsequenzen:
- `benchmarks/load/docker/Dockerfile.gql:28-43`: `USER $APP_UID`; `ASPNETCORE_ENVIRONMENT=Benchmark` (nicht `Development`); Config als `appsettings.Benchmark.json` kopieren (behebt Mismatch Zeile 43 vs. 32). `docker-compose.yml:150` und `docker-compose.gateway.yml:96` entsprechend anpassen.
- **Performance-Vorgabe:** Pfad B ist ein Vergleichsbenchmark. Die Umstellung von `Development` auf `Benchmark`/ForwardAuth ändert die gemessene Pipeline (Auth-Handler, Consent-Checks, Dev-Diagnostik). Vor der Umstellung einen Baseline-Lauf (`run_single_host.sh --quick`) sichern, danach erneut messen. Zusätzliche Prüfungen (Audit, Consent) bleiben über die vorhandenen Config-Optionen schaltbar. Ein Security-Default, der Laufzeit kostet, wird nicht implizit aktiviert, sondern explizit per Option im Bench-Profil gesetzt und im Report ausgewiesen. Rein startzeitbezogene Härtungen (Non-root, Secret-Files, Env-Name, Warn-Logs) haben keine Laufzeitkosten. `EnableTestAuthHandler` entfällt nach 3.1 ohnehin im Release-Build ⇒ Bench-Auth auf ForwardAuth umstellen. Label `org.opencontainers.image.description="BENCHMARK ONLY – never publish"`.
- `deploy/containers/gqlgateway-api/Containerfile:50-52`: nur `appsettings.Benchmark.json` kopieren, keine Kopie nach `appsettings.Development.json`/`appsettings.Production.json`.
- TLS: `TrustServerCertificate=True` (`podman-compose.yaml:242`) und fehlendes `SSL Mode` (Zeile 241) entweder über explizites, beim Start als `DANGER` geloggtes `danger_allow_insecure_transport` (nur zulässig, wenn `Environment == Benchmark`) oder echte TLS-Zertifikate (selbstsignierte CA, im Gateway-Container als Trust-Anchor). `Insecure.warn_allow_unsigned_s3_requests` analog.
- Zuerst klären (Review: „stack either fails closed or something else relaxes the check"): Pfad A (s. o.) nach Entfernen von `stage_sources.sh` einmal aus dem Repo-Root starten und dokumentieren, welche Prüfung greift.

**Verifikation:** `podman run --rm <bench-image> id -u` ≠ 0; Startup-Log enthält `DANGER`-Zeile; Test: `danger_allow_insecure_transport=true` in `Production` ⇒ Startabbruch.

### 3.7 SC-08: Reverse Proxy

- `nginx.conf:109-113`: `location /metrics { deny all; }` bzw. nur von Prometheus-IP (`allow 10.89.77.<prom>; deny all;`), Prometheus scraped direkt den Gateway mit Credentials.
- Geo-Block `nginx.conf:33-67`: Admin-Mapping entfernen oder auf `127.0.0.1` beschränken; `X-Benchmark-Role` aus Client-Requests immer strippen (wie `x-autheris-*`, Zeile 123).
- Compose-Netzwerk `gateway-bench-net` mit `internal: true` für Backend-Services; nur der Proxy hängt zusätzlich am Edge-Netz.
- Banner-Kommentar + README: „nie internet-facing"; optional TLS-Listener 8443 mit selbstsigniertem Zertifikat.

**Verifikation:** `curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/metrics` ⇒ 403; Request mit `X-Benchmark-Role: Admin` aus einem Fremdcontainer im Netz ⇒ keine Admin-Rolle (Gateway-Audit-Log).

### 3.8 SC-09 & SC-11: Container-Härtung

Basis-Profil für `docker-compose.yml` (Gateway):

```yaml
services:
  gql-gateway:
    user: "1654:1654"                # $APP_UID des aspnet-Images
    security_opt: [ "no-new-privileges:true" ]
    cap_drop: [ ALL ]
    read_only: true
    tmpfs:
      - /tmp:rw,noexec,nosuid,size=64m
    volumes:
      - autheris-data:/app/data     # SQLite-Governance-DB + Garnet bleiben beschreibbar
    pids_limit: 512
    mem_limit: 1g
    cpus: 2
    healthcheck:
      test: ["CMD", "dotnet", "Autheris.Api.dll", "--healthcheck"]   # aspnet-Image hat kein curl/wget
      interval: 30s
      timeout: 5s
      retries: 3
```

- **Keine Pauschalübernahme:** `cap_drop: [ALL]` bricht Postgres/Redis/MinIO/SQL Server, die beim Start `CHOWN`, `SETUID`, `SETGID`, `DAC_OVERRIDE`, `FOWNER` benötigen. Für diese Services `cap_drop: [ALL]` + gezieltes `cap_add` oder Start direkt als Nicht-root-UID; `read_only` nur, wo Schreibpfade bekannt sind (Postgres: zusätzlich tmpfs `/var/run/postgresql`).
- Gateway unter `read_only` vorab prüfen: DataProtection-Key-Ring, DuckDB-Temp/Extensions (`HOME`), Plugin-Verzeichnisse, Garnet-Checkpoints – alle nach `/app/data` oder tmpfs lenken; `/tmp` mit `noexec` gegen DuckDB-Extension-Laden testen.
- Healthcheck: aspnet-Basisimage enthält kein `curl`; daher eingebauter `--healthcheck`-Modus (Request auf `http://127.0.0.1:8080/health/live`) oder `HEALTHCHECK` im `Dockerfile` analog.
- SC-11: `USER` (numerisch) in `deploy/containers/mock-extensions`, `lakehouse-seed`, `load-generator`, `reverse-proxy` (nginx: `nginxinc/nginx-unprivileged` oder `listen 8080` + `pid /tmp/nginx.pid`); `sqlserver/Containerfile:3` nur für Paketinstallation `USER root`, danach zurück auf `mssql`. `entrypoint.sh:12,21`: `export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"` und `-P` entfernen.

**Verifikation:** Skript `deploy/scripts/verify-hardening.sh` (CI-Job auf Compose-Configs mit `docker compose config` + `yq`): jeder Service hat `cap_drop`, `no-new-privileges`, `user` ≠ 0/root; zur Laufzeit `podman ps -q | xargs podman inspect -f '{{.Name}} {{.Config.User}} {{.HostConfig.ReadonlyRootfs}}'`; `ps aux` im SQL-Container zeigt kein Passwort.

### 3.9 SC-10: Image-Pinning & veraltete Images

- Alle Images in `deploy/podman-compose.yaml:30,57,127,202,317,337`, `deploy/podman-compose.openmetadata.yaml:21,59`, `benchmarks/load/docker/docker-compose.yml:12,54`, `deploy/containers/load-generator/Containerfile:5` (`grafana/k6:latest`), `sqlserver`, `mock-extensions` auf `name:tag@sha256:<digest>` (Tag zur Lesbarkeit beibehalten; Digest via `crane digest` / `skopeo inspect`, **Multi-Arch-Index-Digest**, nicht plattformspezifisch).
- MinIO (`RELEASE.2024-10-02`) und OpenSearch `2.11.0` aktualisieren; OpenSearch: Security-Plugin aktivieren oder Port 9200 nicht publizieren (`podman-compose.openmetadata.yaml:27`).
- Dependabot `docker-compose` (3.4) pflegt Digests.

**Verifikation:** CI-Check `grep -rnE '^\s*(image:|FROM)\s' … | grep -v '@sha256:'` ⇒ leer (Ausnahme: `FROM <stage>`); Trivy-Scan der Drittimages als wöchentlicher, nicht blockierender Report.

### 3.10 SC-12: Reproduzierbare Benchmark-Builds

- `benchmarks/load/docker/Dockerfile.gql:5-15` kopiert bisher nur `*.csproj` + `Directory.Build.props`; **`NuGet.config` und `packages.lock.json` fehlen** – `--locked-mode` würde ohne sie fehlschlagen bzw. nichts erzwingen. Beide je Projekt mitkopieren, dann `dotnet restore --locked-mode`.
- `deploy/containers/gqlgateway-api/Containerfile:22,30`: `--locked-mode`, `/p:TreatWarningsAsErrors=false` entfernen.
- `stage_sources.sh:7-12` (Quellen aus `/root/gql`) entfernen; Build-Kontext ist das Repo-Root, Image-Label `org.opencontainers.image.revision=$(git rev-parse HEAD)`.

**Verifikation:** CI-Job baut beide Bench-Images (ohne Push); Lock-Drift-Canary ⇒ rot.

### 3.11 SC-13, SC-14, SC-16, SC-18: Restpunkte

- **SC-13:** `deploy/scripts/seed_openmetadata.py:148` Passwort aus Env (`OM_SEED_PASSWORD`, von `generate-env.sh` erzeugt); Dev-Fallbacks in `SqlServerGovernanceRepository.cs:71` und `PurviewDataCatalogClient.cs:133` bleiben Dev-gated, werden aber in `.gitleaks.toml` mit Pfad + Regex + Begründung allowlisted; `appsettings.Benchmark.json:83,104,109,115` Tokens per Env-Substitution.
- **SC-14:** `README.md:535,544`, `docs/configuration-guide.md:474-475,915,1011`, `docs/extensions/lakehouse-connector-guide.md:63`, `docs/features/f-sql-02-governed-stored-procedures.md:368,380` auf `<GENERATE_STRONG_SECRET>` + Hinweis `openssl rand -base64 32`. Verifikation: Gitleaks über `docs/` und `README.md` ohne Allowlist grün.
- **SC-16:** `src/Autheris.Api/Assets/swagger-ui/package.json` mit `"swagger-ui-dist": "5.x.y"` (exakt) + `package-lock.json`; Skript `scripts/revendor-swagger-ui.sh` (`npm ci --ignore-scripts`, Kopie der benötigten Dateien, Hash-Manifest). CI-Check: Hashes der vendorten Dateien == Manifest. Dependabot-npm-Eintrag (3.4).
- **SC-18:** `benchmarks/load/hcloud/cloud-init.yaml:65-92`: `dotnet-install.sh` gegen veröffentlichte Checksumme prüfen (`sha256sum -c`) oder Microsoft-apt-Repo mit `signed-by` nutzen; NodeSource durch apt-Repo mit `signed-by` + gepinnter Version ersetzen; Fingerprint der Docker-/k6-GPG-Keys prüfen (`gpg --show-keys --with-fingerprint` gegen erwarteten Wert). Provisioning-Skript legt `hcloud firewall` an (SSH nur von Admin-IP, DB-/Gateway-Ports nur privates Netz). Verifikation: `hcloud firewall describe`; Cloud-Init mit manipulierter Checksumme ⇒ Abbruch.

### 3.12 Ergänzungen aus dem Plan-Review (nicht im Security Review)

- **SDK-Pinning:** `global.json` fehlt (s. 3.4).
- **Package Source Mapping:** `NuGet.config:7-11` mappt `*` auf nuget.org – sicher, solange es nur eine Quelle gibt. Regel dokumentieren: Bei Aufnahme eines privaten Feeds **muss** das interne Präfix (`Autheris.*`, `Liebherr.*`) exklusiv auf diesen Feed gemappt werden; zusätzlich Präfix auf nuget.org reservieren. Optional `<trustedSigners>` mit nuget.org-Repository-Signatur + `signatureValidationMode=require`.
- **Deterministische Builds:** `ContinuousIntegrationBuild=true` (bei `GITHUB_ACTIONS`), `Deterministic`, SourceLink – Voraussetzung für Provenance-Nachvollziehbarkeit.
- **Analyzer-Unterdrückung:** `Directory.Build.props:16` unterdrückt u. a. `CA1305/CA1307/CA1310` (Culture/Ordinal) – Abbau als eigener Backlog-Punkt; keine Security-relevanten CA-Regeln (`CA2100`, `CA3xxx`, `CA5xxx`) unterdrücken (CI-Check).
- **Workflow-Härtung:** OpenSSF Scorecard (`ossf/scorecard-action`, wöchentlich); `step-security/harden-runner` im Audit-Modus für Publish/Release; `persist-credentials: false`.
- **Lokale Credential-Hygiene:** Git-Remotes dürfen keine eingebetteten PATs enthalten (`https://<token>@github.com/…`); Credential-Helper / `gh auth` verwenden, gefundene Tokens sofort widerrufen. Gitleaks-Pre-Commit-Hook optional (`.pre-commit-config.yaml`).
- **IaC-/Config-Tests** (Review §6): Test, dass ausgelieferte `appsettings.json` keine Secrets enthält und das Image als Nicht-root läuft (`docker inspect -f '{{.Config.User}}'`).

---

## 4. Phasenplan

Reihenfolge so gewählt, dass keine neue CI-Pflicht vor ihrer Baseline aktiviert wird.

1. **Phase 0 (Baseline, nicht blockierend):** `global.json`; Gitleaks, CodeQL, Trivy, Hadolint, Coverage zunächst **report-only** einführen; Funde triagieren (Allowlist/`.trivyignore` mit Ablaufdatum). ~~Klären: Bench-Stack-Startpfad (3.6), Wirksamkeit `Gateway__DataMasking__HmacSecret` (3.5)~~ **geklärt:** Es gibt zwei Startpfade, A `deploy/` (Podman, `Production`) und B `benchmarks/load/` (Docker, `Development`), siehe 3.6. `stage_sources.sh` baut aus `/root/gql` statt aus dem Repo. `Gateway__DataMasking__HmacSecret` ist wirkungslos, der Masking-Key kommt aus `HMAC_SECRET_KEY` (siehe 3.5). Zusätzlich: Performance-Baseline beider Bench-Pfade sichern, bevor Phase 2 etwas am Bench-Profil ändert.
2. **Phase 1 (Code & Compiler-Schranken):** SC-04 (Build-Symbol + Publish-Check), SC-07 (`file:`-Provider, getrennte Secrets), SC-13, SC-14.
3. **Phase 2 (Container & Compose):** SC-05, SC-06, SC-08, SC-09, SC-10, SC-11, SC-12, SC-18 – je Service einzeln, nach jedem Schritt Bench-Stack-Smoke-Test.
4. **Phase 3 (CI/CD & Release):** SC-17, SC-15, SC-16, SC-03 (Gates auf blockierend schalten + Branch-Ruleset), SC-02, zuletzt SC-01 (Tag-Politik + Digest-Pinning in `docker-compose.yml`, da Konsumenten betroffen → Changelog-Hinweis).
5. **Phase 4 (Durchsetzung):** Admission-Policy (Invariante 3) erst, wenn mindestens ein Release signiert ist.

---

## 5. Abnahmekriterien

- [ ] Publish-Output (`dist/publish`, alle Release-RIDs) enthält keinen `TestAuthHandler`-Typ und kein `"TestAuth"`-Scheme (CI-Check auf dem Publish-Artefakt); Release-Tests bleiben grün.
- [ ] Canary-PRs belegen: Gitleaks, CodeQL, Dependency Review, Hadolint, NuGet-Audit (JSON) und Trivy (CRITICAL/HIGH, fixable) brechen ab; Merge auf `main` ist per Ruleset blockiert.
- [ ] Bei rotem Trivy-Scan wird **kein** Image gepusht.
- [ ] `cosign verify` mit fester Identity-Regex und `gh attestation verify` sind für das zuletzt veröffentlichte Image und alle Release-Assets erfolgreich; `cosign verify-blob` für `SHA256SUMS` erfolgreich.
- [ ] `latest`/`getting-started` werden nur aus `v*`-Tags erzeugt; `docker-compose.yml` referenziert `@sha256:`.
- [ ] Release-Job mit `contents: write` führt keinen Build-Code aus; RID-Restore läuft im Locked Mode.
- [ ] Kein Container im Compose-Setup läuft als UID 0; jeder Service hat `no-new-privileges` und `cap_drop: [ALL]` (+ begründetes, minimales `cap_add`) – geprüft durch `verify-hardening.sh`.
- [ ] Alle `image:`/`FROM`-Referenzen sind per Digest fixiert (CI-Check), OpenSearch-Port 9200 nicht publiziert.
- [ ] Fünf getrennte Secrets, Auslieferung als Secret-Files; `inspect` zeigt keine Secret-Werte.
- [ ] Dependabot deckt `/`, `deploy/containers/*`, `benchmarks/*`, `tools/*`, pip, npm (inkl. swagger-ui) und `docker-compose` ab.

---

## 6. Security Architecture Review & Ergänzungen (Security Expert)

> [!IMPORTANT]
> **Sicherheits-Invariante 1: Doppelter Schutz gegen Test-Auth-Bypass (Compile-Time & Publish-Gate)**  
> Der `TestAuthHandler` stellt bei Fehlkonfiguration einen vollständigen Authentifizierungs-Bypass dar.  
> Neben der bedingten Kompilierung (eigenes Symbol `AUTHERIS_TEST_AUTH`, **nicht** `DEBUG`, s. 3.1) prüft ein verbindlicher CI-Check (`SecurityReleaseBinarySanityTests` bzw. Tool in `tools/`) das **publizierte** Assembly per Metadaten-Reflection und schlägt fehl, falls ein Typ namens `TestAuthHandler` oder der Scheme-Name `"TestAuth"` darin gefunden wird. Ein Test gegen das im Testlauf geladene Assembly wäre wirkungslos, da dieses das Symbol bewusst enthält.

> [!CAUTION]
> **Sicherheits-Invariante 2: Notfall-Schlüsselrotation für Masking-Secrets**  
> Wird der Masking-Key kompromittiert, muss eine Rotation geplant möglich sein.  
> **Vorgabe:** Pseudonyme werden mit einer Key-ID versioniert (`HmacSecretKeyVaultRef` + `HmacKeyId`). Da deterministische Pseudonyme mit neuem Schlüssel **andere** Werte ergeben, ist eine „Validierung mit dem alten Schlüssel" nicht ausreichend: Rotation erfordert ein Re-Pseudonymisierungs-/Cache-Invalidierungs-Runbook (Cache-Flush, Neuaufbau abgeleiteter Tabellen, Kommunikation an Konsumenten, die Pseudonyme joinen). `ColumnMaskingProvider` (`src/Autheris.Application/Services/ColumnMaskingProvider.cs`) wird erst dann um einen Key-Ring erweitert, wenn ein konkreter Konsument Übergangsfenster benötigt (eigener Plan, nicht Teil dieses Plans).  
> Webhook-Secrets (OpenMetadata/Catalog/ITSM) sind dagegen sofort rotierbar und unterstützen ein Dual-Secret-Fenster (aktuell + vorherig) bei der Signaturprüfung.

> [!TIP]
> **Sicherheits-Invariante 3: Sigstore / Cosign Admission Gate**  
> Das Erzeugen von Signaturen in CI ist nur die halbe Miete. Es wird ein **neues** Verzeichnis `deploy/policy/` angelegt (weder `deploy/kubernetes/` noch `deploy/podman/` existieren) mit:  
> - `podman/policy.json` + `registries.d/ghcr.yaml` (`use-sigstore-attachments: true`) mit `sigstoreSigned`/`fulcio`-Anforderung (containers/image ≥ 5.26),  
> - optional `kubernetes/kyverno-verify-images.yaml` (`verifyImages`, `mutateDigest: true`).  
> Erwartete Identität: Issuer `https://token.actions.githubusercontent.com`, Subject-Regex `^https://github\.com/themulle/autheris/\.github/workflows/docker-publish\.yml@refs/tags/v.+$` (Releases werden aus `v*`-Tags gebaut; `refs/heads/main` nur für Pre-Release-Kanäle zulassen).  
> **Verifikation:** `podman pull` eines unsignierten bzw. mit fremder Identität signierten Images ⇒ Ablehnung; signiertes Release-Image ⇒ Erfolg.
