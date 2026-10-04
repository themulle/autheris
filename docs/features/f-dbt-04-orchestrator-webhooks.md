# F-DBT-4: Zero-Touch dbt Cloud & Orchestrator Webhook Integration\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`DbtWebhookReceiver.cs`](file:///root/lis-git/gql/gql_extensions/src/Autheris.Extensions/Dbt/DbtWebhookReceiver.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Aktualisierungen von Modellen und Testläufen erfordern oft fehleranfälliges Polling oder manuelle CLI-Trigger.

## 2. Architektur & Umsetzung
- Webhook-Receiver für dbt Cloud (`job.run.completed`), Apache Airflow und Dagster.
- Timing-sichere HMAC-SHA256 Signaturprüfung und Replay-Schutz über 5-Minuten-Zeitfenster (`FixedTimeEquals`).
- Automatischer Download und sequentielles Streaming neuer Artefakte (`manifest.json`, `run_results.json`).

## 3. Business Value
- Zero-Touch Automatisierung: Sobald die Pipeline im Warehouse fertiggestellt ist, ist das Gateway synchronisiert.\n