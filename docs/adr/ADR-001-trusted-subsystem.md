# ADR-001: Virtualisierte Berechtigungen via Trusted Subsystem statt individueller Datenbank-Logins

## Status
Akzeptiert

## Kontext
Das Gateway agiert als zentraler Zugangsknoten für Unternehmensdatenbanken (PostgreSQL, SQL Server, Databricks) mit bis zu 50.000 Tabellen. Zur Laufzeit greifen tausende Benutzer über Single-Sign-On (Active Directory) zu. Die Verwaltung individueller Datenbank-Logins und RBAC-Rollen in den Quelldatenbanken skaliert organisatorisch und technisch nicht (Connection-Pool-Erschöpfung, hohe Verwaltungskosten, mangelnde Flexibilität).

## Entscheidung
Wir etablieren das **Trusted Subsystem Pattern**:
1. Das GraphQL-Gateway verbindet sich mit den Quelldatenbanken über ein dediziertes, technisches Dienstkonto mit striktem Least-Privilege (ausschließlich Lesezugriff `SELECT` auf katalogisierte Tabellen).
2. Alle Berechtigungsprüfungen (Data-Owner-Consent nach F-CONS-07, Spaltenmaskierung, Zeilenfilter) werden virtualisiert im Gateway und der Governance-DB vollzogen, **bevor** eine Abfrage die Quelle erreicht.
3. Zur Sicherung der Attribution wird der Endnutzer-Kontext (SID) pro Verbindung session-spezifisch an die Quelle weitergereicht (SQL Server `SESSION_CONTEXT`, PostgreSQL `set_config`).

## Konsequenzen
### Positiv
- Vollständige Erhaltung des Connection-Poolings für maximale Abfrage-Performance.
- Zentrale, feingranulare Governance (Data-Owner-Consents) ohne Eingriffe in Quellsysteme.
- Keine Passwort- oder Login-Verwaltung für Fachnutzer auf DB-Ebene.

### Negativ / Risiken
- Größerer Blast-Radius bei einer hypothetischen Kompromittierung des Dienstkontos. Gegenmaßnahme: Physische Isolation, Read-Only-Rechte, WORM-Audit-Logging und STRIDE Threat Model (DOK-07).
