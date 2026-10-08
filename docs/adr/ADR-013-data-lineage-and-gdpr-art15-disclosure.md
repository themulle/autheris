# ADR-013: Data Lineage, Downstream Consumer Analyse & DSGVO-Art.-15-Auskunft

## Status
Akzeptiert

## Kontext
Vor Schema-Änderungen (z. B. Umbenennen oder Löschen von Spalten) müssen Data Engineers wissen, welche BI-Dashboards (PowerBI, Tableau), ETL-Pipelines (dbt, Airflow) und externen Services von einer Quelltabelle abhängen. Zudem verlangt Art. 15 Abs. 1 Bst. c DSGVO die lückenlose Auskunft darüber, gegenüber welchen Empfängern und Empfängerkategorien personenbezogene Daten offengelegt wurden.

## Entscheidung
1. **Statische Lineage via DAG BFS (`ILineageGraphStore`)**: Iterative, zyklensichere Breitensuche über registrierte Downstream-Knoten (`Dashboard`, `Pipeline`, `ExternalService`, `Table`) mit Ermittlung der Distanz von der Wurzel.
2. **Laufzeit-Lineage aus Audit-Logs (`IAuditLogRepository`)**: Verknüpfung der statischen Graphen mit tatsächlichen Gateway-Abfragen aus den kryptografischen HMAC-SHA256 Audit-Logs zur Ermittlung aktiver Konsumenten, Häufigkeiten und Zeitfenster.
3. **Automatisierte Risikobewertung (`BreakingChangeRisk`)**:
   - `CRITICAL`: Aktive Dashboards/Pipelines kombiniert mit hohem realem Abfragevolumen.
   - `HIGH`: Abhängige Pipelines oder Dashboards ohne hohes Abfragevolumen.
   - `MEDIUM`: Externe Services oder geringe Abfrageaktivität.
   - `LOW`: Keine aktiven Konsumenten oder Abhängigkeiten.
4. **Zero-Trust E-Mail-Maskierung**: Kontaktadressen abhängiger Systeme (`OwnerEmail`) werden für nicht-autorisierte Nutzer auf `null` maskiert.
5. **DSGVO Art. 15 Auskunftsbericht**: Strukturierter Report (`getGdprDataDisclosureReport`) über Empfänger, Empfängerkategorien, abgefragte Spalten, Maskierungsregeln und Zwecke über bis zu 365 Tage.

## Konsequenzen
### Positiv
- Schutz vor unbemerkten Breaking Changes in produktiven BI-Dashboards und Datenpipelines.
- Vollständige rechtliche Konformität für DSGVO-Auskunftsbegehren auf Knopfdruck.
- Strikte Wahrung der Vertraulichkeit von Data Owner Kontaktdaten (Zero-Trust Spaltenautorisierung).

### Negativ / Risiken
- Große Graphstrukturen oder Millionen Audit-Einträge können Abfragezeiten verlängern. Gegenmaßnahme: Iterative BFS mit Queue/Visited-Set, indizierte SQLite-Spalten (`idx_audit_target_table`, `idx_audit_actor_sid`) und konfigurierbare Zeitfenster.
