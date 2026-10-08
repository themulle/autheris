# ADR-005: Revisionssichere Audit-Protokollierung mit HMAC-SHA256 Hash-Verkettung (Dual Storage)

## Status
Akzeptiert (Erweitert auf Keyed HMAC-SHA256)

## Kontext
Compliance-Vorgaben (GoBD, SOX, DSGVO) fordern den manipulationssicheren Nachweis aller Berechtigungsentscheidungen, Widerrufe und Zugriffe auf sensible Daten. Logs in rein textuellen Dateien oder ungeschützten relationalen Tabellen können nachträglich modifiziert oder gelöscht werden. Ein rein ungesalzener/unverschlüsselter Hash (wie unkeyed SHA-256) könnte bei direktem Schreibzugriff auf die Datenbank durch Angreifer komplett neu berechnet werden.

## Entscheidung
Wir setzen ein **Dual-Storage-Modell mit kryptografischer HMAC-SHA256 Hash-Kette** um:
1. **Ebene 1 (Transaktional in Governance-DB):** Tabelle `AUDIT_LOG_ENTRIES` speichert Events unveränderlich. Jeder Eintrag enthält den Hash des vorherigen Eintrags (`prev_hash`) und berechnet seinen eigenen Hash mittels keyed HMAC-SHA256:
   `entry_hash = HMACSHA256(auditSecretKey, prev_hash | occurred_at | event_type | actor_sid | target_table | decision | trace_id | details_json)`.
   Der HMAC-Schlüssel wird sicher aus dem Key Vault (`Gateway:Audit:HmacSecret` bzw. `IKeyVaultSecretProvider`) bezogen.
   Die Integritätsprüfung validiert die Kette kontinuierlich unter Verwendung von timing-sicherem `CryptographicOperations.FixedTimeEquals`.
2. **Ebene 2 (Search & SIEM):** Über das Outbox-Pattern werden Events asynchron nach Elasticsearch / SIEM gestreamt.
3. **DSGVO-Konformität:** Audit-Logs enthalten nur pseudonyme SIDs (`actor_sid`). Die Personenauflösung liegt in einer getrennten, löschbaren Tabelle (`AUDIT_ACTOR_DIRECTORY`).

## Konsequenzen
### Positiv
- Vollständige Revisions- und Manipulationssicherheit: Selbst bei unbefugtem direktem Schreibzugriff auf die Governance-DB kann ein Angreifer ohne den Key-Vault-Schlüssel die Kette nicht fälschen.
- Timing-sichere Validierung verhindert Side-Channel-Angriffe beim Integritätsabgleich.
- DSGVO-Recht auf Vergessenwerden erfüllbar, ohne die Audit-Integrität zu zerstören.
- Schnelle Analyse in Kibana/Elasticsearch bei gleichzeitiger rechtssicherer Verankerung in der DB.

### Negativ
- Sequenzielle Abhängigkeit bei der Hash-Berechnung (wird durch Transaktions-Sperren im Schreibpfad koordiniert).
- Schlüssel-Management erforderlich (Rotation und sichere Hinterlegung des Audit-HMAC-Keys).
