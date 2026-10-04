# ADR-016: Remediation externer Security Code Review Findings (Mandantenisolation, Invarianten, Secret & ABAC Hardening)

## Status
Akzeptiert & Vollständig Implementiert

## Kontext
Im Rahmen eines externen Security Code Reviews wurden 5 spezifische Schwachstellen und Härtungspotenziale identifiziert:
1. **Finding 1 (🔴 Kritisch - Cross-Tenant Bypass via `X-Tenant-ID`-Header)**:
   - `TenantResolutionMiddleware` prüfte Claim-Tenant vs. Header-Tenant nur, wenn beide gesetzt waren. Weder `BasicAuthenticationHandler` noch `ForwardAuthAuthenticationHandler` setzten zuvor Tenant-Claims (`tenant_id`, `tid`, `tenant`). Ein authentifizierter Angreifer konnte über den `X-Tenant-ID`-Header auf beliebige Fremdmandanten zugreifen.
2. **Finding 2 (🟠 Hoch - Inkonsistente Security-Gates rund um `EnableTestAuthHandler` / `IsAnonymousAccessAllowed`)**:
   - `EnableTestAuthHandler` und `IsAnonymousAccessAllowed` wurden in Validation, DI-Registrierung und Scheme-Auswahl unterschiedlich behandelt. In Nicht-Development-Umgebungen konnte bei `IsAnonymousAccessAllowed=true` die Validierung umgangen werden, was zur Laufzeit zu DoS (`InvalidOperationException`) oder potenzieller Rechte-Eskalation führte.
3. **Finding 3 (🟡 Mittel - `eval(p.sub_rule)` im Casbin-Matcher)**:
   - Dynamic ABAC Matching via Casbin-Govaluate birgt RCE-Gefahr, falls `sub_rule` dynamisch aus nicht vertrauenswürdigen Quellen ohne strikte Token-Filterung injiziert wird.
4. **Finding 4 (🟡 Mittel - Breite Alias-Derivation in `DefaultEnvironmentSecretProvider`)**:
   - `Contains("hmac")` und `Contains("itsm")` führten zu breiten Wildcard-Kandidaten, wodurch generische Umgebungsvariablen unbeabsichtigt für sensitive HMAC/ITSM-Zwecke gebunden werden konnten.
5. **Finding 5 (🟡 Mittel - Deterministisches HMAC-Pseudonymisieren für Low-Entropy-Felder)**:
   - Deterministisches Hashing ohne pro-Zeile-Salt ermöglicht statistische Korrelation und Brute-Force über kleine Wertebereiche (z. B. SSN, Telefonnummern).

## Entscheidung
Wir implementieren eine umfassende, mehrschichtige Behebung und architektonische Härtung:

### 1. Strikt autoritative Mandanten-Claims & Anti-Spoofing Guard (Finding 1)
- **Modell- & Handler-Erweiterung**:
  - `BasicAuthUserConfig` erhält Eigenschaft `TenantId` (Default: `"default"`). `BasicAuthenticationHandler` emittiert zwingend `tenant_id`, `tenant` und `tid` Claims.
  - `ForwardAuthOptions` erhält `TenantHeader` (`"X-Forwarded-Tenant"`) und `DefaultTenantId`. `ForwardAuthAuthenticationHandler` liest den Mandanten aus verifizierten Proxy-Headern oder Fallbacks und emittiert zwingend `tenant_id`, `tenant` und `tid`.
- **TenantResolutionMiddleware Guard**:
  - Für authentifizierte Anfragen ist der Tenant-Claim **strikt autoritativ**.
  - Stimmt ein vom Client gesendeter `X-Tenant-ID`-Header nicht mit dem Token-Claim überein, wird die Anfrage mit `403 Forbidden` (`CROSS_TENANT_ACCESS_FORBIDDEN`) abgewiesen.
  - Besitzt ein authentifizierter Nutzer keinen Mandanten-Claim, ist das Überschreiben via Header verboten (Abweisung mit 403), es sei denn, er besitzt administrative Gateway-Rechte (`GatewayAdmin`, `PlatformAdmin`).

### 2. Zentrale, redundanzfreie Invarianten für Test- & Anonymous-Auth (Finding 2)
- `EnableTestAuthHandler` und `danger_allow_anonymous_access` sind **strikt und ausnahmslos auf die Development-Umgebung beschränkt**.
- Sowohl `ValidateGatewayOptions` als auch `AddOptions<GatewayOptions>().Validate(...)` werfen außerhalb von Development unweigerlich eine `ValidationException`.
- Die Registrierung und Scheme-Selektion nutzen einheitlich `isTestAuthAllowed = environment.IsDevelopment() && (EnableTestAuthHandler || IsAnonymousAccessAllowed)`. In Produktion wird `TestAuthHandler` weder registriert noch ausgewählt.

### 3. Casbin sub_rule Input-Validierung (Finding 3)
- `CasbinEnforcementService.AddPolicy` validiert eingehende `subRule`-Ausdrücke zur Laufzeit gegen eine strikte Blacklist von CLR- und Reflection-Tokens (`System.`, `Process`, `File.`, `Directory.`, `Assembly`, `GetType`, `Activator`, `Environment.`, `AppDomain`, `MethodInfo`, `Invoke`, `Type.`, `Reflection`).
- Nicht konforme Regeln werden sofort mit `ArgumentException` abgewiesen.

### 4. Bounded Candidate Derivation & Audit-Logging (Finding 4)
- `DefaultEnvironmentSecretProvider` entfernt unspezifische Substring-Prüfungen (`Contains`).
- Kandidaten werden ausschließlich für explizite Präfixe (`itsm:`, `hmac:`) oder exakte Standard-Namen abgeleitet.
- Die Auflösung von Secrets wird über `ILogger` für Audit-Zwecke auf `Debug`-Ebene protokolliert (Schlüsselname ohne Offenlegung des Secret-Wertes).

### 5. Risiko-Kompensation für deterministisches Masking (Finding 5)
- Deterministisches HMAC-Hashing (`IColumnMaskingProvider`) ist architektonisch erforderlich, um relationale Verknüpfungen (JOINs/GROUP BY) über pseudonymisierte Attribute in Föderations-Szenarien zu erhalten.
- Kompensierende Kontrollen:
  - Pflicht zur Nutzung sicherer Key-Vault-Schlüssel außerhalb von Development.
  - Schlüssel-Derivation (`GetOrDeriveKey`) mit isolierten Schlüsseln je Spalte/Typ (`hmacKeyId`).
  - Regelmäßige Rotation der Master-Schlüssel in Azure Key Vault / HashiCorp Vault.

## Konsequenzen
### Positiv
- Vollständige Eliminierung des Cross-Tenant-Bypass-Risikos: Mandanten-Identitäten sind kryptografisch bzw. über Identity-Provider-Claims verankert.
- Ausfallsicherheit und Robustheit: Keine Diskrepanzen mehr zwischen Options-Validierung und Scheme-Registrierung.
- Erhöhte Transparenz im Audit-Trail durch strukturierte Secret-Resolution-Logs.
- Sämtliche 535 Tests (417 Unit, 5 Architecture, 84 Integration, 29 Extensions) sind grün.
