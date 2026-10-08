# ADR-011: Moderne Identitäts-Abstraktion (Entra ID/OIDC) & M2M Service-Principals

## Status
Akzeptiert

## Kontext
Traditionelle On-Premises-Architekturen basieren auf Windows Active Directory und Kerberos. Moderne Cloud- und Hybrid-Landschaften migrieren schrittweise zu OpenID Connect (OIDC) und Microsoft Entra ID (Azure AD). Zudem erfordern automatisierte Batch-Pipelines, ETL-Jobs und Fremdsysteme unpersönliche Machine-to-Machine (M2M) Zugriffe, die nicht an interaktive Benutzer-SIDs gebunden sind.

## Entscheidung
1. **Identitätsquellen-Abstraktion (`IIdentityProvider`)**: Entkopplung der Gateway-Kernlogik von Active Directory. Das Gateway unterstützt parallelen Hybrid-Betrieb von Kerberos und Entra ID / OIDC.
2. **Standardisierte Claims-Transformation (`EnterpriseClaimsTransformation`)**: Vereinheitlicht Claims (`oid`, `sub`, `appid`, `client_id`, `onprem_sid`) in typsichere `Sid`-Identifikatoren.
3. **M2M-Dienst-Prinzipale**:
   - Eigene Identifikations-Präfixe (`SP-<client_id>` oder Service-Account-SIDs) für OAuth2 Client-Credentials und mutual TLS (mTLS).
   - Eigenständiges Consent-Modell für Dienstkonten mit technischer Begründung, Ablaufdatum und Data-Owner-Freigabe.
4. **Mandantenfähigkeit (Multi-Tenancy)**: Zweistufige Isolation über SQL-Prädikate (`tenant_id = @tenant`) und native PostgreSQL Row-Level Security (`SET LOCAL app.tenant_id = @tenant`).

## Konsequenzen
### Positiv
- Zukunftssichere Hybrid-Architektur ohne Refactoring bei Cloud-Migration.
- Saubere Trennung zwischen interaktiven Endbenutzern und automatisierten Batch-Jobs.
- Vollständige Revisionssicherheit für M2M-Datenabfragen.

### Negativ / Risiken
- Erhöhte Komplexität bei der Validierung und Verwaltung verschiedener Token-Typen. Gegenmaßnahme: Smart Dynamic Scheme Selector und einheitliche Claims-Normalisierung.
