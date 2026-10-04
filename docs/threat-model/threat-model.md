# STRIDE Threat Model (DOK-07): C# GraphQL Enterprise Gateway

**Dokumenten-Status:** Freigegeben / Normativ  
**Referenzen:** [README.md](file:///root/gql/README.md), [gatewayconfig.md](file:///root/gql/gatewayconfig.md)

---

## 1. Systemübersicht & Vertrauensgrenzen (Trust Boundaries)

```
[ Unauthenticated Zone / Internet / LAN ]
                   |
     (TB-1: Network / Pre-Auth)
                   v
[ Pre-Auth IP Rate Limiter & Reverse Proxy ]
                   |
     (TB-2: Negotiate / Kerberos Auth)
                   v
[ Gateway Pod: Authentication & Authorization Middleware ]
                   |
     (TB-3: Distributed Cache & Event Bus)
                   v
[ Redis L2 & In-Process Channels (Epoch Validation) ]
                   |
     (TB-4: Data Access / Trusted Subsystem)
                   v
[ Governance DB (SQLite/MSSQL) & Read-Only Source Databases ]
```

---

## 2. STRIDE-Bedrohungsanalyse & Gegenmaßnahmen

### 2.1 Spoofing (Identitätsanmaßung)
- **Bedrohung 1.1: Spoofing von Benutzer-Identitätsheadern (`X-Test-User-Sid`).**
  - *Gefahr:* Ein Angreifer sendet gefälschte Header, um sich als privilegierter Benutzer oder Data Owner auszugeben.
  - *Gegenmaßnahme:* Startup-Validierung in `Program.cs` (`ValidateOnStart()`): `EnableTestAuthHandler` führt in Produktionsumgebungen (`builder.Environment.IsProduction()`) zum **sofortigen Boot-Absturz**. In Produktion akzeptiert das Gateway ausschließlich verifizierte Protokolle (ForwardAuth, Entra ID / AD FS Bearer, Basic Auth oder Kerberos).
- **Bedrohung 1.2: Spoofing von Traefik ForwardAuth-Headern (`X-Forwarded-User`, `X-Forwarded-Groups`).**
  - *Gefahr:* Ein Angreifer im internen Netzwerk sendet direkt gefälschte `X-Forwarded-*`-Header an das Gateway, um Identitäten oder Rollen vorzutäuschen.
  - *Gegenmaßnahme:* Multi-Faktor-Netzwerk- und Secret-Validierung in `ForwardAuthAuthenticationHandler`:
    1. *Proxy-IP-Einschränkung:* Ist `RequireTrustedProxy = true`, werden Anfragen abgewiesen, wenn die Remote-IP nicht exakt in `TrustedNetworks` (CIDR-Subnetze der Traefik-Pods) oder `TrustedProxies` liegt.
    2. *Timing-sicheres Shared Secret:* Abgleich des Headers `X-Forwarded-Secret` mit einem über Key Vault bereitgestellten Secret (`SharedSecretKeyVaultRef`) via `CryptographicOperations.FixedTimeEquals` verhindert Replay- und Spoofing-Attacken.
- **Bedrohung 1.3: Timing- und Brute-Force-Angriffe auf Passwörter bei HTTP Basic Authentication.**
  - *Gefahr:* Angreifer leiten Passwörter über Laufzeitunterschiede ab oder knacken ungesalzene Hashes mittels Rainbow Tables.
  - *Gegenmaßnahme:* `BasicAuthenticationHandler` erzwingt in Produktion gesalzene PBKDF2-Hashes (`$pbkdf2$<iterations>$<salt>$<hash>`). Klartext- und ungesalzene SHA-256-Passwörter sind **ausschließlich in `Development`** zulässig und werden in Produktion mit `HTTP 401 Unauthorized` abgewiesen. Für nicht existierende Benutzer führt der Handler eine Dummy-PBKDF2-Berechnung mit identischer Iterationszahl aus, um Timing-Angriffe zur Benutzer-Enumeration unmöglich zu machen. Alle Vergleiche erfolgen byte-genau via `CryptographicOperations.FixedTimeEquals`.
- **Bedrohung 1.4: Ticket-Manipulation (Kerberos PAC Spoofing).**
  - *Gefahr:* Gefälschte Gruppenmitgliedschaften im Kerberos-Ticket.
  - *Gegenmaßnahme:* Kerberos-Validierung gegen das Active Directory mit Signaturprüfung der Privilege Attribute Certificate (PAC). Aufgelöste Gruppen werden maximal für `GroupCacheTtlMinutes` (Standard 5 min) gecacht.
- **Bedrohung 1.5: Server-Side Request Forgery (SSRF) über deklarative REST-Datenquellen.**
  - *Gefahr:* Angreifer konfigurieren oder manipulieren Endpunkt-URLs, um interne Cloud-Metadaten (`169.254.169.254`, `metadata.google.internal`), Kubernetes API-Server oder interne Intranet-Dienste abzufragen.
  - *Gegenmaßnahme:* `DeclarativeHttpDataSourceExecutor` führt vor jedem Request eine DNS-Auflösung durch und blockiert RFC 1918 (private Netze `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), Link-Local (`169.254.0.0/16`), Loopback (`127.0.0.0/8`, `::1`) sowie spezifische Cloud-Metadaten-Hostnamen fail-closed. Zudem ist `AllowAutoRedirect = false` konfiguriert; Weiterleitungen werden per `SendWithRedirectProtectionAsync` Hop-für-Hop isoliert re-validiert (max. 5 Hops), sodass kein SSRF über HTTP 30x Redirects möglich ist.

### 2.2 Tampering (Daten- und Manipulationsschutz)
- **Bedrohung 2.1: SQL-Injection über dynamische GraphQL-Filter.**
  - *Gefahr:* Ein Angreifer injiziert SQL-Befehle über `where: { ... }` Argumente.
  - *Gegenmaßnahme:* `SqlFilterProvider` validiert alle Tabellen- und Spaltenbezeichner strikt gegen eine Regex-Whitelist (`^[a-zA-Z_][a-zA-Z0-9_]*$`) und den Metadatenkatalog `TableMetadata.HasColumn(...)`. Unbekannte Felder werfen sofort eine Exception. Alle Filterwerte werden ausnahmslos als typisierte SQL-Parameter (`@p1`, `$1`) gebunden.
- **Bedrohung 2.2: Manipulation des Audit-Trails.**
  - *Gefahr:* Ein böswilliger Administrator mit DB-Schreibzugriff ändert oder löscht Einträge in `AUDIT_LOG_ENTRIES` und berechnet Hashes neu.
  - *Gegenmaßnahme:* Kryptografische **HMAC-SHA256** Verkettung (`entry_hash = HMACSHA256(auditKey, prev_hash | payload)`). Ohne den im Key Vault isolierten HMAC-Schlüssel kann kein gültiger Nachfolge-Hash erzeugt werden. Integritätsprüfungen vergleichen die Hash-Kette mit `CryptographicOperations.FixedTimeEquals`.
- **Bedrohung 2.3: Cache-Poisoning im Consent-Cache.**
  - *Gefahr:* Ein Angreifer manipuliert L1/L2 Cache-Einträge, um unberechtigten Zugriff zu erhalten.
  - *Gegenmaßnahme:* Jeder Cache-Eintrag speichert die `epoch`, mit der er berechnet wurde. Vor Rückgabe validiert `IEpochValidationService` atomar gegen die Governance-DB.

### 2.3 Repudiation (Nicht-Abstreitbarkeit)
- **Bedrohung 3.1: Abstreiten von Lesezugriffen auf sensible Daten.**
  - *Gefahr:* Ein Nutzer streitet ab, sensible Gehalts- oder Kundendaten abgefragt zu haben.
  - *Gegenmaßnahme:* Transaktionales Audit-Logging (Stufe A) für alle Zugriffe auf als `HIGH` markierte Tabellen mit `ActorSid`, Zeitstempel, Tabelle und W3C-Trace-ID.
- **Bedrohung 3.2: Abstreiten von Consent-Genehmigungen.**
  - *Gefahr:* Ein Data Owner bestreitet die Freigabe eines Zugriffsantrags.
  - *Gegenmaßnahme:* Genehmigungen (`APPROVAL_STEPS`) speichern die authentifizierte `ApproverSid`, Entscheidung und exakten Zeitstempel im revisionssicheren Speicher.
- **Bedrohung 3.3: Webhook-Replay-Angriffe (`/api/webhooks/openmetadata`).**
  - *Gefahr:* Ein Angreifer fängt ein gültiges OpenMetadata-Katalog-Update ab und sendet es wiederholt, um Cache-Invalidierungen zu spammen oder den Datenstand zurückzurollen.
  - *Gegenmaßnahme:* `OpenMetadataSyncService` erzwingt neben der HMAC-SHA256-Signatur (`X-OpenMetadata-Signature`) zwingend vorhandene `Id`- und `Timestamp`-Felder. Nachrichten mit Zeitstempel außerhalb des 5-Minuten-Gleitzeitfensters oder bereits verarbeitete Event-IDs werden fail-closed abgewiesen.

### 2.4 Information Disclosure (Offenlegung von Informationen)
- **Bedrohung 4.1: Ausspähen von Tabellennamen via Schema-Introspection.**
  - *Gefahr:* Angreifer rekonstruieren vertrauliche Unternehmensstrukturen über GraphQL-Introspection.
  - *Gegenmaßnahme:* Introspection und Hot Chocolate Banana Cake Pop sind in Produktion standardmäßig deaktiviert (`EnableIntrospection: false`, `EnableBananaCakePop: false`).
- **Bedrohung 4.2: Inferenz-Lecks über Filter/Sortierung auf maskierten Feldern (F-CONS-07 Regel 5).**
  - *Gefahr:* Ein Nutzer filtert auf `where: { salary: { gt: 100000 } }`. Anhand der Treffermenge erfährt er das Gehalt, obwohl die Spalte für ihn als `MASK` definiert ist.
  - *Gegenmaßnahme:* F-CONS-07 Regel 5 verbietet Filter, Sortierung und Gruppierung auf Spalten, deren effektive Stufe nicht `CLEAR` ist. Zudem validiert `SqlDataSourceExecutor` die Zugriffsstufe vor der SQL-Generierung.
- **Bedrohung 4.3: Stack-Trace- und Exception-Lecks.**
  - *Gefahr:* Fehlerhafte Abfragen geben DB-Verbindungsdaten oder interne Pfade an den Client zurück.
  - *Gegenmaßnahme:* `ErrorSanitizingFilter` fängt unbehandelte Exceptions ab, loggt sie geschützt im Server-Log und gibt dem Client nur neutrale Codes (`INTERNAL_SERVER_ERROR`).
- **Bedrohung 4.4: Table Oracle / Schema-Enumeration über Fehlermeldungen.**
  - *Gefahr:* Angreifer probieren gezielt Tabellennamen durch, um anhand von `TableNotFoundException` vs. `AccessDenied` die Existenz interner Tabellen aufzudecken.
  - *Gegenmaßnahme:* `ErrorSanitizingFilter` maskiert `TableNotFoundException` in Produktionsumgebungen als generischen `FORBIDDEN`-Fehlercode, sodass Angreifer keine Information über die tatsächliche Existenz einer Tabelle erhalten.

### 2.5 Denial of Service (Verfügbarkeitsangriffe)
- **Bedrohung 5.1: Kerberos-Handshake-Flooding (Negotiate DoS).**
  - *Gefahr:* Ressourcenüberlastung des Gateways und Domain Controllers durch unvollständige Auth-Handshakes.
  - *Gegenmaßnahme:* `PreAuthIpRateLimitingMiddleware` begrenzt eingehende Verbindungen pro IP-Adresse **vor** dem Eintritt in die Windows-Negotiate-Middleware.
- **Bedrohung 5.2: Komplexe / rekursive GraphQL-Queries ("Billion Laughs" für GraphQL).**
  - *Gefahr:* Tief geschachtelte Relationen oder gigantische Queries blockieren CPU und RAM.
  - *Gegenmaßnahme:* Hot Chocolate Query Depth Enforcement (`MaxAllowedExecutionDepth: 10`), Query Complexity Analyzer (Limit: 1500), erzwungenes Keyset-Paging (max. 250 Zeilen) und hartes Timeout (`QueryTimeoutSeconds: 30`).
- **Bedrohung 5.3: Überlastung durch einzelne Benutzer-SIDs.**
  - *Gefahr:* Ein kompromittiertes Benutzerkonto sendet tausende Abfragen pro Sekunde.
  - *Gegenmaßnahme:* `PostAuthSidRateLimitingMiddleware` implementiert einen Token-Bucket-Limiter pro SID mit konfigurierbarer Kapazität und Nachfüllrate. Der In-Memory-Limiter verwendet lock-freie atomare `Interlocked`-Zähler zur Vermeidung von Lock-Contention auf Hot-Paths.
- **Bedrohung 5.4: Ausfall oder Degradierung des Redis-Clusters beim verteilten Rate-Limiting.**
  - *Gefahr:* Unerreichbarkeit von Redis führt entweder zu ungeschütztem Totalzugriff (Fail-Open) oder zu kompletter Gateway-Blockade (Fail-Closed).
  - *Gegenmaßnahme:* `RedisRateLimiterService` verfügt über einen automatischen Fallback auf den lokalen `InMemoryRateLimiterService`. Bei Redis-Verbindungsfehlern schützt die lokale Token-Bucket-Engine die Instanz nahtlos weiter, ohne Verbindungen abzubrechen.

### 2.6 Elevation of Privilege (Rechteausweitung)
- **Bedrohung 6.1: Selbst-Genehmigung von Rechten (Vier-Augen-Bypass).**
  - *Gefahr:* Ein Antragsteller genehmigt seinen eigenen Antrag auf hochsensible Daten.
  - *Gegenmaßnahme:* Strikte Durchsetzung der Funktionstrennung in `SqliteGovernanceRepository.ApproveConsentRequestStepAsync`: `req.RequesterSid == approverSid` wirft eine `InvalidOperationException`.
- **Bedrohung 6.2: Kompromittierung des technischen DB-Dienstkontos (Blast Radius).**
  - *Gefahr:* Angreifer erlangt Dienstkonto-Zugangsdaten und greift direkt auf die Quelldatenbank zu.
  - *Gegenmaßnahme:* Das Dienstkonto besitzt auf der Quelldatenbank ausschließlich `SELECT`-Berechtigungen auf freigegebene Schemas/Tabellen. Keine DDL-, Schreib- oder Server-Admin-Rechte. Netzwerk-Isolation (nur Zugriffe von Gateway-Pod-IPs).
- **Bedrohung 6.3: Rechteausweitung über unbefugte Aktivierung von "Insecure Modes" (`danger_*`).**
  - *Gefahr:* Ein böswilliger Entwickler oder Operator setzt `danger_bypass_authorization = true` in Produktion, um Zugriffskontrollen auszuhebeln.
  - *Gegenmaßnahme:*
    1. Alle unsicheren Optionen tragen das Präfix `danger_` oder `warn_` und sind in `Production` standardmäßig `false`.
    2. Der Gateway-Host loggt beim Start und bei jeder Nutzung auffällige `CRITICAL SECURITY ALERT`-Events im Audit-Log.
    3. CI/CD-Pipelines prüfen `appsettings.Production.json` automatisiert auf das Vorkommen von `danger_` oder `warn_` mit Wert `true`.
- **Bedrohung 6.4: Unbefugtes Auslesen von Data Owner E-Mail-Adressen über Lineage-Abfragen.**
  - *Gefahr:* Ein regulärer Analyst ruft `tableConsumers` auf, um gezielt E-Mail-Adressen von Systemverantwortlichen für Phishing-Kampagnen zu ernten.
  - *Gegenmaßnahme:* Zero-Trust Spaltenmaskierung in `LineageImpactAnalyzerService`: Das Feld `OwnerEmail` wird für Aufrufer ohne administrative Rollen (`GovernanceAdmin`, `ClusterAdmin`) oder ohne Data-Owner-Berechtigung auf der Tabelle strikt auf `null` maskiert.
- **Bedrohung 6.5: Manipulation von Sensitivitäts-Klassifizierungen über gefälschte externe Katalog-Updates.**
  - *Gefahr:* Ein Angreifer manipuliert Metadaten in Purview/Collibra, um Art.-9-DSGVO-Tags zu entfernen und so Vier-Augen-Freigaben zu umgehen.
  - *Gegenmaßnahme:*
    1. TLS-Zertifikatsvalidierung bei allen ausgehenden Catalog-API-Verbindungen.
    2. Zero-Trust Invariante: Externe Datenkataloge dienen als Metadatenquelle, erteilen jedoch **keine direkten Zugriffsrechte**. Berechtigungen werden ausschließlich über vom Data Owner genehmigte Consents erteilt.
    3. Monotone Epochen-Invalidierung: Jedes Katalog-Update triggert einen Policy-Epochen-Inkrement, der gecachte Berechtigungen invalidiert.

---

## 3. Fazit & Risikoverbleib
Alle STRIDE-Kategorien sind durch mehrstufige, software- und architekturseitige Schutzmechanismen (Defense-in-Depth) abgedeckt. Das Restrisiko bei verzögerter Übernahme von AD-Gruppenänderungen ist im Risikoregister (R-04) erfasst und durch `GroupCacheTtlMinutes: 5` zeitlich eng begrenzt.

