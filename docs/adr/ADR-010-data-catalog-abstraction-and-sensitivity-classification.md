# ADR-010: Multi-Catalog Provider-Abstraktion & automatisierte Sensitivitätsklassifizierung

## Status
Akzeptiert

## Kontext
Unternehmen verwalten Metadaten, Schemabeschreibungen und Sensitivitäts-Klassifizierungen (PII, DSGVO Art. 9) zunehmend in spezialisierten Data-Governance- und Datenkatalog-Lösungen wie Microsoft Purview, Collibra, Alation oder OpenMetadata. Die manuelle Pflege von Tabellenmetadaten und Schutzklassen direkt im Gateway führt zu Redundanzen, Drift und erhöhtem Pflegeaufwand für Data Stewards.

## Entscheidung
Wir etablieren eine generische Multi-Catalog-Abstraktion:
1. **Einheitliche Schnittstelle `IDataCatalogClient`**: Adapter-Implementierungen für Microsoft Purview (Apache Atlas REST), Collibra (Core REST API v2), Alation (API v2) und OpenMetadata.
2. **Dual-Mode-Architektur**:
   - `Mirror`: Synchronisiert Metadaten periodisch oder per Mutation `syncDataCatalog` in den lokalen SQLite-Store.
   - `Reference`: Ermöglicht On-Demand-Lookups ohne persistente Duplikation.
3. **Automatisierte DSGVO-Art.-9-Klassifizierung**: Erkennung besonderer Kategorien personenbezogener Daten (Gesundheit, Biometrie, Genetik, Religion, Sexualleben) erzwingt automatisch `Sensitivity = "HIGH"`, Vier-Augen-Freigabe (`RequiresFourEyes = true`) und Maskierung (`REDACT` mit `[REDACTED-GDPR-ART9]`).
4. **PII-Tag-Mapping**: Dynamische Abbildung von Katalog-Tags auf Maskierungsregeln (`TagToMaskingRuleMap`).
5. **Zero-Trust-Invariante**: Externe Kataloge liefern Metadaten und Schutzvorschläge, erteilen aber **keine eigenständigen Zugriffsrechte**. Zugriffsberechtigungen verbleiben strikt unter Hoheit der Data Owner Consents im Gateway.

## Konsequenzen
### Positiv
- Nahtlose Integration in bestehende Enterprise-Data-Governance-Ökosysteme.
- Keine redundante Metadaten- und Klassifizierungspflege.
- Garantierte Einhaltung der strengen Schutzanforderungen für DSGVO Art. 9 Daten.

### Negativ / Risiken
- Abhängigkeit von Verfügbarkeit und API-Ratenbegrenzungen externer Kataloge. Gegenmaßnahme: Lokaler Cache, Resilienz-Puffer, Mirror-Modus.
