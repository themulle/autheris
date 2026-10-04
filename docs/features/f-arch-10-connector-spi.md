# F-ARCH-10: Standardisiertes Connector-SPI nach Trino-Muster\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** [`IAutherisConnector.cs`](file:///root/lis-git/gql/gql/src/Autheris.Domain/Connectors/IAutherisConnector.cs), [`AutherisConnectorRegistry.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Connectors/AutherisConnectorRegistry.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Proprietäre Konnektor-Schnittstellen erschweren die Anbindung neuer Datenquellen und führen zu Sicherheitsrisiken bei Credential-Handhabung.

## 2. Architektur & Umsetzung
- Standardisiertes Interface nach Trino-Vorbild: Getrennte Metadaten-Inspektion, Table-Handle-Auflösung und Split-Streaming.
- Zero-Trust Delegating Decorators zum Schutz vor unberechtigter Daten-Exfiltration.

## 3. Business Value
- Zukunftssichere, modulare Architektur zur schnellen Integration neuer Enterprise-Datenquellen.\n