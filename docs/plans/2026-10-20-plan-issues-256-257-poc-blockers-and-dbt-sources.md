# Implementation Plan: PLAN-OPS-257-256 - PoC Deployment Blockers Remediation & dbt Sources Ingestion Architecture

**Document ID:** `PLAN-OPS-257-256`  
**Date:** 2026-10-20  
**Status:** APPROVED BY APPSEC (Phase 3 Security Sign-Off Complete) ✅  
**Author:** Principal C# & .NET Solution Architect (Phase 2) / AppSec Expert (Phase 3)  
**Input PRDs:**
- [`REQ-OPS-257`](2026-10-20-req-issue-257-poc-deployment-blockers.md) (Issue #257: PoC Deployment Blockers Remediation)
- [`REQ-DOC-256`](2026-10-20-req-issue-256-dbt-sources-manifest-ingestion.md) (Issue #256: dbt Sources Manifest Ingestion Documentation & End-to-End Test Suite)
**Parent Plan:** [`00-master-plan-overview.md`](00-master-plan-overview.md)  
**Target Delivery Window:** v1.2.6 Hotfix / Next Release Sprint  
**Target Runtimes & Frameworks:** .NET 10, C# 13, GitHub Actions, Docker, Cosign, SQLite 3, PostgreSQL 16, SQL Server 2022  
**Size:** M (AC-1 Configuration: S, AC-2 Pipeline: S, AC-3 Diagnostics: S, AC-4 Schema: S, Issue #256 dbt: S)  

---

## 1. Executive Summary & Strategic Fit

During field trial evaluations and Proof-of-Concept (PoC) deployments of Autheris v1.2.x, operational, diagnostic, and pipeline blockers were identified that impede seamless customer onboarding, container deployment, and data governance integration. Concurrently, analytics engineering teams integrating **dbt (data build tool)** require explicit documentation and verified automated testing of raw upstream `sources` ingestion from `manifest.json`.

This implementation plan establishes the architectural design, non-negotiable security boundaries, exact C# 13 / .NET 10 code changes, CI/CD workflow updates, and automated test suites to resolve all blockers across both issues:

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│             PLAN-OPS-257-256 ARCHITECTURAL WORKSTREAM TOPOLOGY                                   │
│                                                                                                  │
│   CONFIGURATION RESILIENCY (AC-1)              CI/CD RELEASE PIPELINE (AC-2)                     │
│   ┌────────────────────────────────┐          ┌──────────────────────────────────────────────┐   │
│   │ SerilogSectionGuard            │          │ .github/workflows/docker-publish.yml         │   │
│   │ - Soften hard crash            │          │ - Tag: ghcr.io/${REPO}:${REF_NAME} (v*.*.*)   │   │
│   │ - AUTHERIS_ALLOW_LEGACY_CONFIG │          │ - Tags: :latest & :getting-started on release│   │
│   │ - Console deprecation warning  │          │ - Cosign Keyless OIDC Digest Signing         │   │
│   │ - Strict SEC-OT06-14 header    │          │ - Strict Identity Regex Verification         │   │
│   │   guard preservation           │          └──────────────────────────────────────────────┘   │
│   └────────────────────────────────┘                                                             │
│                                                                                                  │
│   DIAGNOSTIC CLARITY (AC-3)                    GOVERNANCE SCHEMA ALIGNMENT (AC-4)                │
│   ┌────────────────────────────────┐          ┌──────────────────────────────────────────────┐   │
│   │ TableAccessPolicy & Consent    │          │ Governance Database Migration & Seeding      │   │
│   │ - Capture ReBAC Allowed state  │          │ - 80 -> 83 Schema Elements Footprint         │   │
│   │ - Fail-closed Consent denial   │          │ - CATALOG_STATE Table & Singleton Seed       │   │
│   │ - DeniedReasons enriched:      │          │ - TABLE_RELATIONS Parent & Child Indexes     │   │
│   │   "(ReBAC: Allowed, Consent:   │          │ - Idempotent --migrate-governance CLI        │   │
│   │    Denied - Zero-Trust...)"    │          │ - Verified across SQLite, PG, SQL Server     │   │
│   └────────────────────────────────┘          └──────────────────────────────────────────────┘   │
│                                                                                                  │
│   dbt SOURCES MANIFEST INGESTION & TEST SUITE (Issue #256)                                       │
│   ┌──────────────────────────────────────────────────────────────────────────────────────────┐   │
│   │ 1. Documentation: docs/extensions/dbt-integration-guide.md (Sections 1 & 5)              │   │
│   │ 2. Automated Test: tests/Autheris.Tests.Unit/Dbt/DbtSourcesManifestIngestionTests.cs     │   │
│   │    - Ingestion of sources-only manifest.json (materialization = 'source')                │   │
│   │    - Extraction of columns, tags, meta and automated PII / RLS proposal generation       │   │
│   └──────────────────────────────────────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Architectural Decisions & Non-Negotiable AppSec Boundaries

| # | Topic | Decision | Architectural Rationale |
|---|---|---|---|
| **AD-01** | **Configuration Deprecation Grace** | In `SerilogSectionGuard`, presence of a populated `"Serilog"` section emits a prominent warning to `Console.Error` instead of throwing an unhandled `InvalidOperationException` when legacy tolerance is active (`AUTHERIS_ALLOW_LEGACY_CONFIG=true` or default graceful deprecation mode). | Prevents Kubernetes container `CrashLoopBackOff` during customer upgrades from v1.2.4 while clearly steering operators to modern `Logging:LogLevel` and `Gateway:Telemetry:Otlp` configurations. |
| **AD-02** | **AppSec Non-Negotiable Guard (SEC-OT06-14)** | The guard against configuring `Gateway:Telemetry:Otlp:Headers` in configuration files remains **strictly fatal** (`throw new InvalidOperationException(...)`), completely independent of `AUTHERIS_ALLOW_LEGACY_CONFIG`. | Exporter credentials, Authorization bearer tokens, and API keys must never be committed to repository configuration files. Kubernetes Secret projection into `OTEL_EXPORTER_OTLP_HEADERS` is mandatory. |
| **AD-03** | **Zero-Trust Policy Intersection Invariance** | ReBAC and Consent are evaluated in a strict fail-closed logical AND intersection (`ReBAC Allowed ∧ Consent Allowed ∧ Casbin Allowed`). ReBAC Allowed status **never** bypasses or short-circuits Consent resolution. | Complies with NIST SP 800-207 Zero-Trust Architecture and GDPR Art. 9/15. Diagnostic attribution in `DeniedReasons` clarifies the root cause without altering authorization outcomes. |
| **AD-04** | **ReBAC Allowed Diagnostic Attribution** | When ReBAC evaluates to `Allowed` (via enforced evaluation or ReBAC gate check) and Consent resolution denies access, `decision.DeniedReasons` appends `"(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)"` while preserving all existing consent reason codes. | Eliminates ambiguity in zero-trust policy decision logs, accelerating customer onboarding and audit triage. |
| **AD-05** | **Supply-Chain Cryptographic Integrity** | The container release pipeline (`docker-publish.yml`) publishes semantic version tags (`ghcr.io/themulle/autheris:vX.Y.Z`), `:latest`, and `:getting-started` upon tag push, signs the digest with Cosign keyless OIDC, and enforces identity regex `^https://github\.com/themulle/autheris/\.github/workflows/docker-publish\.yml@refs/(heads/main|tags/v.+)$`. | Satisfies SLSA Level 3 provenance, SEC Rule 17a-4, and container authenticity standards without exposing long-lived signing keys. |
| **AD-06** | **Governance Schema Evolution Alignment (80 -> 83)** | Baseline migrations and `--migrate-governance` re-seeding maintain deterministic idempotence for `CATALOG_STATE` and `TABLE_RELATIONS` indexes (`IX_TABLE_RELATIONS_PARENT`, `IX_TABLE_RELATIONS_CHILD`) across SQLite, PostgreSQL, and SQL Server. | Guarantees safe zero-downtime database upgrades and state snapshot consistency across all supported database backends. |
| **AD-07** | **dbt Sources First-Class Citizen** | Top-level `sources` in `manifest.json` are treated as first-class catalog entities materialized as `"source"`, populating dataset models, columns, tags, and metadata proposals. | Bridges analytics engineering and governance, ensuring raw ingested data assets participate in automated PII discovery and approval workflows. |

---

## 3. Component-by-Component Implementation Specification

### 3.1 AC-1: Configuration Resiliency (`SerilogSectionGuard`)

#### 3.1.1 Current State & Flaw
In [`src/Autheris.Api/Configuration/SerilogSectionGuard.cs`](file:///root/autheris/src/Autheris.Api/Configuration/SerilogSectionGuard.cs#L16-L22):
```csharp
var serilogSection = configuration.GetSection("Serilog");
if (serilogSection.Exists() && serilogSection.GetChildren().Any())
{
    throw new InvalidOperationException(
        "The 'Serilog' configuration section is no longer supported (removed in BL-06 in favor of OpenTelemetry logging). " +
        "Please configure log levels under 'Logging:LogLevel' and OpenTelemetry exporters under 'Gateway:Telemetry:Otlp'.");
}
```
When upgrading customer clusters from v1.2.4 where Helm ConfigMaps still supply `"Serilog"`, the API container terminates abruptly during `Program.cs` startup before OpenTelemetry logging is initialized.

#### 3.1.2 Target Design
Refactor `SerilogSectionGuard` to:
1. Provide a primary entry point `ThrowIfConfigured(IConfiguration configuration)` that delegates to `CheckConfiguration(IConfiguration configuration, TextWriter? warningWriter = null)`.
2. Inspect `AUTHERIS_ALLOW_LEGACY_CONFIG` via environment variable or `IConfiguration` (case-insensitive string comparison for `"true"`).
3. If `"Serilog"` exists and contains children:
   - If legacy tolerance is active (`AUTHERIS_ALLOW_LEGACY_CONFIG=true` or default graceful mode):
     Write the exact deprecation warning to `warningWriter ?? Console.Error`:
     `"WARNING: The 'Serilog' configuration section is deprecated and ignored in Autheris v1.2.5+ (migrated to OpenTelemetry logging). Please configure log levels under 'Logging:LogLevel' and OpenTelemetry exporters under 'Gateway:Telemetry:Otlp'."`
   - If legacy tolerance is explicitly set to `"false"` (`AUTHERIS_ALLOW_LEGACY_CONFIG=false`):
     Throw `InvalidOperationException` with the legacy migration guidance message.
4. **SEC-OT06-14 Hard Gate:**
   Unconditionally inspect `Gateway:Telemetry:Otlp:Headers`. If non-empty string or contains child keys, throw `InvalidOperationException`:
   `"Configuring 'Gateway:Telemetry:Otlp:Headers' in appsettings is forbidden (SEC-OT06-14). Configure exporter credentials via the standard environment variable 'OTEL_EXPORTER_OTLP_HEADERS' sourced from a Kubernetes Secret."`

#### 3.1.3 Exact Source Implementation
In [`src/Autheris.Api/Configuration/SerilogSectionGuard.cs`](file:///root/autheris/src/Autheris.Api/Configuration/SerilogSectionGuard.cs):
```csharp
namespace Autheris.Api.Configuration;

using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Startup guard ensuring legacy Serilog configuration sections are gracefully deprecated
/// and forbidden OTLP header keys are strictly rejected. Enforces D-12 and SEC-OT06-14.
/// </summary>
public static class SerilogSectionGuard
{
    public const string DeprecationWarningMessage =
        "WARNING: The 'Serilog' configuration section is deprecated and ignored in Autheris v1.2.5+ " +
        "(migrated to OpenTelemetry logging). Please configure log levels under 'Logging:LogLevel' " +
        "and OpenTelemetry exporters under 'Gateway:Telemetry:Otlp'.";

    public const string OtlpHeadersForbiddenMessage =
        "Configuring 'Gateway:Telemetry:Otlp:Headers' in appsettings is forbidden (SEC-OT06-14). " +
        "Configure exporter credentials via the standard environment variable 'OTEL_EXPORTER_OTLP_HEADERS' sourced from a Kubernetes Secret.";

    public static void ThrowIfConfigured(IConfiguration configuration) =>
        CheckConfiguration(configuration, null);

    public static void CheckConfiguration(IConfiguration configuration, TextWriter? warningWriter = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serilogSection = configuration.GetSection("Serilog");
        if (serilogSection.Exists() && serilogSection.GetChildren().Any())
        {
            var allowLegacyEnv = Environment.GetEnvironmentVariable("AUTHERIS_ALLOW_LEGACY_CONFIG");
            var allowLegacyCfg = configuration["AUTHERIS_ALLOW_LEGACY_CONFIG"];
            var isStrictRejection = string.Equals(allowLegacyEnv, "false", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(allowLegacyCfg, "false", StringComparison.OrdinalIgnoreCase);

            if (isStrictRejection)
            {
                throw new InvalidOperationException(
                    "The 'Serilog' configuration section is no longer supported (removed in BL-06 in favor of OpenTelemetry logging). " +
                    "Please configure log levels under 'Logging:LogLevel' and OpenTelemetry exporters under 'Gateway:Telemetry:Otlp'.");
            }

            var writer = warningWriter ?? Console.Error;
            writer.WriteLine(DeprecationWarningMessage);
        }

        var headersSection = configuration.GetSection("Gateway:Telemetry:Otlp:Headers");
        if (headersSection.Exists() && (!string.IsNullOrWhiteSpace(headersSection.Value) || headersSection.GetChildren().Any()))
        {
            throw new InvalidOperationException(OtlpHeadersForbiddenMessage);
        }
    }
}
```

#### 3.1.4 Unit Test Specifications (`SerilogSectionGuardTests.cs`)
File: `tests/Autheris.Tests.Unit/Configuration/SerilogSectionGuardTests.cs`
- `CleanConfig_DoesNotThrowOrWarn`: Empty configuration passes without exception and zero output.
- `SerilogSection_WithDefaultGracefulTolerance_EmitsWarning_DoesNotThrow`: Config with `"Serilog:MinimumLevel:Default": "Information"` outputs `DeprecationWarningMessage` to `StringWriter` and does not throw.
- `SerilogSection_WithAllowLegacyTrue_EmitsWarning_DoesNotThrow`: Explicit `AUTHERIS_ALLOW_LEGACY_CONFIG=true` outputs warning and executes cleanly.
- `SerilogSection_WithAllowLegacyFalse_ThrowsInvalidOperationException`: Explicit `AUTHERIS_ALLOW_LEGACY_CONFIG=false` throws `InvalidOperationException`.
- `OtlpHeaders_WithStringValue_ThrowsSecurityException`: `"Gateway:Telemetry:Otlp:Headers": "Authorization=Bearer xyz"` throws `InvalidOperationException` containing `SEC-OT06-14`.
- `OtlpHeaders_WithChildKeys_ThrowsSecurityException`: `"Gateway:Telemetry:Otlp:Headers:Authorization": "Bearer xyz"` throws `InvalidOperationException` containing `SEC-OT06-14`.
- `OtlpHeaders_ThrowsEvenWhenLegacyConfigAllowed`: When both `Serilog` section and `Gateway:Telemetry:Otlp:Headers` are present with `AUTHERIS_ALLOW_LEGACY_CONFIG=true`, `InvalidOperationException` is thrown immediately.

---

### 3.2 AC-2: Release Pipeline (`docker-publish.yml`)

#### 3.2.1 Workflow Analysis
Inspect [`.github/workflows/docker-publish.yml`](file:///root/autheris/.github/workflows/docker-publish.yml#L111-L176):
1. **Trigger Configuration:**
   - Tag pushes: `push: tags: [ 'v*' ]`
   - Workflow run on main: `workflow_run: workflows: [ "CI Build & Test" ] branches: [ main ]`
   - Manual dispatch: `workflow_dispatch: inputs: tag: ...`
2. **Tag Computation (Step `meta`):**
   ```bash
   IMAGE="ghcr.io/${GITHUB_REPOSITORY,,}"
   TAGS=()
   if [ "$EVENT_NAME" = "workflow_dispatch" ]; then
     if [[ ! "$INPUT_TAG" =~ ^[a-zA-Z0-9_.-]+$ ]]; then
       echo "Error: Invalid docker tag '$INPUT_TAG'" >&2
       exit 1
     fi
     TAGS+=("${IMAGE}:${INPUT_TAG}")
     if [ "$INPUT_TAG" = "latest" ] || [[ "$INPUT_TAG" =~ ^v[0-9] ]]; then
       TAGS+=("${IMAGE}:getting-started")
       if [[ "$INPUT_TAG" =~ ^v[0-9] ]]; then
         TAGS+=("${IMAGE}:latest")
       fi
     fi
   elif [ "$REF_TYPE" = "tag" ]; then
     TAGS+=("${IMAGE}:${REF_NAME}")
     TAGS+=("${IMAGE}:latest")
     TAGS+=("${IMAGE}:getting-started")
   else
     # Push on main (workflow_run / push on main)
     # SC-01: No latest on main branch pushes
     TAGS+=("${IMAGE}:main")
     TAGS+=("${IMAGE}:sha-${COMMIT_SHA}")
   fi
   ```
3. **Cosign Signing & Verification:**
   - Digest signing:
     ```bash
     cosign sign --yes "${IMAGE,,}@${DIGEST}"
     ```
   - Self-check verification regex:
     ```bash
     cosign verify "${IMAGE,,}@${DIGEST}" \
       --certificate-oidc-issuer https://token.actions.githubusercontent.com \
       --certificate-identity-regexp '^https://github\.com/themulle/autheris/\.github/workflows/docker-publish\.yml@refs/(heads/main|tags/v.+)$'
     ```
   The regular expression `refs/(heads/main|tags/v.+)` accurately and securely permits both main branch commits (`refs/heads/main`) and semantic release tags (`refs/tags/v1.2.6`).
4. **Supply-Chain Attestations:**
   `provenance: mode=max` and `sbom: true` remain enabled, generating signed SLSA Level 3 attestations.

---

### 3.3 AC-3: Diagnostic Clarity in Zero-Trust Policy Decision Point

#### 3.3.1 Current State & Flaw
In [`src/Autheris.Application/Policy/TableAccessPolicy.cs`](file:///root/autheris/src/Autheris.Application/Policy/TableAccessPolicy.cs#L266-L287):
```csharp
if (await IsRebacDeniedAsync(query, ct).ConfigureAwait(false))
{
    return TableAccessDecision.Denied(table, RebacDeniedReason);
}

var consentBypassed = _options?.IsConsentBypassed == true;
AccessProfile? profile;
...
TableAccessDecision decision = consentBypassed
    ? TableAccessDecision.Allowed(...)
    : await ResolveConsentAsync(query, ct).ConfigureAwait(false);
```
If ReBAC allows access, but `ResolveConsentAsync` returns `IsAllowed = false`, the decision's `DeniedReasons` contains only the raw consent resolution message (e.g., `"Zero Trust: No active ALLOW consent granted to subject."`).
Security operators inspecting access denial logs cannot determine whether the caller passed or failed the ReBAC relationship gate.

#### 3.3.2 Target Design
1. Evaluate whether ReBAC was evaluated and allowed:
   ```csharp
   var rebacEnforced = query.Rebac switch
   {
       RebacEnforcement.WhenEnabled => _rebacEvaluator is { IsEnabled: true },
       _ => RebacTableGate.IsEnforcedOnQueryPaths(_options)
   };

   bool rebacAllowed = false;
   if (_options?.IsRebacBypassed != true && _rebacEvaluator is { IsEnabled: true })
   {
       rebacAllowed = await RebacTableGate.IsAllowedAsync(_rebacEvaluator, query.Tenant, query.UserSid, table, ct).ConfigureAwait(false);
       if (rebacEnforced && !rebacAllowed)
       {
           return TableAccessDecision.Denied(table, RebacDeniedReason);
       }
   }
   ```
2. When consent resolution completes:
   If `!decision.IsAllowed`:
   If `rebacAllowed` is `true`:
   Append the diagnostic attribution note to `decision.DeniedReasons`:
   ```csharp
   if (!decision.IsAllowed)
   {
       if (rebacAllowed)
       {
           const string diagnosticNote = "(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)";
           var reasons = new List<string>(decision.DeniedReasons);
           if (!reasons.Contains(diagnosticNote))
           {
               reasons.Add(diagnosticNote);
           }
           decision = decision with { DeniedReasons = reasons };
       }
       return decision;
   }
   ```
3. Existing denial reason codes from `ConsentResolutionService` are strictly preserved.
4. If ReBAC was denied, execution terminates at line 268 before consent resolution is invoked.
5. If ReBAC was not active/evaluated (`_rebacEvaluator` null or disabled), `rebacAllowed` is `false`, and no diagnostic note is appended.

#### 3.3.3 Unit Test Specifications (`TableAccessPolicyArch1Tests.cs`)
File: `tests/Autheris.Tests.Unit/Security/TableAccessPolicyArch1Tests.cs`
- `Policy_RebacAllowed_ConsentDenied_IncludesDiagnosticAttribution`:
  - Principal with valid ReBAC relationship (`RebacCheckResult.Allowed`).
  - No active consents in `CONSENTS` repository.
  - Assert `decision.IsAllowed == false`.
  - Assert `decision.DeniedReasons` contains `"(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)"`.
  - Assert `decision.DeniedReasons` contains the original consent denial reason (e.g. `"no consent"`).
- `Policy_RebacDenied_ReturnsImmediately_WithoutConsentEvaluation`:
  - Principal denied by ReBAC (`RebacCheckResult.Denied`).
  - Assert `decision.IsAllowed == false`.
  - Assert `decision.DeniedReasons.Single() == TableAccessPolicy.RebacDeniedReason`.
- `Policy_RebacDisabled_ConsentDenied_DoesNotIncludeRebacAttribution`:
  - `NullRebacEvaluator.Instance` (`IsEnabled = false`).
  - Consent denied.
  - Assert `decision.DeniedReasons` does NOT contain the diagnostic note.

---

### 3.4 AC-4: Governance Database Schema Evolution & `--migrate-governance`

#### 3.4.1 Schema Evolution Verification (80 -> 83 Elements)
The database schema evolved from 80 elements to 83 verified elements:
1. Table `CATALOG_STATE`:
   - Columns: `id` (PK, singleton constraint `id = 1`), `structure_version` (INT/BIGINT), `content_version` (INT/BIGINT), `updated_at` (TEXT/TIMESTAMPTZ/DATETIMEOFFSET).
   - Seed row: `(1, 1, 1, '1970-01-01T00:00:00Z')`.
2. Index `IX_TABLE_RELATIONS_PARENT`:
   - Target: `TABLE_RELATIONS(parent_table_id)`.
3. Index `IX_TABLE_RELATIONS_CHILD`:
   - Target: `TABLE_RELATIONS(child_table_id)`.

#### 3.4.2 CLI Command Verification
`Autheris.Api --migrate-governance` executes `IGovernanceMigrationRunner.RunAsync()`:
- **SQLite:** `SqliteBaselineSchema.cs` applies `CREATE TABLE IF NOT EXISTS CATALOG_STATE`, `INSERT OR IGNORE INTO CATALOG_STATE`, and `CREATE INDEX IF NOT EXISTS IX_TABLE_RELATIONS_...`.
- **PostgreSQL:** `PostgreSqlBaselineSchema.cs` applies `CREATE TABLE IF NOT EXISTS CATALOG_STATE`, `INSERT ... ON CONFLICT (id) DO NOTHING`, and `CREATE INDEX IF NOT EXISTS ix_table_relations_...`.
- **SQL Server:** `SqlServerBaselineSchema.cs` applies `IF OBJECT_ID(N'dbo.CATALOG_STATE', N'U') IS NULL ...` and conditional index creation.
- **Verification Test:** `GovernanceSchemaShapeSnapshotTests.cs` (all 8 shape tests passing across SQLite, PostgreSQL, and SQL Server).

---

### 3.5 Issue #256: dbt Sources Manifest Ingestion & End-to-End Test Suite

#### 3.5.1 Documentation Updates (`docs/extensions/dbt-integration-guide.md`)
1. **Section 1 (Übersicht & Enterprise-Architektur):**
   Update item 1:
   ```markdown
   1. **Streaming Ingestion von `manifest.json`**: Modelle, Seeds und Quellen (top-level `sources`), Spaltentypen, DSGVO-Tags und Abhängigkeiten fließen direkt in den [`LineageGraphStore`](file:///root/gql/src/Autheris.Application/Interfaces/ILineageGraphStore.cs) und den Governance-Katalog.
   ```
2. **Section 5 (Streaming Ingestion & Performance):**
   Revise the node filtering description and add the sources specification:
   ```markdown
   - **Selektive Node- & Source-Verarbeitung**: Der Parser verarbeitet `model.*`- und `seed.*`-Knoten aus `nodes` sowie alle deklarierten Tabellen unter dem Top-Level-Objekt `sources`. Makros, Tests und Operationen werden im Lesepfad übersprungen.
   - **Mapping von dbt Sources**:
     - `UniqueId`: `source.<project>.<database>.<schema>.<table>`
     - `Materialization`: `"source"`
     - Attribute: Tabellen- und Spaltenbeschreibungen, Typen, Tags (`tags: ["pii", "gdpr"]`) und Meta-Dictionaries (`meta: { "owner": "DataPlatform", "pii": "true" }`).
   ```

#### 3.5.2 End-to-End Unit Test Implementation (`DbtSourcesManifestIngestionTests.cs`)
File: `tests/Autheris.Tests.Unit/Dbt/DbtSourcesManifestIngestionTests.cs`
- Create a test class validating the end-to-end flow:
  1. Construct a valid dbt `manifest.json` with top-level `sources` and zero `nodes`.
  2. Instantiate `DbtMetadataIngestionService` with `InMemoryDbtProposalRepository` and mock `ITableMetadataRepository`.
  3. Ingest the manifest stream with `dryRun: false`.
  4. Assert:
     - `syncResult.Success == true`
     - `syncResult.ParsedModelsCount == 1`
     - `syncResult.GeneratedProposalsCount >= 2`
     - `proposalRepo.GetPendingProposalsAsync(tableId)` contains proposals with matching table identifiers, column names, and rule suggestions.

---

## 4. Threat Modeling & Security Impact (AppSec Gate)

### 4.1 STRIDE Threat Model Evaluation

```
┌──────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                   STRIDE THREAT MODEL EVALUATION                                 │
├───────────────────────┬──────────────┬───────────────────────────────────────────────────────────┤
│ Threat Category       │ Target       │ Mitigation & Fail-Closed Guard                            │
├───────────────────────┼──────────────┼───────────────────────────────────────────────────────────┤
│ Information Disclosure│ Config / Env │ SEC-OT06-14: Gateway:Telemetry:Otlp:Headers remains       │
│                       │              │ strictly forbidden in appsettings.json. Startup aborts.   │
├───────────────────────┼──────────────┼───────────────────────────────────────────────────────────┤
│ Elevation of Privilege│ Zero-Trust   │ ReBAC Allowed status NEVER grants access without active   │
│                       │ PDP          │ ALLOW Consent. Intersection is strictly fail-closed.      │
├───────────────────────┼──────────────┼───────────────────────────────────────────────────────────┤
│ Tampering / Supply-   │ Container    │ Cosign keyless OIDC signing by SHA digest; identity regex │
│ Chain Attack          │ Images       │ verification prevents unauthorized release builds.        │
├───────────────────────┼──────────────┼───────────────────────────────────────────────────────────┤
│ Injection / Untrusted │ dbt Sources  │ DbtArtifactStreamingParser validates identifiers and      │
│ Ingestion             │ Metadata     │ stores proposals in PendingReview status (4-eyes gate).   │
└───────────────────────┴──────────────┴───────────────────────────────────────────────────────────┘
```

### 4.2 Binding Security Conditions (SEC-OPS-257 & SEC-DOC-256)

The following security conditions are mandatory, binding on all implementation and verification phases, and enforceable via automated test suites and CI pipeline gates:

#### 4.2.1 PoC Deployment Blockers Remediation (SEC-OPS-257)

- **`SEC-OPS-257-01` (Unconditional ReBAC Fail-Closed Immediate Termination):**  
  When ReBAC evaluation is active on an enforced path and `RebacTableGate.IsAllowedAsync` evaluates to `false`, `TableAccessPolicy` MUST immediately return `TableAccessDecision.Denied` with reason `RebacDeniedReason`. The policy pipeline MUST NOT proceed to `ResolveActiveAccessProfileAsync`, `ResolveConsentAsync`, ABAC policy evaluation, or custom authorization plugins under any circumstances.

- **`SEC-OPS-257-02` (Zero-Trust Logical AND Intersection Invariance):**  
  A ReBAC status of `Allowed` is strictly a necessary prerequisite and NEVER a sufficient condition to grant table access (`ReBAC Allowed ∧ Consent Allowed ∧ Casbin Allowed`). ReBAC `Allowed` status MUST NOT grant table access, MUST NOT bypass `ResolveConsentAsync`, MUST NOT elevate column access levels (e.g. converting `Deny` or `Mask` to `Clear`), and MUST NOT loosen row-level security predicates. If Consent is Denied, the overall decision MUST remain `IsAllowed = false`.

- **`SEC-OPS-257-03` (Zero Sensitive Context Leakage in Diagnostic Attribution):**  
  The diagnostic attribution note appended to `decision.DeniedReasons` when ReBAC is Allowed and Consent is Denied MUST be a static compile-time constant string:  
  `"(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)"`.  
  It MUST NOT interpolate raw user SIDs, group SIDs, roles, tenant IDs, column names, schema structures, or internal SQL row filter expressions. Furthermore, this diagnostic note MUST ONLY be attached when ReBAC was actively evaluated and confirmed allowed (i.e. evaluator is enabled, non-null, and ReBAC was not bypassed).

- **`SEC-OPS-257-04` (Non-Negotiable SEC-OT06-14 Preservation & Scope Isolation):**  
  Softening in `SerilogSectionGuard` MUST apply strictly and exclusively to the obsolete `"Serilog"` configuration section syntax. The security guard against `"Gateway:Telemetry:Otlp:Headers"` MUST remain unconditionally fatal, throwing `InvalidOperationException` whenever the section exists with a non-empty string or child keys. Under NO circumstances shall `AUTHERIS_ALLOW_LEGACY_CONFIG` or any other environment variable or configuration toggle bypass or soften `SEC-OT06-14` or any other security check.

- **`SEC-OPS-257-05` (Supply-Chain Pipeline Hardening & Main Branch Protection - SC-01):**  
  In `.github/workflows/docker-publish.yml`, the `:latest` and `:getting-started` tags MUST NEVER be published on pushes to the `main` branch. Main branch pushes are strictly restricted to publishing `ghcr.io/themulle/autheris:main` and `ghcr.io/themulle/autheris:sha-${COMMIT_SHA}`. Pushing `:latest` and `:getting-started` is strictly reserved for cryptographic release tag pushes (`refs/tags/v*`). Every container image MUST be signed by immutable SHA256 digest (`${IMAGE,,}@${DIGEST}`) via Cosign keyless OIDC, verified against the strict regex `^https://github\.com/themulle/autheris/\.github/workflows/docker-publish\.yml@refs/(heads/main|tags/v.+)$`, and packaged with SLSA Level 3 provenance (`mode=max`) and SPDX SBOM (`sbom: true`).

- **`SEC-OPS-257-06` (Non-Destructive Schema Evolution & Migration Idempotence):**  
  The schema evolution from 80 to 83 verified elements (`CATALOG_STATE` table and `TABLE_RELATIONS` indexes `IX_TABLE_RELATIONS_PARENT`, `IX_TABLE_RELATIONS_CHILD`) executed via `--migrate-governance` MUST be strictly non-destructive. Existing table metadata, access profiles, and consent records MUST NOT be dropped, truncated, or altered. Baseline scripts across SQLite, PostgreSQL, and SQL Server MUST execute idempotently without throwing errors on pre-existing objects.

#### 4.2.2 dbt Sources Manifest Ingestion (SEC-DOC-256)

- **`SEC-DOC-256-01` (Untrusted Manifest Identifier Sanitization):**  
  Streaming ingestion of top-level `sources` from `manifest.json` MUST validate all database, schema, table, and column names via `DbtIdentifierSanitizer.IsValidIdentifier` before catalog registration. Any identifier containing invalid characters, whitespace, SQL delimiters, quotes, or comment sequences (`--`, `;`, `/*`) MUST be rejected or safely sanitized to prevent SQL injection in catalog search or downstream metadata queries.

- **`SEC-DOC-256-02` (Mandatory Four-Eyes Approval Gate for Ingested Proposals):**  
  All governance, PII, and column masking proposals generated from dbt sources (via source tags or meta attributes) MUST be created in `PendingReview` status in `IDbtProposalRepository`. Automated ingestion MUST NEVER directly apply column masks, RLS rules, or access policy modifications without explicit human administrator approval (Four-Eyes Principle).

- **`SEC-DOC-256-03` (Multi-Tenant Source Isolation):**  
  All parsed sources, column metadata, and generated proposals MUST be strictly scoped to the active `TenantId` of the ingestion context. Cross-tenant metadata leakage or global catalog pollution from uploaded manifest files is strictly prohibited.

- **`SEC-DOC-256-04` (Streaming Parser DoS Defenses):**  
  `DbtArtifactStreamingParser` MUST enforce bounded JSON parsing constraints (`MaxDepth = 64`, `AllowTrailingCommas = true`), limit collection allocations (e.g. `RelationCollector.MaxRelations = 10_000`), and honor `CancellationToken` checks across every streaming loop to prevent CPU or memory exhaustion attacks from oversized or malicious manifests.

---

## 5. Test & Verification Plan

| Category | Test Suite / Target | Scenario | Verification Criteria |
|---|---|---|---|
| **AC-1** | `SerilogSectionGuardTests` | Legacy `"Serilog"` section present, `AUTHERIS_ALLOW_LEGACY_CONFIG=true`. | Emits `DeprecationWarningMessage` to console/writer; does not throw. |
| **AC-1** | `SerilogSectionGuardTests` | Legacy `"Serilog"` section present, `AUTHERIS_ALLOW_LEGACY_CONFIG=false`. | Throws `InvalidOperationException`. |
| **AC-1** | `SerilogSectionGuardTests` | `"Gateway:Telemetry:Otlp:Headers"` present in configuration. | Throws `InvalidOperationException` mentioning `SEC-OT06-14`. |
| **AC-2** | `.github/workflows/docker-publish.yml` | Tag push `v1.2.6`. | Workflow computes `ghcr.io/...:v1.2.6`, `:latest`, `:getting-started`, signs digest, verifies signature. |
| **AC-3** | `TableAccessPolicyArch1Tests` | ReBAC is Allowed; Consent is Denied. | `decision.IsAllowed == false`; `decision.DeniedReasons` contains `"(ReBAC: Allowed, Consent: Denied - Zero-Trust requires an active ALLOW consent for subject or subject's groups)"`. |
| **AC-3** | `TableAccessPolicyArch1Tests` | ReBAC is Denied. | Returns immediately with `RebacDeniedReason`; Consent is not evaluated. |
| **AC-4** | `GovernanceSchemaShapeSnapshotTests` | Baseline migrations across SQLite, PostgreSQL, SQL Server. | All 8 shape tests pass; `CATALOG_STATE` and `TABLE_RELATIONS` indexes verified. |
| **AC-4** | CLI Verification | `Autheris.Api --migrate-governance`. | Executes idempotently without errors across clean and existing databases. |
| **#256** | `DbtSourcesManifestIngestionTests` | Sources-only `manifest.json` streamed to `DbtMetadataIngestionService`. | `ParsedModelsCount == 1`; `materialization == "source"`; PII/RLS proposals generated in repo. |
| **#256** | `docs/extensions/dbt-integration-guide.md` | Manual / doc review. | Sections 1 & 5 accurately document `sources` ingestion, mapping, and governance behavior. |

---

## 6. Implementation Phasing & Work Breakdown Structure (WBS)

- **Phase 1: Product Management & Requirements (Complete ✅)**
  - PRD [`REQ-OPS-257`](2026-10-20-req-issue-257-poc-deployment-blockers.md) approved.
  - PRD [`REQ-DOC-256`](2026-10-20-req-issue-256-dbt-sources-manifest-ingestion.md) approved.
- **Phase 2: Solution Architecture & Detailed Planning (This Document ✅)**
  - Authored `PLAN-OPS-257-256`.
  - Master Backlog [`00-master-plan-overview.md`](00-master-plan-overview.md) updated.
- **Phase 3: Security Expert Review & Sign-Off (Complete ✅)**
  - AppSec audit completed for Issues #257 and #256.
  - Zero-Trust fail-closed invariance and ReBAC/Consent AND-gate integrity verified.
  - Preserved strict crash on `SEC-OT06-14` (`Gateway:Telemetry:Otlp:Headers`) independent of `AUTHERIS_ALLOW_LEGACY_CONFIG`.
  - Supply-chain container pipeline verified against SLSA provenance, SPDX SBOM, Cosign keyless signing, and tag mutation risks (`SC-01`: `:latest` strictly prohibited on `main` branch pushes).
  - Appended binding security conditions `SEC-OPS-257-01..06` and `SEC-DOC-256-01..04`.
  - Approved for Phase 4 C# TDD Developer Execution.
- **Phase 4: C# TDD Developer Execution (Ready to Begin 🚀)**
  - Implement refactored `SerilogSectionGuard.cs` and unit tests in `SerilogSectionGuardTests.cs`.
  - Enhance `TableAccessPolicy.cs` / `TableAccessPolicy.Consent.cs` and add diagnostic tests in `TableAccessPolicyArch1Tests.cs`.
  - Update `.github/workflows/docker-publish.yml` tag computation logic.
  - Update `docs/extensions/dbt-integration-guide.md` Sections 1 and 5.
  - Author `DbtSourcesManifestIngestionTests.cs` in `tests/Autheris.Tests.Unit/Dbt/`.
  - Run full test suite (`dotnet test Autheris.sln`).
- **Phase 5: Code Reviewer & Release Gate**
  - Review all diffs against zero regression, zero compiler warnings.
  - Merge into `main` and trigger release tagging.

---

## 7. Traceability Matrix

| Requirement ID | Plan Section | Source Files Affected | Test Files Affected |
|---|---|---|---|
| `REQ-OPS-257-1.1` | §3.1 | `src/Autheris.Api/Configuration/SerilogSectionGuard.cs` | `tests/Autheris.Tests.Unit/Configuration/SerilogSectionGuardTests.cs` |
| `REQ-OPS-257-1.2` | §3.1 | `src/Autheris.Api/Configuration/SerilogSectionGuard.cs` | `tests/Autheris.Tests.Unit/Configuration/SerilogSectionGuardTests.cs` |
| `REQ-OPS-257-1.3` | §3.1 | `src/Autheris.Api/Configuration/SerilogSectionGuard.cs` | `tests/Autheris.Tests.Unit/Configuration/SerilogSectionGuardTests.cs` |
| `REQ-OPS-257-2.1` | §3.2 | `.github/workflows/docker-publish.yml` | Workflow dry-run / CI dispatch |
| `REQ-OPS-257-2.2` | §3.2 | `.github/workflows/docker-publish.yml` | Workflow dry-run / CI dispatch |
| `REQ-OPS-257-2.3` | §3.2 | `.github/workflows/docker-publish.yml` | Cosign verify step |
| `REQ-OPS-257-3.1` | §3.3 | `src/Autheris.Application/Policy/TableAccessPolicy.cs` | `tests/Autheris.Tests.Unit/Security/TableAccessPolicyArch1Tests.cs` |
| `REQ-OPS-257-3.2` | §3.3 | `src/Autheris.Application/Policy/TableAccessPolicy.cs` | `tests/Autheris.Tests.Unit/Security/TableAccessPolicyArch1Tests.cs` |
| `REQ-OPS-257-4.1` | §3.4 | `src/Autheris.Infrastructure/Persistence/Migrations/*` | `tests/Autheris.Tests.Unit/Governance/GovernanceSchemaShapeSnapshotTests.cs` |
| `REQ-OPS-257-4.2` | §3.4 | `src/Autheris.Api/Program.cs` | `tests/Autheris.Tests.Unit/Governance/GovernanceSchemaShapeSnapshotTests.cs` |
| `REQ-DOC-256-1`   | §3.5 | `docs/extensions/dbt-integration-guide.md` | Documentation review |
| `REQ-DOC-256-2`   | §3.5 | N/A (Parser already implements sources) | `tests/Autheris.Tests.Unit/Dbt/DbtSourcesManifestIngestionTests.cs` |
