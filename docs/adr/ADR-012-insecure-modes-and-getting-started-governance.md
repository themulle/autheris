# ADR-012: Explizite unsichere Modi ("Insecure Modes") für Developer Onboarding & Fremdsysteme

## Status
Akzeptiert

## Kontext
Strikte Zero-Trust- und Defense-in-Depth-Vorgaben (mTLS, HMAC-Signaturen, striktes Kerberos, Rate Limiting, Query-Depth-Limits) erschweren schnelle PoCs, Integrationstests und das Onboarding unvollständiger Drittsysteme oder lokaler Webhook-Tunnel (z. B. ngrok zu ServiceNow/Jira). Entwickler neigen dazu, Sicherheitsprüfungen im Code auszukommentieren, wenn die Konfiguration keine kontrollierte Lockerung ermöglicht.

## Entscheidung
Wir führen ein explizites, semantisch präfixiertes Konfigurationsschema ein:
1. **Risikobasierte Präfixe**:
   - `warn_`: Lockert Schwellenwerte und Netzwerkschutz (z. B. `warn_allow_all_cors_origins`, `warn_disable_rate_limiting`, `warn_bypass_query_cost_limits`).
   - `danger_`: Deaktiviert zentrale Sicherheitsgarantien (z. B. `danger_allow_anonymous_queries`, `danger_bypass_authorization`, `danger_bypass_webhook_signature_validation`, `danger_allow_untrusted_certificates`).
2. **Sicherheits-Gegenmaßnahmen & Invarianten**:
   - Alle unsicheren Optionen stehen standardmäßig auf `false` (Fail-Closed).
   - Bei aktiviertem `danger_`-Flag emittiert das Gateway beim Start und zur Laufzeit auffällige `CRITICAL SECURITY ALERT`-Logeinträge.
   - CI/CD-Pipelines verbieten `danger_* = true` in Produktionskonfigurationen.

## Konsequenzen
### Positiv
- Schnelleres Prototyping und reibungsloses Onboarding externer Partner- und Webhook-Systeme.
- Transparente Risikodarstellung in Konfigurationsdateien ohne versteckte Bypässe.
- Verhindert unkontrolliertes Modifizieren von Quellcode für Testzwecke.

### Negativ / Risiken
- Risiko versehentlicher Aktivierung in Staging oder Produktion. Gegenmaßnahme: Auffällige Logging-Warnungen, automatisierte CI-Audits und Runbook-Checklisten.

## Nachtrag 2026-10-05: Vereinheitlichung (Phase 4)
Alle `warn_`/`danger_`-Schalter existieren ausschließlich in `Gateway:Insecure`. Die domain-lokalen Duplikate (z. B. `Mcp:danger_bypass_mcp_auth`, `GraphQL:warn_allow_all_cors_origins`, `WebSql:warn_allow_dml`) wurden entfernt. Ein noch gesetzter alter Schlüssel mit Wert `true` führt zu einem Startfehler mit Hinweis auf den neuen Schlüssel (`LegacySwitchGuard`). Details und Migration: [developer-guide.md](../developer-guide.md#3-developer-options-gatewaydev).
