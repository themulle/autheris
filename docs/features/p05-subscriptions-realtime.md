# P5: Subscriptions, Realtime Events & In-Stream RLS\n\n**Status:** [Done] (100% GA – Core Foundation)  \n**Komponenten:** [`Subscription.cs`](file:///root/lis-git/gql/gql/src/Autheris.GraphQL/Subscriptions/Subscription.cs), [`StreamRlsPolicyEnforcer.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Streaming/Services/StreamRlsPolicyEnforcer.cs), [`DebeziumCdcParser.cs`](file:///root/lis-git/gql/gql/src/Autheris.Infrastructure/Streaming/DebeziumCdcParser.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Echtzeit-Subscriptions im Enterprise-Umfeld dürfen keine unautorisierten Daten leaken.

## 2. Architektur & Umsetzung
- WebSocket (`graphql-transport-ws`) und SSE Transport.
- In-Stream Auswertung von Casbin ABAC und dynamisches Column Masking pro Subscriber.
- Integration mit Debezium/Kafka Event Streams.

## 3. Business Value
- Revisionssichere Echtzeit-Datenströme mit vollständiger Mandanten- und Zugriffstrennung.\n