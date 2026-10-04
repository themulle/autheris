# F-CDC-02: Native MSSQL Change Tracking Ingestion Provider\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** [`MssqlChangeTrackingIngestionService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Streaming/MssqlChangeTrackingIngestionService.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Klassisches CDC via Debezium/Kafka verlangt hohen Betriebsaufwand ("The Kafka Barrier"). Viele Enterprise-DBAs sperren sich gegen log-basierte Agenten.

## 2. Architektur & Umsetzung
- Direkte Integration über natives MSSQL `CHANGETABLE(CHANGES ...)`.
- Versionsbasiertes Polling (`CHANGE_TRACKING_CURRENT_VERSION()`) ohne Kafka oder Extra-Storage.
- Dynamische In-Stream Filterung durch Casbin ABAC und RLS im Hot Path vor WebSocket/SSE Egress.

## 3. Business Value
- Zero-Infrastructure Realtime CDC bei minimaler Datenbanklast und null Kafka-Betriebskosten.\n