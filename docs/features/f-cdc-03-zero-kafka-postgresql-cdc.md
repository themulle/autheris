# F-CDC-03: Zero-Kafka PostgreSQL CDC via Logical Streaming Replication

**Status:** **100% (GA) ✅ (Implementiert & Security-Audited 2026-10-02)**  
**Komponenten:** [`PostgreSqlLogicalReplicationService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Streaming/PostgreSqlLogicalReplicationService.cs), [`WalMessageDecoder.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Streaming/WalMessageDecoder.cs), [`PostgreSqlCdcOptions.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Options/GatewayOptions.cs)  
**Referenzen:** [`implementation-plan-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/architecture/implementation-plan-welle-1-2026-10-02.md), [`security-review-welle-1-2026-10-02.md`](file:///root/lis-git/gql/gql/docs/threat-model/security-review-welle-1-2026-10-02.md)

---

## 1. Übersicht & Problemstellung
Echtzeit-Streaming über Apache Kafka und Debezium scheitert in vielen Abteilungen an den hohen Infrastruktur- und Betriebskosten ("The Kafka Barrier"). Bisher deckte das Gateway diesen Zero-Kafka-Bypass nur für MSSQL Change Tracking (`F-CDC-02`) ab.

## 2. Architektur & Umsetzung
- **Direkter PostgreSQL Logical Replication Client:** Das Gateway dockt direkt an den PostgreSQL WAL-Stream über das native `pgoutput`-Streaming-Protokoll an (`Npgsql.Replication.PgOutput`).
- **Autonomes Slot-Management & LSN Acknowledgment:** Laufende Bestätigung der verarbeiteten Log Sequence Numbers (LSN) zur Vermeidung von WAL-Akkumulation auf der Datenbank.
- **WAL Lag Guard & DoS-Schutz:** Automatische Überwachung von `CurrentWalLagBytes` gegen `MaxLagBytes` (Standard: 1 GB) mit Drosselung bei anhaltendem Konsumentenverzug.
- **In-Stream Row-Level Security (RLS) & Mandantentrennung:** Der `WalMessageDecoder` decodiert Tupel-Änderungen (`INSERT`, `UPDATE`, `DELETE`) und filtert diese über den `StreamRlsPolicyEnforcer` mandantenscharf, bevor Events an GraphQL-Subscriptions oder SSE gestreamt werden.

## 3. Konfigurationsbeispiel (`appsettings.json`)
```json
{
  "Gateway": {
    "Cdc": {
      "PostgreSql": {
        "Enabled": true,
        "ConnectionString": "Host=localhost;Database=appdb;Username=repuser;Password=***",
        "SlotName": "autheris_gateway_cdc_slot",
        "PublicationName": "gql_cdc_pub",
        "MaxLagBytes": 1073741824,
        "TenantIdColumn": "tenant_id"
      }
    }
  }
}
```

## 4. Business Value & TCO-Vorteil
- **Kein Apache Kafka / Debezium / ZooKeeper-Cluster:** Spart bis zu 80 % der Infrastruktur- und Betriebskosten bei CDC-Initiativen.
- **Echtzeit-Fähigkeit für PostgreSQL & Supabase:** Sub-Sekunden-Latenz von der Datenbankänderung bis zur GraphQL-Subscription.
