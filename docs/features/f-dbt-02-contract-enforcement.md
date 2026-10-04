# F-DBT-2: dbt Model Contract Enforcement & Breaking-Change CI Gate\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`DbtMetadataIngestionService.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/Dbt/DbtMetadataIngestionService.cs), `POST /api/extensions/dbt/validate-contract`\n\n---\n\n## 1. Übersicht & Problemstellung
Wenn Data Engineers im dbt-Projekt Spalten umbenennen, löschen oder Datentypen anpassen, brechen Downstream-Konsumenten oft erst zur Laufzeit in Produktion.

## 2. Architektur & Umsetzung
- Validiert `manifest.json` gegen das aktive Schema und registrierte Client-Queries.
- Erkennt Schema-Breaks (entfernte Felder, Typänderungen, fehlende Pflichtfelder) vor dem Merge im PR-Workflow.
- Bietet automatisierte CI/CD Gate-Prüfung über dedizierten Endpoint.

## 3. Business Value
- Beseitigt Breaking Changes vor dem Deployment in die Produktion.
- Schützt API-Konsumenten vor unangekündigten Modellmodifikationen.\n