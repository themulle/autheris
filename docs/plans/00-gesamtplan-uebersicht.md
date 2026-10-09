# Gesamtübersicht der Architektur- & Implementierungspläne

**Stand:** 09.10.2026 · **Zweig:** `feat/ast-target-dialect-generator`  
**Ziel:** Strukturierte Übersicht und Ausführungsgraph aller Anforderungen, PoC-Befunde, Audit-Befunde und Security-Reviews, aufgeteilt in thematisch fokussierte Architekturdokumente in `docs/plans/`.

---

## 1. Thematische Aufteilung & Umsetzungsstatus

| Plan | Thema | Behandelte Befunde & Anforderungen | Status |
|---|---|---|---|
| **[Plan 1: Governance-Import & dbt](plan-governance-import-dbt.md)** | dbt-Integration, Metadaten-Streaming, Bereinigung, `replace`-Modus, typgerechtes `REDACT` & Katalog | **B-01, B-02, B-03, B-04, B-05, B-06, R-50, R-51** | Implementiert & Verifiziert ✅ |
| **[Plan 2: Erweiterte Maskierungsregeln](plan-erweiterte-maskierungsregeln.md)** | Geodaten-Schutz (`GEO_JITTER`), Teilmaskierung (`PARTIAL_MASK`), Tokenisierung & Vokabular | **R-53, R-25, B-06** | Detaillierte Architektur & Spezifikation bereit ⏳ |
| **[Plan 3: Klartext je Person & Profile](plan-klartext-ausnahmen-zugriffsprofile.md)** | Deklarative Zugriffsprofile, Klartext-Ausnahmen (david vs philipp), Bulk-Consent-API & Audit | **R-52, R-50, R-20** | Detaillierte Architektur & DDL/API-Design bereit ⏳ |
| **[Plan 4: Audit-Architektur-Härtung](plan-audit-architektur-haertung.md)** | Lückenloses Zugriffs-Audit, kryptografische Anker, Transaktionsintegrität & Resilienz | **AU-01 bis AU-19** | Detaillierter Architekturplan & Phasenplan bereit ⏳ |
| **[Plan 5: Security Review Phase 3 & CI/CD](plan-security-review-phase3.md)** | Security-Befunde Niedrig & Härtung, Release-Cross-Compile Fix (NU1004), Teststabilität | **SG-22 bis SG-39, Build v1.1.3** | Implementiert & 100% Tests Grün ✅ |

---

## 2. Abhängigkeits- & Ausführungsgraph

```mermaid
flowchart TD
    subgraph Foundation["Fundament & Infrastruktur (Abgeschlossen ✅)"]
        CICD["CI/CD & Release Fix<br/>NU1004 -p:RestoreLockedMode=false<br/>Integrationstest ENOMEM Isolation"]
        SG["Security Review Phase 3<br/>SG-22 bis SG-39 Härtung"]
        DBT["dbt & Governance-Import (Plan 1)<br/>B-01 bis B-06 & R-50/51"]
    end

    subgraph SecurityAudit["Audit & Transaktionsintegrität (Plan 4)"]
        AU_Core["AU-01..03: KMS Anker & Prod Fail-Closed"]
        AU_Tx["AU-04: Transaktionale Kopplung (Enrolled Tx)"]
        AU_Resilience["AU-05: Dead-Letter Queue & Probe"]
    end

    subgraph MaskingEngine["Datenschutz & Maskierung (Plan 2)"]
        GEO["GEO_JITTER (round & noise)"]
        PARTIAL["PARTIAL_MASK (Unicode Rune Safe)"]
        TOKEN["TOKENIZATION Entwurf"]
    end

    subgraph AccessGovernance["Zugriffsprofile & Ausnahmen (Plan 3)"]
        PROFILES["Deklarative Zugriffsprofile<br/>(ACCESS_PROFILES Schema)"]
        CONSENT_API["POST /api/v1/consents/bulk"]
        DAVID_PHILIPP["david (Unmasked) vs. philipp (Default)"]
    end

    CICD --> DBT
    SG --> AU_Core
    DBT --> PROFILES
    AU_Tx --> PROFILES
    AU_Tx --> CONSENT_API
    GEO --> DAVID_PHILIPP
    PARTIAL --> DAVID_PHILIPP
    PROFILES --> DAVID_PHILIPP
```

---

## 3. Vollständige Mindmap aller Anforderungen & Befunde

```mermaid
mindmap
  root((Autheris Governance & Security))
    dbt & Metadaten (Plan 1)
      B-01: Validierung none/null/false
      B-02: replace-Modus & Zaehler
      B-03: Harmonisierte Sensitivitaet
      B-04: source-Beziehungen Parser
      B-05: DATA_OWNERS Deduplizierung
      B-06: Typgerechtes REDACT
      R-50: Virtuelle Filter & Profile
      R-51: Routinen-Warnungen
    Maskierung & Datenschutz (Plan 2)
      R-53: GEO_JITTER Geodaten
      R-53: PARTIAL_MASK Kennungen
      R-53: TOKENIZATION Entwurf
      Vokabular-Harmonisierung
      Dialekt-SQL T-SQL / PG / DuckDB / SL
      In-Memory C# Rune Provider
    Klartext & Profile (Plan 3)
      R-52: david Klartext-Ausnahme
      R-52: philipp Maskiert
      POST /api/v1/consents/bulk
      ACCESS_PROFILES DDL
      TableAccessPolicy Integration
      CONSENT_GRANTED Audit-Kopplung
    Audit & Compliance (Plan 4)
      AU-01..03: Anker-Persistenz & KMS
      AU-04: Transaktionale Kopplung
      AU-05: Resiliente Tier-B Pipeline
      AU-06..07: Literal-Redaktion & Laengen
      AU-08..19: Anbieter-Harmonisierung
    Security & CI/CD (Plan 5)
      SG-22..39: Phase 3 Haertung
      v1.1.3: Release-Workflow NU1004 Fix
      Integrationstest ENOMEM Isolation
```

---

## 4. Übergreifende Sicherheits- & Governance-Prinzipien (Security Expert Review)

Alle fünf Fachpläne unterliegen sechs nicht verhandelbaren Sicherheits-Invarianten:

```mermaid
flowchart LR
    Inv1["1. Fail-Closed Default<br/>(Im Zweifel verweigern)"]
    Inv2["2. Krypto-Trennung<br/>(HMAC Kette vs. KMS Signer)"]
    Inv3["3. AST-Sandbox<br/>(Kein Roh-SQL in Filtern)"]
    Inv4["4. Vier-Augen-Prinzip<br/>(Kein heimliches Lockern)"]
    Inv5["5. PII-Freies WORM<br/>(DSGVO Art. 17 Schutz)"]
    Inv6["6. Transaktions-Atomizitaet<br/>(Kein Phantom-Audit)"]

    Inv1 --- Inv2 --- Inv3
    Inv4 --- Inv5 --- Inv6
```

1. **Fail-Closed als universelles Grundprinzip:**
   - Bei unvollständiger Konfiguration, fehlenden Richtlinien, unbekannten Maskierungstypen oder gestörter Audit-Pipeline verweigert das Gateway den Zugriff (`403 Forbidden` bzw. `503 Service Unavailable`).
   - Ein Fallback auf unmaskierte Klartextdaten oder das stillschweigende Übergehen von Schutzregeln ist architekturell ausgeschlossen.
2. **Kryptografische Domänentrennung:**
   - Symmetrischer Hochdurchsatz-Schlüssel (HMAC-SHA256) für die In-Process-Verkettung.
   - Asymmetrische Hardwareschlüssel (KMS/HSM) für WORM-Anker-Zertifikate, die kein interner DBA manipulieren kann.
3. **AST-Validierung dynamischer Ausdrücke:**
   - Virtuelle Filter und Profil-Prädikate werden vor der Persistenz als syntaktische Bäume (AST) gegen eine strikte Whitelist geprüft (nur spaltenbezogene Boolesche Operatoren, keine DDL/DML, keine externen Funktionen).
4. **Schutz vor heimlicher Schutzbedarfs-Lockerung (Ratsche & Vier-Augen):**
   - Das Entfernen oder Abschwächen von Maskierungsregeln im dbt-Import oder das Vergeben von `Unmasked`-Profilen erfordert zwingend eine Begründung und die Freigabe eines zweiten Sicherheitsbeauftragten (SG-22).
5. **Datenschutzkonformes WORM-Audit (DSGVO Art. 17 vs. Unveränderlichkeit):**
   - Personenbezogene Literale werden vor dem Schreiben in unveränderliche Audit-Trails anonymisiert (`@p_redacted`), während die Integrität über deterministische Hashes gewahrt bleibt.
6. **Transaktionale Koppelung von Daten & Audit:**
   - Audit-Ereignisse für Berechtigungen und Profile werden in derselben DB-Transaktion persistiert. Ein Rollback verhindert Phantom-Einträge im Audit-Log.

---

## 5. Quelltexte und Referenzdokumente

- [Plan 1: Governance-Import & dbt](plan-governance-import-dbt.md)
- [Plan 2: Erweiterte Maskierungsregeln](plan-erweiterte-maskierungsregeln.md)
- [Plan 3: Klartext je Person & Profile](plan-klartext-ausnahmen-zugriffsprofile.md)
- [Plan 4: Audit-Architektur-Härtung](plan-audit-architektur-haertung.md)
- [Plan 5: Security Review Phase 3 & CI/CD](plan-security-review-phase3.md)
- [PoC v1.1.5 Befunde (09.10.2026)](2026-10-09-poc-befunde-v1-1-5.md)
- [PoC v1.1.2 Requirements-Bericht](2026-10-09-requirements-poc-v1-1-2.md)
- [Feature-Request Klartext je Person (R-52)](2026-10-09-feature-request-klartext-je-person.md)
- [Feature-Request Maskierungsregeln (R-53)](2026-10-09-feature-request-maskierungsregeln.md)
- [Befunde zur Audit-Architektur (AU-01 bis AU-19)](2026-10-09-audit-architektur-befunde.md)
- [Feature Lückenloses Zugriffs-Audit](2026-10-09-feature-lueckenloses-zugriffs-audit.md)
- [Security Review Gesamtprojekt (SG-01 bis SG-39)](2026-10-09-security-review-gesamtprojekt.md)

