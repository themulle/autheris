# Implementation Plan: Security Wave C, part 2 (open remainders)

- **Document ID:** `IMPL-SEC-WAVE-C-2`
- **Parent plans:** [`../00-master-plan-overview.md`](../00-master-plan-overview.md), [`../security.md`](../security.md) (section 4, Wave C Low findings)
- **Scope:** open remainders of `SR-L-13` .. `SR-L-24`, `SR-D-06` .. `SR-D-09`
- **Date:** 2026-10-14
- **Base:** `main` @ `a4137626`
- **Detailed finding text:** `git show d8db81cc^:docs/plans/2026-10-14-security-code-review/<file>.md` (finding IDs `SR-*`).

WP-SEC2-01 .. WP-SEC2-07 shipped in Waves 1-3 (SR-L-13 .. SR-L-24 closed). WP-SEC2-08 and WP-SEC2-09 shipped their main parts; the remainders below are still open. The full original design is in git history: `git show a4137626:docs/plans/implementation/impl-security-wave-c-2.md`.

---

## 1. Verification table (open items only)

| ID | Status on main | Evidence |
|---|---|---|
| SR-D-06 | **Partially resolved** | ServiceMonitor auth (`deploy/helm/autheris/templates/servicemonitor.yaml`, bearer/mTLS with `requireAuth` guard) and removal of the `prometheus.io/*` annotations are done. Open: the gateway side has only a generic `MapMetrics().RequireAuthorization()` (`src/Autheris.Api/Extensions/GatewayApplicationBuilderExtensions.cs:364`), no dedicated metrics-scraper policy; `templates/configmap.yaml` wires only `RequireTrustedProxy` and `AllowedHosts`, not the other required production settings (`Gateway__Authentication__*`, trusted-proxy list, `Gateway__DataSources__RequireTenantColumn`). |
| SR-D-07 | **Partially resolved** | Default is `false` (`Directory.Build.props:17`) and the benchmark `Containerfile:31` passes the flag. Open: no test asserts that a Release publish contains no TestAuth handler. |
| SR-D-08 | **Partially resolved** | Reverse proxy runs as `USER nginx` with `cap_drop: [ALL]`; per-consumer secrets in `.env.example` / `generate-env.sh`. Open: `TrustServerCertificate=True` remains in `deploy/podman-compose.yaml:245` and `deploy/containers/gqlgateway-api/appsettings.Benchmark.json:72` (no SQL Server CA); the proxy service has no `read_only: true`. Benchmark-only. |
| SR-D-09 | **Partially resolved** | `global.json` uses `rollForward: latestPatch`; all workflows use `global-json-file: global.json`. Open: runtime/SDK images carry the floating `10.0` tag next to the digest (`Dockerfile:6`, `Dockerfile.production:6`, `Dockerfile.staging:6`, `deploy/containers/gqlgateway-api/Containerfile:7,34`); `NuGetAuditLevel` is still `moderate` (`Directory.Build.props:12`, decision D5). |

---

## 2. Work packages

### WP-SEC2-08 (remainder): Release build check, image tags, NuGet audit level (SR-D-07, SR-D-09)

- **Design:**
  1. Add `tests/Autheris.Tests.Architecture/ReleaseBuildExcludesTestAuthTests.cs`: a Release build with default properties must not contain the TestAuth handler type (scan the built `Autheris.Api` assembly). Then drop the now-redundant `-p:AutherisIncludeTestAuth=false` from the publish workflows.
  2. Write the patch tag next to each image digest (`10.0.x@sha256:...`) so a digest refresh is reviewable.
  3. D5: set `NuGetAuditLevel` to `low` if the tree builds clean with warnings as errors; otherwise keep `moderate` and document the exception list.
- **Overlap:** one PR owner for `Directory.Build.props` and the workflows (lane L8, with WP-ARC-C-21).
- **Acceptance:** the architecture test fails on a Release build with TestAuth compiled in; every image reference names an exact patch tag plus digest.
- **Effort:** S. **Risk:** low.

### WP-SEC2-09 (remainder): Metrics scrape policy, production config, benchmark TLS (SR-D-06, SR-D-08)

- **Design (SR-D-06):** add a dedicated `GatewayPolicies.MetricsScrape` policy (scraper role/claim or a static `Gateway:Metrics:ScrapeToken`) and apply it to `MapMetrics()` (D6: bearer). Wire the required production settings in `templates/configmap.yaml` via values with `required` guards (`Gateway__Authentication__*`, trusted-proxy list, `Gateway__DataSources__RequireTenantColumn`, metrics policy).
- **Design (SR-D-08):** generate a self-signed SQL Server CA in `deploy/scripts/generate-env.sh`, mount it, and drop `TrustServerCertificate=True` from `podman-compose.yaml:245` and `appsettings.Benchmark.json:72`; add `read_only: true` (with tmpfs for the nginx writable paths) to the proxy service; extend `deploy/scripts/verify-hardening.sh` to assert no `TrustServerCertificate=True` under `deploy/`.
- **Acceptance:** `helm template -f values.production.yaml` fails when a required production setting is missing; no `TrustServerCertificate=True` in `deploy/`.
- **Effort:** M. **Risk:** low (not on the runtime path), medium for benchmark TLS wiring.

### Deprecation follow-ups (from WP-SEC2-01, WP-SEC2-02)

Autheris has no production deployment yet, so the one-release shims can be removed in any wave:

- Remove `Gateway:Webhooks:EmitLegacySignature` (`src/Autheris.Domain/Options/WebhooksOptions.cs:22`) and the legacy body-only `X-Autheris-Signature` header on CloudEvents deliveries (`src/Autheris.Application/Events/Services/CloudEventWebhookDispatcher.cs:117`, `WebhookSignature.LegacySignatureHeader`).
- Remove the `[Obsolete]` `DocumentDepositedEvent.SourceFilePath` alias (`src/Autheris.Domain/Events/GatewayEventModels.cs:174-175`).

---

## 3. Open decisions

- **D5 (NuGet audit level):** `low` vs staying `moderate`. Recommendation: `low` if the tree builds clean; otherwise keep `moderate` and track exceptions.
- **D6 (metrics auth mechanism):** bearer token vs mTLS. Recommendation: bearer with a dedicated metrics scraper policy; mTLS only where a service mesh already provides it. The Helm side supports both already.
