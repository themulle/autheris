# Konfigurationshandbuch – GraphQL Enterprise Gateway

Dieses Handbuch bietet eine vollständige, praxiserprobte Referenz aller Konfigurationsoptionen des **GraphQL Enterprise Gateway**, inklusive Validierungsregeln, Sicherheitsinvariablen, Umgebungsvariablen, Kubernetes-Konfiguration und Profilen (Entwicklung vs. Produktion).

---

## 1. Architektur der Konfiguration

Die Konfiguration basiert auf dem .NET 10 Options-Pattern (`IOptions<GatewayOptions>`) mit strenger Validierung beim Anwendungsstart (**Fail-Fast** via `ValidateOnStart()`). 

### 1.1 Hierarchie & Laderangfolge
Konfigurationswerte werden in folgender Priorität ausgewertet (spätere Quellen überschreiben frühere):
1. **`appsettings.json`**: Basis-Konfiguration für Standardeinstellungen.
2. **`appsettings.{Environment}.json`**: Umgebungsabhängige Überschreibungen (z. B. `appsettings.Development.json` oder `appsettings.Production.json`).
3. **Umgebungsvariablen**: Container- und Host-Konfiguration (Syntax: doppelte Unterstriche `Gateway__Section__Property`).
4. **Secrets Provider**: Key Vault / Environment Secret Fallback für sensible kryptografische Schlüssel.

### 1.2 Startup-Validierung & Sicherheitsinvariablen
Beim Hochfahren des Hosts (`Program.cs`) führt `GatewayServiceCollectionExtensions.AddGatewayOptions` rekursive DataAnnotation- und semantische Sicherheitsprüfungen durch. Schlägt eine Bedingung fehl, bricht der Startprozess mit einer `ValidationException` sofort ab:

| Regel-ID | Prüfung | Fehlerbedingung |
| :--- | :--- | :--- |
| **NF-HA-01a** | `ShutdownTimeoutSeconds >= QueryTimeoutSeconds + 10` | Shutdown muss mindestens 10 Sekunden mehr Puffer als der längste Query-Timeout haben. |
| **NF-HA-01b** | `TerminationGracePeriodSeconds >= DrainDelaySeconds + ShutdownTimeoutSeconds + 10` | Kubelet Grace Period muss den gesamten Drain- und Shutdown-Zyklus abdecken. |
| **NF-SEC-01** | `environment.IsDevelopment() \|\| !Authentication.EnableTestAuthHandler` | Der `TestAuthHandler` (Header-basiertes SID-Impersonation) ist außerhalb von `Development` **strikt verboten**. |
| **NF-SEC-03** | Außerhalb von Development: `!string.IsNullOrWhiteSpace(HmacSecretKeyVaultRef)` und nicht gleich Test-Defaults | HMAC-Salts müssen in Staging/Produktion aus einem sicheren Secret-Store stammen. |
| **NF-SEC-04** | Außerhalb von Development: BasicAuth Benutzer müssen zwingend das gesalzene PBKDF2-Format (`$pbkdf2$...`) verwenden | Klartext- und ungesalzene SHA-256-Passwörter sind in Staging/Produktion verboten und werden zur Laufzeit mit `401 Unauthorized` abgewiesen. |
| **R-DEP-2** | Outside Development: Secret keys (`DataMasking.HmacSecretKeyVaultRef`, `GovernanceDb.AuditHmacKeyVaultRef`, `Authentication.ForwardAuth.SharedSecret`) must be at least 32 bytes in UTF-8 representation | Cryptographic keys shorter than 32 UTF-8 bytes are rejected fail-closed during startup validation. |
| **R-API-1** | Outside Development: `Itsm.LegacyGlobalWebhookSecret = true` and all `DANGER:` bypass flags strictly prohibited | Setting `Itsm.LegacyGlobalWebhookSecret = true` or any `DANGER:` flags causes an immediate `ValidationException` process startup crash outside `Development`. |
| **F-6 / POL-1** | When `Casbin:Enabled = true`: `Casbin:ModelPath` and `Casbin:PolicyPath` are mandatory and must satisfy `CasbinModelContract` | Missing configuration or failure of mandatory probes (M1–M8) or wildcard safety probes (W2–W6) causes immediate startup failure. |

> [!IMPORTANT]
> **Secret Key Format and Requirements (R-DEP-2 & Arch 6)**:
> Startup and runtime validation checks the **UTF-8 byte count** of the resolved secret text string (`byteCount >= 32`).
> - **Entropy Warning**: A string of 32 hexadecimal characters (`[0-9a-fA-F]{32}`) occupies 32 ASCII/UTF-8 bytes, but represents only **16 bytes (128 bits) of cryptographic entropy**, which is insufficient for production HMAC master and audit keys.
> - **Recommended Practice**: Provide secrets as Base64-encoded strings representing at least **32 cryptographically random bytes** (256 bits of raw entropy, yielding >= 44 Base64 ASCII characters) or generate at least 32 raw random characters.

---

## 2. Vollständige Referenz der Konfigurationssektionen (`Gateway:*`)

Alle gateway-spezifischen Optionen befinden sich unter dem Hauptknoten `"Gateway"`.

### 2.1 `HighAvailability` (Hochverfügbarkeit & Graceful Shutdown)

Steuert das Verkehrs-Draining bei Rolling Deployments und Pod-Terminierungen.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `DrainDelaySeconds` | `int` | `1 .. 30` | `5` | Wartezeit nach Eingang des SIGTERM-Signals, bevor aktive Verbindungen geschlossen werden (erlaubt K8s Endpoints-Controller das Propagieren des Unready-Zustands). |
| `QueryTimeoutSeconds` | `int` | `5 .. 120` | `30` | Maximal zulässige Ausführungsdauer eines GraphQL-Requests. |
| `ShutdownTimeoutSeconds` | `int` | `10 .. 180` | `40` | Maximale Zeitspanne, die aktiven Anfragen eingeräumt wird, um regulär zu beenden. |
| `TerminationGracePeriodSeconds` | `int` | `20 .. 300` | `60` | Kubelet Pod-Grace-Period-Korridor zur Vermeidung von abruptem SIGKILL. |

```json
"HighAvailability": {
  "DrainDelaySeconds": 5,
  "QueryTimeoutSeconds": 30,
  "ShutdownTimeoutSeconds": 40,
  "TerminationGracePeriodSeconds": 60
}
```

---

### 2.2 `Authentication` (Multi-Protocol Identity, ForwardAuth & Kerberos)

Das Gateway unterstützt ein flexibles, mehrgleisiges Authentifizierungskonzept mit automatischer Protokollauswahl (**Smart Dynamic Scheme Selector**). Es vereint Kubernetes Ingress ForwardAuth, Microsoft Entra ID (Azure AD), AD FS, HTTP Basic Authentication und Windows Kerberos.

#### 2.2.1 Basiseinstellungen & Kerberos / Negotiate
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Domain` | `string` | Gültiger FQDN | `"CORP.LOCAL"` | Active Directory Domäne. |
| `ServicePrincipalName` | `string` | SPN-Format | `"HTTP/gql-gateway.corp.local"` | Kerberos Service Principal Name für SPNEGO/Negotiate. |
| `RequireKerberosOnly` | `bool` | `true \| false` | `true` | Enforces Kerberos: `NTLM` Authorization headers are not routed to Negotiate, and identities authenticated by Negotiate with an authentication type other than Kerberos (e.g. NTLM) are rejected. `PersistNtlmCredentials`/`PersistKerberosCredentials` are always disabled so credentials never carry over on shared upstream connections. |
| `GroupCacheTtlMinutes` | `int` | `1 .. 60` | `5` | TTL für den lokalen Cache aufgelöster Windows-Gruppen-SIDs. |
| `EnableTestAuthHandler` | `bool` | `true \| false` | `false` | Ermöglicht `X-Test-User-Sid`-Header zur Simulation von Identitäten (**nur in Development erlaubt!**). |

#### 2.2.2 `Authentication.ForwardAuth` (Kubernetes / Traefik Ingress)
Wird das Gateway in Kubernetes betrieben, kann die Authentifizierung an den vorgelagerten Ingress-Controller (z. B. **Traefik Ingress**) via ForwardAuth (z. B. Authelia, Keycloak Gatekeeper, Authentik, OAuth2-Proxy) delegiert werden. Traefik terminiert SSL, prüft das Benutzer-Session-Cookie oder JWT und leitet die verifizierten Identitätsmerkmale per HTTP-Header weiter:

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Aktiviert das ForwardAuth Authentication Scheme. |
| `UserHeader` | `string` | Header-Name | `"X-Forwarded-User"` | Header mit Benutzeridentifikator (Benutzername oder User-SID). |
| `EmailHeader` | `string` | Header-Name | `"X-Forwarded-Email"` | Header mit der E-Mail-Adresse des Benutzers. |
| `GroupsHeader` | `string` | Header-Name | `"X-Forwarded-Groups"` | Kommagetrennte Liste von Gruppen-SIDs oder Gruppennamen. |
| `RolesHeader` | `string` | Header-Name | `"X-Forwarded-Roles"` | Kommagetrennte Liste von Rollen (z. B. `GovernanceAdmin,DataOwner`). |
| `SharedSecretHeader` | `string` | Header-Name | `"X-Forwarded-Secret"` | Header für das Pre-Shared Secret zwischen Ingress und Gateway. |
| `SharedSecret` | `string` | Geheimes Token | `""` | Pre-Shared Secret for ForwardAuth validation. Outside Development, mandatory if `SharedSecretKeyVaultRef` is unset, and must be at least 32 UTF-8 bytes long (`Encoding.UTF8.GetByteCount >= 32`). Recommended: Base64 string of >= 32 cryptographically random bytes (256 bits). |
| `SharedSecretKeyVaultRef` | `string` | Secret-Name | `""` | Name des Secrets in Azure Key Vault / HashiCorp Vault. |
| `RequireTrustedProxy` | `bool` | `true \| false` | `true` | **Zero-Trust**: Erzwingt, dass Anfragen zwingend von einer IP aus `TrustedNetworks` oder `TrustedProxies` stammen müssen. |
| `TrustedProxies` | `List<string>` | IP-Adressen | `[]` | Feste IP-Adressen der vertrauenswürdigen Traefik-Pods / Proxies. |
| `TrustedNetworks` | `List<string>` | CIDR-Blöcke | `["127.0.0.1/32", "::1/128"]` | Erlaubte Subnetze (z. B. Kubernetes Pod-CIDR `"10.244.0.0/16"`). |

#### 2.2.3 `Authentication.BasicAuth` (HTTP Basic Authentication & Login-API)
Ermöglicht direkte Authentifizierung via `Authorization: Basic <base64>` für GraphQL-Queries sowie einen dedizierten Endpunkt `/api/auth/login`:

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Aktiviert HTTP Basic Auth und den Login-Endpunkt `/api/auth/login`. |
| `Users` | `List<BasicAuthUserConfig>` | Array | `[]` | Liste konfigurierter Benutzerkonten. |
| `Users[].Username` | `string` | Text | `""` | Eindeutiger Benutzername für Basic Auth. |
| `Users[].Password` | `string` | PBKDF2 / Klartext | `""` | In Produktion: Gesalzener PBKDF2-String im Format `$pbkdf2$<iterations>$<saltBase64>$<hashBase64>`. Klartext ist **nur in `Development`** erlaubt! |
| `Users[].PasswordHashSha256` | `string` | 64-Hex SHA256 | `""` | Veralteter ungesalzener SHA-256 Hash (**nur in `Development`** erlaubt; in Produktion verboten). |
| `Users[].Roles` | `List<string>` | Rollen-Array | `[]` | Zugewiesene Rollen (`DataConsumer`, `DataOwner`, `GovernanceAdmin`). |
| `Users[].UserSid` | `string` | SID-Format | `""` | Zugeordnete Windows-User-SID (z. B. `S-1-5-21-CONSUMER-1`). |
| `Users[].GroupSids` | `List<string>` | SID-Array | `[]` | Zugeordnete Windows-Gruppen-SIDs. |

> [!IMPORTANT]
> **Sicherheits-Invariante für Produktion**:
> - Außerhalb der `Development`-Umgebung werden ungesalzene SHA-256-Hashes (`PasswordHashSha256`) sowie Klartextpasswörter (`Password`) ausnahmslos abgelehnt (`401 Unauthorized`).
> - Passwörter müssen das PBKDF2-Format aufweisen: `$pbkdf2$<iterations>$<salt>$<hash>` (z. B. `$pbkdf2$600000$c2FsdHNhbHQ=$...`).
> - **Timing-Angriffsschutz**: Existiert ein angefragter Benutzername nicht, führt das Gateway im Hintergrund eine Dummy-PBKDF2-Berechnung mit derselben Iterationszahl durch, sodass Angreifer über Zeitmessungen keine gültigen Benutzernamen enumerieren können. Alle Hashvergleiche erfolgen via `CryptographicOperations.FixedTimeEquals`.

#### 2.2.4 `Authentication.EntraId` & `Authentication.Adfs` (JWT Bearer)
Unterstützt moderne OIDC/OAuth2-Bearer-Token aus Microsoft Entra ID (Azure AD) und Active Directory Federation Services (AD FS):

- **EntraId**:
  - `Enabled` (`bool`): Aktiviert Bearer-Validierung gegen Microsoft Entra ID.
  - `Instance` (`string`, Standard: `"https://login.microsoftonline.com/"`): Entra ID Login-Instanz.
  - `TenantId` (`string`): Entra ID Mandanten-ID (GUID).
  - `ClientId` (`string`): Anwendungs-Client-ID.
  - `Audience` (`string`): Erwartete Token-Audience (z. B. `"api://gql-gateway"`).
- **Adfs**:
  - `Enabled` (`bool`): Aktiviert Bearer-Validierung gegen AD FS.
  - `MetadataAddress` (`string`): Federation-Metadata-URL von AD FS.
  - `Audience` (`string`): Relying Party Identifier.
- **EnterpriseClaimsTransformation**:
  - Normalisiert Entra ID (`oid`, `preferred_username`, `groups` GUIDs/SIDs) und AD FS Claims (`onprem_sid`, `primarysid`, `primarygroupsid`, `roles`) automatisch in kanonische `ClaimTypes.PrimarySid`, `ClaimTypes.GroupSid` und `ClaimTypes.Role`.

```json
"Authentication": {
  "Domain": "CORP.LOCAL",
  "ServicePrincipalName": "HTTP/gql-gateway.corp.local",
  "RequireKerberosOnly": true,
  "GroupCacheTtlMinutes": 5,
  "EnableTestAuthHandler": false,
  "ForwardAuth": {
    "Enabled": true,
    "UserHeader": "X-Forwarded-User",
    "GroupsHeader": "X-Forwarded-Groups",
    "RolesHeader": "X-Forwarded-Roles",
    "SharedSecretHeader": "X-Forwarded-Secret",
    "SharedSecretKeyVaultRef": "GQL-FORWARD-AUTH-SECRET",
    "RequireTrustedProxy": true,
    "TrustedNetworks": [
      "127.0.0.1/32",
      "::1/128",
      "10.244.0.0/16"
    ]
  },
  "BasicAuth": {
    "Enabled": true,
    "Users": [
      {
        "Username": "service-analyst",
        "Password": "$pbkdf2$600000$ZXhhbXBsZXNhbHQxMjM0NQ==$dGVzdGhhc2hiYXNlNjQ=",
        "Roles": ["DataConsumer"],
        "UserSid": "S-1-5-21-CONSUMER-1",
        "GroupSids": ["S-1-5-21-FINANCE-ANALYSTS"]
      }
    ]
  },
  "EntraId": {
    "Enabled": true,
    "TenantId": "72f988bf-86f1-41af-91ab-2d7cd011db47",
    "ClientId": "a820c78a-f326-4d1d-91b4-2195f1342618",
    "Audience": "api://gql-gateway"
  }
}
```

---

### 2.3 `GovernanceDb` (Katalog- & Consent-Datenbank)

Speicherort für Metadaten, Freigaben, Delegationen, Vier-Augen-Genehmigungen und Audit-Logs.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Provider` | `string` | `"SqlServer"`, `"PostgreSql"`, `"Sqlite"` | `"SqlServer"` | Datenbank-Treiber für die Governance-Verwaltung. |
| `ConnectionString` | `string` | ADO.NET ConnStr | `"Data Source=governance.db;Cache=Shared"` | Verbindungszeichenfolge zur Governance-DB. |
| `CommandTimeoutSeconds` | `int` | `1 .. 60` | `15` | Timeout für Governance-SQL-Statements. |
| `EnableOutboxProcessor` | `bool` | `true \| false` | `true` | Startet den asynchronen Outbox-Worker für Invalidation-Events. |
| `SeedDemoData` | `bool` | `true \| false` | `true` | Initialisiert Demo-Tabellen und Standard-Governance-Regeln bei leerer DB. |

```json
"GovernanceDb": {
  "Provider": "Sqlite",
  "ConnectionString": "Data Source=governance.db;Cache=Shared",
  "CommandTimeoutSeconds": 15,
  "EnableOutboxProcessor": true,
  "SeedDemoData": false
}
```

---

### 2.4 `DataSources` (Backend-Fachdatenbanken & RLS-Pushdown)

Konfiguriert echte relationale Datenbank-Backends für die abgefragten Fachdaten. Das Gateway unterstützt über `ISqlConnectionFactory` die Provider `"SqlServer"`, `"PostgreSql"` und `"Sqlite"`. 

Im Gegensatz zu synthetischen Stubs führt der `SqlDataSourceExecutor` echte SQL-Queries aus und **pushed Row-Level Security (RLS) Filter direkt als WHERE-Klausel in die Datenbank**:

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `DataSources:Connections:{sourceName}:Provider` | `string` | `"SqlServer"`, `"PostgreSql"`, `"Sqlite"` | `"Sqlite"` | Datenbank-Treiber für die Ziel-Datenquelle. |
| `DataSources:Connections:{sourceName}:ConnectionString` | `string` | ADO.NET ConnStr | `""` | Verbindungszeichenfolge zur Zieldatenbank. DEP-7: außerhalb von Development verlangt der Start bei PostgreSQL `SSL Mode=VerifyFull` (oder `VerifyCA`), bei SQL Server `Encrypt=Mandatory`/`Strict` ohne `TrustServerCertificate=true`. |
| `DataSources:RequireTenantColumn` | `bool` | `true`, `false` | `false` | Review E-5: when `true`, a table without a tenant column (`tenant_id`, `TenantId`, ...) is refused fail-closed instead of being read unscoped. |
| `DataSources:TenantColumnExemptTables` | `string[]` | `schema.table` or `table` | `[]` | Tables that are deliberately shared across tenants and therefore exempt from `RequireTenantColumn`. |

```json
"DataSources": {
  "Connections": {
    "finance": {
      "Provider": "SqlServer",
      "ConnectionString": "Server=sql-finance.corp.local;Database=FinanceDb;Integrated Security=SSPI;Encrypt=Mandatory;TrustServerCertificate=false;"
    },
    "hr": {
      "Provider": "PostgreSql",
      "ConnectionString": "Host=pg-hr.corp.local;Port=5432;Database=HrDb;Username=gql_app;Password=<secret>;SSL Mode=VerifyFull;"
    }
  }
}
```

---

### 2.5 `Caching` (Zweistufiges Caching & Multi-Instance Redis Clustering)

Steuert den L1 In-Memory Cache, L2 Redis und die Konsistenzprüfung.

#### `Caching.L1MemoryCache`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `SizeLimitMb` | `int` | `16 .. 4096` | `512` | Maximale In-Memory-Cache-Größe in Megabyte. |
| `DefaultTtlMinutes` | `int` | `1 .. 120` | `10` | Standard-Gültigkeit zwischengespeicherter Consent-Entscheidungen. |
| `SensitiveTableTtlSeconds` | `int` | `1 .. 600` | `60` | Verkürzte TTL für als `IsSensitive = true` markierte Tabellen. |

#### `Caching.Redis`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Configuration` | `string` | StackExchange.Redis ConnStr | `"localhost:6379,abortConnect=false"` | Redis Host- und Verbindungsparameter. |
| `InstanceName` | `string` | Prefix | `"Autheris:"` | Schlüsselpräfix für Multi-Gateway-Cluster. |
| `InvalidationChannel` | `string` | Kanalname | `"consent:invalidations"` | Redis Pub/Sub Kanal für Epochen-Änderungen. |
| `ConnectTimeoutMs` | `int` | `100 .. 10000` | `2000` | Verbindungs-Timeout in Millisekunden. |
| `SyncTimeoutMs` | `int` | `100 .. 10000` | `1000` | Synchroner Lese-/Schreib-Timeout in ms. |

#### `Caching.EpochValidation`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `FailClosedOnSensitiveTables` | `bool` | `true \| false` | `true` | Zero-Trust: Bei Nichterreichbarkeit des Epochen-Stores wird der Zugriff auf sensible Tabellen blockiert. |
| `DegradedMaxStalenessSeconds` | `int` | `1 .. 300` | `30` | Maximale Staleness für unkritische Tabellen bei Redis-Ausfall. |
| `PipelinedMGetEnabled` | `bool` | `true \| false` | `true` | Nutzt Redis Batch-Pipelining zur Reduktion von Roundtrips. |

```json
"Caching": {
  "L1MemoryCache": {
    "SizeLimitMb": 512,
    "DefaultTtlMinutes": 10,
    "SensitiveTableTtlSeconds": 60
  },
  "Redis": {
    "Configuration": "redis-cluster.corp.local:6379,abortConnect=false,ssl=true",
    "InstanceName": "AutherisProd:",
    "InvalidationChannel": "consent:invalidations",
    "ConnectTimeoutMs": 2000,
    "SyncTimeoutMs": 1000
  },
  "EpochValidation": {
    "FailClosedOnSensitiveTables": true,
    "DegradedMaxStalenessSeconds": 30,
    "PipelinedMGetEnabled": true
  }
}
```

---

### 2.5 `RateLimiting` (DDoS- & Missbrauchsschutz)

Kombiniert Pre-Authentication IP-Limiting mit Token-Bucket-Verbrauch pro Windows-Benutzer-SID.

#### `RateLimiting.PreAuthIpRateLimit`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `PermitLimit` | `int` | `1 .. 100000` | `100` | Maximal erlaubte Requests pro IP-Adresse innerhalb des Fensters. |
| `WindowSeconds` | `int` | `1 .. 3600` | `60` | Zeitfenster in Sekunden für das IP-Rate-Limit. |
| `QueueLimit` | `int` | `>= 0` | `0` | Warteschlangengröße vor Zurückweisung mit `HTTP 429`. |

#### `RateLimiting.PostAuthSidRateLimit`
| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `TokenBucketCapacity` | `int` | `10 .. 100000` | `500` | Maximale Token-Kapazität pro authentifizierter User-SID. |
| `TokensPerSecond` | `int` | `1 .. 10000` | `50` | Nachfüllrate des Token-Buckets pro Sekunde. |
| `MaxCostPerMinute` | `int` | `100 .. 1000000` | `10000` | Maximales GraphQL-Komplexitätsbudget pro Minute pro Benutzer. |

```json
"RateLimiting": {
  "PreAuthIpRateLimit": {
    "PermitLimit": 100,
    "WindowSeconds": 60,
    "QueueLimit": 0
  },
  "PostAuthSidRateLimit": {
    "TokenBucketCapacity": 500,
    "TokensPerSecond": 50,
    "MaxCostPerMinute": 10000
  }
}
```

> [!TIP]
> **Resilienz & Hochverfügbarkeit (Failover)**:
> In Multi-Pod-Umgebungen nutzt `RedisRateLimiterService` Redis für die instanzübergreifende Ratenbegrenzung. Sollte das Redis-Cluster ausfallen oder Verbindungsprobleme melden, schaltet das Gateway **automatisch und transparent** auf den lokalen `InMemoryRateLimiterService` um. Anfragen werden nicht blockiert, und das Gateway bleibt vor DoS-Attacken geschützt.
> 
> **Performance auf Hot-Paths**:
> Der lokale `InMemoryRateLimiterService` pflegt IP- und Bucket-Zähler über atomare `Interlocked.Increment` / `Interlocked.Decrement` Operationen, um Deadlocks und Lock-Contention auf `ConcurrentDictionary.Count` vollständig zu vermeiden.


---

### 2.6 `GraphQL` (Engine- & Abfrageschutz)

Steuert Hot Chocolate 16 Parameter, Komplexitätsgrenzen und Anti-CSRF-Prüfungen.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `EndpointPath` | `string` | URL-Pfad | `"/graphql"` | Relativer Endpunkt-Pfad für GraphQL-Abfragen. |
| `MaxAllowedExecutionDepth` | `int` | `1 .. 25` | `10` | Maximale Abfragetiefe zur Vermeidung zyklischer DoS-Queries. |
| `MaxAllowedComplexity` | `int` | `100 .. 10000` | `1500` | Maximales statisches Query-Kostenbudget. |
| `EnableIntrospection` | `bool` | `true \| false` | `false` | Schema-Introspektion (`__schema`). In Produktion deaktivieren! |
| `PersistedQueriesOnly` | `bool` | `true \| false` | `false` | Erlaubt nur vorregistrierte Hash-basierte Abfragen. |
| `EnableBananaCakePop` | `bool` | `true \| false` | `false` | Nitro Banana Cake Pop GraphQL IDE im Browser. In Produktion deaktivieren! |
| `MaxResponseRows` | `int` | `100 .. 100000` | `5000` | Maximal zulässige Zeilenanzahl im Response-Budget. |
| `MaxResponseBytes` | `long` | `1 MB .. 100 MB` | `10485760` (10 MB) | Maximales Byte-Budget für GraphQL-Antworten. |
| `MaxInClauseBatchSize` | `int` | `10 .. 10000` | `500` | Maximale Batch-Größe für DataLoader `IN`-Prädikate. |
| `TrustedOrigins` | `List<string>` | URLs / `"*"` | `[]` | CORS- und Anti-CSRF-Erlaubnisliste für `Origin`/`Referer`-Header. |

```json
"GraphQL": {
  "EndpointPath": "/graphql",
  "MaxAllowedExecutionDepth": 10,
  "MaxAllowedComplexity": 1500,
  "EnableIntrospection": false,
  "PersistedQueriesOnly": false,
  "EnableBananaCakePop": false,
  "MaxResponseRows": 5000,
  "MaxResponseBytes": 10485760,
  "MaxInClauseBatchSize": 500,
  "TrustedOrigins": [
    "https://portal.corp.local",
    "https://analytics.corp.local"
  ]
}
```

> [!IMPORTANT]
> **Anti-CSRF Preflight Schutz**: Jede eingehende POST- oder GET-Abfrage an `/graphql` erfordert zwingend den Header `GraphQL-Preflight: 1` oder `X-Requested-With`. Browser-Anfragen ohne diesen Header werden mit `HTTP 400 Bad Request` abgewiesen.

---

### 2.7 `DataMasking` (Kryptografische Pseudonymisierung & Maskierung)

Konfiguriert die deterministische Pseudonymisierung (`HMAC_SHA256`) sowie Maskierungs-Caches.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `HmacKeyId` | `string` | Bezeichner | `"key-2026-q1"` | Schlüssel-ID zur Unterstützung von Key-Rotationen. |
| `HmacSecretKeyVaultRef` | `string` | Secret-Name | `"DEV_INSECURE_...` | Name of the secret in Azure Key Vault / HashiCorp Vault or environment fallback. Outside Development, must resolve to at least 32 UTF-8 bytes. |
| `MaskingCacheTtlHours` | `int` | `1 .. 168` | `24` | Gültigkeitsdauer des Caches für vorberechnete Maskierungsregeln. |

> [!NOTE]
> **Key Entropy & Length (R-DEP-2)**: The HMAC masking master key must contain >= 32 UTF-8 bytes outside Development. 32 hexadecimal characters encode only 16 bytes of entropy and are only 32 ASCII bytes, which could be weak. Recommended practice is a Base64-encoded string derived from at least 32 cryptographically secure random bytes (256 bits of entropy).

```json
"DataMasking": {
  "HmacKeyId": "key-2026-q1",
  "HmacSecretKeyVaultRef": "GQL-GATEWAY-HMAC-SECRET-KEY",
  "MaskingCacheTtlHours": 24
}
```

---

### 2.8 `Audit` (Manipulationssichere HMAC-SHA256 Audit-Hash-Chain)

Protokolliert Datenzugriffe manipulationssicher in einer kryptografisch verketteten HMAC-SHA256 Prüfkette (`AUDIT_LOG_ENTRIES`). Durch den Einsatz eines geheimen HMAC-Schlüssels (aus Key Vault oder Umgebung) kann die Kette selbst bei direktem Schreibzugriff auf die relationale Governance-DB nicht unbemerkt modifiziert werden.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `TierAEnabled` | `bool` | `true \| false` | `true` | Tier-A Auditierung: Synchrones Schreiben sensibler Zugriffe mit HMAC-SHA256-Verkettung. |
| `TierBAggregationWindowSeconds` | `int` | `1 .. 3600` | `60` | Aggregationsintervall für unkritische Tier-B Massenzugriffe. |
| `AuditLogRetentionDays` | `int` | `1 .. 7300` | `3650` (10 Jahre) | Gesetzliche Aufbewahrungsfrist für Prüfprotokolle. |
| `VerifyHashChainIntervalHours` | `int` | `1 .. 168` | `24` | Zyklische Integritätsprüfung der gesamten Prüfkette im Hintergrund mit timing-sicherem `FixedTimeEquals`. |
| `HmacSecretKeyVaultRef` | `string` | Secret-Name | `"GQL-GATEWAY-AUDIT-HMAC-SECRET"` | Key Vault Referenz für den geheimen HMAC-Schlüssel der Audit-Kette. Outside Development, must resolve to at least 32 UTF-8 bytes (recommended: Base64 of >= 32 random bytes). |
| `ElasticsearchSinkUrl` | `string` | URL | `""` | Optionaler sekundärer Sink für SIEM-Systeme (Splunk / Elasticsearch). |

```json
"Audit": {
  "TierAEnabled": true,
  "TierBAggregationWindowSeconds": 60,
  "AuditLogRetentionDays": 3650,
  "VerifyHashChainIntervalHours": 24,
  "HmacSecretKeyVaultRef": "GQL-GATEWAY-AUDIT-HMAC-SECRET",
  "ElasticsearchSinkUrl": "https://siem.corp.local:9200"
}
```

---

### 2.9 `ReverseProxy` (Forwarded Headers & Proxy-Netzwerke)

Schützt vor IP-Spoofing hinter Load Balancern (K8s Ingress, F5, Envoy, NGINX).

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `true` | Aktiviert die Auswertung von `X-Forwarded-For` und `X-Forwarded-Proto`. |
| `KnownNetworks` | `List<string>` | CIDR-Notation | `["127.0.0.1/32", "::1/128"]` | Vertrauenswürdige IP-Netzwerke (z. B. internes Pod-Subnetz). |
| `KnownProxies` | `List<string>` | IP-Adressen | `[]` | Feste IP-Adressen vorgelagerter Reverse Proxies. |

```json
"ReverseProxy": {
  "Enabled": true,
  "KnownNetworks": [
    "127.0.0.1/32",
    "::1/128",
    "10.244.0.0/16"
  ],
  "KnownProxies": [
    "10.0.1.50"
  ]
}
```

---

### 2.10 `OpenMetadata` (Governance-Katalog-Synchronisation & Webhooks)

Automatische Synchronisation von Schema-Metadaten, Klassifikations-Tags (`PII.*`) und Ownership aus OpenMetadata.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Aktiviert den OpenMetadata-Sync-Background-Worker. |
| `ServerUrl` | `string` | URL | `"http://localhost:8585/api/v1"` | Basis-URL der OpenMetadata REST API. |
| `AuthToken` | `string` | JWT / Token | `""` | Bearer-Token für die OpenMetadata API. |
| `WebhookSecret` | `string` | Secret | `""` | HMAC-SHA256 Secret zur Validierung eingehender Webhooks unter `/api/webhooks/openmetadata`. |
| `ServiceFilter` | `string` | Service-Name | `""` | Filtert Tabellen auf einen bestimmten Datenservice. |
| `SyncIntervalMinutes` | `int` | `1 .. 1440` | `30` | Intervall des Hintergrund-Pollings. |
| `TagToMaskingRuleMap` | `Map` | Tag -> MaskingType | *Siehe unten* | Zuordnung von OpenMetadata Klassifikations-Tags zu Maskierungsregeln. |
| `TeamToGroupSidMap` | `Map` | Team -> SID | `{}` | Übersetzung von OpenMetadata Teams in Active Directory Gruppen-SIDs. |
| `UserToUserSidMap` | `Map` | User -> SID | `{}` | Übersetzung von OpenMetadata Usernames in Windows User-SIDs. |

```json
"OpenMetadata": {
  "Enabled": true,
  "ServerUrl": "https://openmetadata.corp.local/api/v1",
  "AuthToken": "eyJhbGciOi...",
  "WebhookSecret": "OM-WEBHOOK-HMAC-SECRET-2026",
  "ServiceFilter": "enterprise_dw",
  "SyncIntervalMinutes": 30,
  "TagToMaskingRuleMap": {
    "PII.Sensitive": "REDACT",
    "PII.Email": "MASK_EMAIL",
    "PII.Pseudonym": "HMAC_SHA256",
    "PersonalData.Personal": "REDACT"
  },
  "TeamToGroupSidMap": {
    "FinanceAnalytics": "S-1-5-21-5001",
    "DataScienceTeam": "S-1-5-21-5002"
  },
  "UserToUserSidMap": {
    "john.doe": "S-1-5-21-1001"
  }
}
```

> [!IMPORTANT]
> **Webhook Replay-Schutz (`/api/webhooks/openmetadata`)**:
> - Eingehende Webhooks erfordern zwingend eine gültige HMAC-SHA256-Signatur im Header `X-OpenMetadata-Signature` (geprüft via `FixedTimeEquals`).
> - Webhook-Payloads müssen zwingend die Felder `id` (eindeutige GUID/ID) und `timestamp` (Unix-Millisekunden) enthalten.
> - Anfragen mit fehlenden Feldern, verarbeiteten IDs (Deduplizierung) oder Zeitstempeln außerhalb des 5-Minuten-Gleitzeitfensters werden fail-closed mit `HTTP 401/400` abgewiesen.

---

### 2.11 `Plugins` (Isolierte C#-Konnektoren)

Verwaltet dynamische C#-Erweiterungen (`IHttpDataSourcePlugin`) in isolierten `AssemblyLoadContext`-Instanzen.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Directory` | `string` | Verzeichnispfad | `"plugins"` | Relativer oder absoluter Pfad zum Plugin-Ordner mit DLLs. |
| `EnableHotReload` | `bool` | `true \| false` | `false` | Ermöglicht das Neuladen von Plugins zur Laufzeit ohne Neustart. |

```json
"Plugins": {
  "Directory": "/var/gql-gateway/plugins",
  "EnableHotReload": false
}
```

---

### 2.12 `DataSources.Http` (SSRF-Schutz & Egress-Sicherheit für REST)

Für deklarative HTTP-Datenquellen (`DeclarativeHttpDataSourceExecutor`) gelten strikte Zero-Trust Egress-Vorgaben zum Schutz vor Server-Side Request Forgery (SSRF):

- **DNS- & IP-Validierung**: Vor jedem HTTP-Aufruf wird der Ziel-Hostname per DNS aufgelöst. Loopback-Adressen (`127.0.0.0/8`, `::1`), Link-Local (`169.254.0.0/16`, `fe80::/10`) und private Netze nach RFC 1918 (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`) werden abgewiesen.
- **Cloud-Metadaten-Schutz**: Hostnamen wie `metadata.google.internal` und `kubernetes.default.svc` sind explizit gesperrt.
- **Hop-für-Hop Redirect-Prüfung**: Automatisches Folgen von HTTP-Weiterleitungen ist im HTTP-Client deaktiviert (`AllowAutoRedirect = false`). Bei Statuscodes 301, 302, 307 und 308 führt der Executor eine schrittweise Re-Validierung des `Location`-Headers durch (maximal 5 Hops), um SSRF über offene Weiterleitungen auszuschließen.

---

### 2.13 `Catalog` (Enterprise Data Catalog Integration & DSGVO-Klassifizierung)

Ermöglicht die zentrale Anbindung an externe Unternehmens-Datenkataloge (**Microsoft Purview**, **Collibra**, **Alation**, **OpenMetadata**) zur automatisierten Spiegelung von Metadaten und Sensitivitäts-Klassifizierungen.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Enabled` | `bool` | `true \| false` | `false` | Aktiviert die Data-Catalog-Integration. |
| `Provider` | `string` | `OpenMetadata \| MicrosoftPurview \| Collibra \| Alation` | `"OpenMetadata"` | Aktiver Datenkatalog-Provider. |
| `SyncMode` | `string` | `Mirror \| Reference` | `"Mirror"` | `Mirror`: Synct Tabellen/Spalten periodisch in den lokalen Store. `Reference`: Führt On-Demand-Lookups durch. |
| `SyncIntervalMinutes` | `int` | `1 .. 1440` | `60` | Synchronisationsintervall des Hintergrunddienstes in Minuten. |
| `TagToMaskingRuleMap` | `Dictionary<string, string>` | Key-Value Paare | *(Standard-Map)* | Mappt Katalog-Tags auf Maskierungsregeln (`REDACT`, `MASK_EMAIL`, `HMAC_SHA256`). |
| `GdprArticle9Tags` | `List<string>` | Tag-Namen | *(Art. 9 Tags)* | Tags für besondere Kategorien (Gesundheit, Biometrie, Genetik, Religion). Erzwingt `HIGH`, Four-Eyes und `REDACT`. |
| `PiiTags` | `List<string>` | Tag-Namen | *(PII Tags)* | Tags für personenbezogene Daten. |

> [!NOTE]
> **Sensitivity Classification Ranking (D-4 & ADR-010)**:
> The gateway evaluates table sensitivity across a defined ratchet ranking:
> `PUBLIC` / `LOW` (rank 0) < `NORMAL` / `INTERNAL` (rank 1) < `MEDIUM` (rank 2) < `CONFIDENTIAL` (rank 3) < `HIGH` (rank 4) < `RESTRICTED` / `SECRET` (rank 5).
> - **High-Sensitivity Threshold**: A table is treated as highly sensitive (`Table.IsSensitivityHigh = true` / `IsHighlySensitive`) if its sensitivity is **`CONFIDENTIAL` or higher** (rank >= 3), or if it has an unknown classification (fail-closed, e.g. `PII`).
> - Tables classified as `MEDIUM` are **not** treated as high-sensitivity.
> - High-sensitivity triggers mandatory Four-Eyes approval (`RequiresFourEyes = true`), shorter consent cache TTL, and fail-closed behavior in degraded mode.

#### Provider-Konfigurationen:
- **`Catalog.Purview`**: Azure Purview / Apache Atlas (`Endpoint`, `TenantId`, `ClientId`, `ClientSecretKeyVaultRef`).
- **`Catalog.Collibra`**: Collibra Data Intelligence Cloud Core API v2 (`BaseUrl`, `Username`, `PasswordKeyVaultRef`).
- **`Catalog.Alation`**: Alation Integration API v2 (`BaseUrl`, `ApiRefreshTokenKeyVaultRef`, `DefaultDataSourceId`).

```json
"Catalog": {
  "Enabled": true,
  "Provider": "MicrosoftPurview",
  "SyncMode": "Mirror",
  "SyncIntervalMinutes": 60,
  "Purview": {
    "Endpoint": "https://corp-purview.purview.azure.com",
    "TenantId": "72f988bf-86f1-41af-91ab-2d7cd011db47",
    "ClientId": "a820c78a-f326-4d1d-91b4-2195f1342618",
    "ClientSecretKeyVaultRef": "PURVIEW-SP-SECRET"
  },
  "TagToMaskingRuleMap": {
    "PII.Sensitive": "REDACT",
    "PII.Email": "MASK_EMAIL",
    "PII.Pseudonym": "HMAC_SHA256"
  },
  "GdprArticle9Tags": [
    "GDPR.Article9", "Art9", "HealthData", "Biometric", "Genetic",
    "ReligiousBelief", "TradeUnionMembership", "SexLife", "PoliticalOpinion"
  ]
}
```

---

### 2.14 `Insecure` (Pragmatisches Onboarding & Fremdsystem-Anbindung)

Für schnelle PoCs, Integrationstests, externe Webhook-Systeme oder Third-Party-Konnektoren können Sicherheitsprüfungen per Konfiguration gelockert werden. Um Risiken transparent zu machen, sind alle Parameter zwingend nach Sicherheitsauswirkung präfixiert:

- **`warn_` (Mittlerer Impact)**: Lockert Limits und Netzwerkschutz.
- **`danger_` (Kritischer Impact)**: Deaktiviert Authentifizierung, Autorisierung oder Zertifikatsprüfungen vollständig.

> [!CAUTION]
> **Production & Staging Warning (DANGER Switches & R-API-1 Migration Notice)**:
> All `danger_`-prefixed switches, as well as flags reclassified as `DANGER:` (such as `warn_fallback_default_tenant_for_webhooks`, `warn_allow_unmasked_ai_access`, `warn_mock_external_systems_if_unreachable`, `warn_auto_approve_access_requests`, `warn_disable_rate_limiting`, `warn_allow_unsigned_s3_requests`, `warn_ignore_webhook_timestamp_tolerance`, `Catalog.OpenSchema`, `OpenMetadata.AutoCreateConsents`, and `WebSql.MaxAffectedRows <= 0` with DML enabled), **strictly abort application startup outside of the `Development` environment** (`ValidationException` fail-fast).
> 
> **Migration Notice (R-API-1 / API-1)**:
> Setting `Itsm.LegacyGlobalWebhookSecret = true` is classified as `DANGER:` and strictly prevents application startup outside `Development`. Deployments relying on the legacy shared global ITSM webhook secret will not boot in Staging or Production. Operators must configure instance-specific webhook secrets (`itsm:webhook-secret:<instanceId>`) via Key Vault or environment variables.

| Eigenschaft | Typ | Standard | Sicherheits-Level | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `danger_allow_anonymous_access` | `bool` | `false` | **CRITICAL** | Erlaubt GraphQL-Abfragen ohne jegliche Authentifizierung (anonymer Benutzer). |
| `danger_bypass_consent_checks` | `bool` | `false` | **CRITICAL** | Umgeht die Zero-Trust Consent-Prüfung (`ALLOW` für alle Tabellen). |
| `danger_bypass_webhook_signature_validation` | `bool` | `false` | **CRITICAL** | Erlaubt ungesignete Webhook-Aufrufe (z. B. ServiceNow, Jira, OpenMetadata ohne HMAC-Prüfung). |
| `danger_allow_untrusted_certificates` | `bool` | `false` | **CRITICAL** | Akzeptiert selbstsignierte oder abgelaufene SSL/TLS-Zertifikate bei ausgehenden HTTP-Aufrufen (Purview, Collibra, APIs). |
| `danger_allow_anonymous_webhooks` | `bool` | `false` | **CRITICAL** | Akzeptiert eingehende Webhook-Payloads ohne Auth-Token oder Secret. |
| `warn_allow_all_cors_origins` | `bool` | `false` | **WARN** | Setzt `Access-Control-Allow-Origin: *` und deaktiviert CSRF-Preflight. |
| `warn_disable_rate_limiting` | `bool` | `false` | **WARN** | Deaktiviert IP- und SID-basiertes Rate-Limiting (keine `429 Too Many Requests`). |
| `warn_relaxed_query_limits` | `bool` | `false` | **WARN** | Deaktiviert AST-Depth- und Complexity-Limits für tief verschachtelte Abfragen. |

```json
"Insecure": {
  "warn_allow_all_cors_origins": true,
  "warn_disable_rate_limiting": true,
  "danger_bypass_webhook_signature_validation": false,
  "danger_allow_untrusted_certificates": false
}
```

---

### 2.14a `Dev` (Development-only switches)

`Gateway:Dev` groups the development conveniences (class A) and development security switches (class B). Security bypasses stay in `Insecure` (class C). Everything in `Dev` is **Development only**: outside Development a non-default value is a startup error. Detailed developer guide: [developer-guide.md#3-developer-options-gatewaydev](developer-guide.md#3-developer-options-gatewaydev).

| Property | Type | Default | Class | Description |
| :--- | :--- | :--- | :--- | :--- |
| `Preset` | `string` | `Standard` | A/B | `Standard`, `Quickstart` (adds `warn_allow_all_cors_origins`, `warn_enable_introspection`, `warn_auto_approve_access_requests`, `OpenSchema`) or `Strict` (no test auth, no introspection). Replaces the deprecated `Gateway:Profile`. |
| `Banner` | `bool` | `true` | A | Startup banner with links and personas. |
| `VerboseErrors` | `bool` | `true` | A | Diagnostic `problem+json` for 403 and unhandled exceptions. |
| `PersonaLogin` | `bool` | `true` | A | `/api/dev/personas` and `/api/dev/login/{persona}`. |
| `Info` | `bool` | `true` | A | `/api/dev/info` (effective configuration without secrets). |
| `DemoData` | `bool?` | preset | A | Seed demo catalog data. Alias of `GovernanceDb.SeedDemoData`. |
| `Persist:Enabled` | `bool` | `false` | A | Keep the governance database in `<Persist:Directory>/dev.db`. |
| `Persist:Directory` | `string` | `.data` | A | Directory of the development database and its audit anchor. Reset with `scripts/dev-reset.sh`. |
| `TestAuthHandler` | `bool?` | preset | B | Header-based test identities. Alias of `Authentication.EnableTestAuthHandler`. |
| `Tooling:BananaCakePop` | `bool?` | preset | A | Alias of `GraphQL.EnableBananaCakePop` in Development. |
| `Tooling:Introspection` | `bool?` | preset | B | Alias of `GraphQL.EnableIntrospection` in Development. |

Precedence per switch: explicit `Dev` value, then an explicitly set legacy key (deprecated, logged at startup), then the preset default. A `Dev` value that contradicts its legacy key fails the start. Do not set the legacy keys in the base `appsettings.json`: an explicit value there defeats the preset default. `GraphQL.EnableIntrospection` and `GraphQL.EnableBananaCakePop` remain regular options in every environment; only the `Dev:Tooling:*` aliases are Development-only.

---

### 2.15 `Identity & Multi-Tenant / M2M Service-Accounts`

Unterstützt hybride Identitätsmigration und Machine-to-Machine-Zugriffe für Hintergrund-Jobs:

- **Abstraktionsschicht (`IIdentityProvider`)**: Ermöglicht den parallelen Betrieb von On-Prem-Active-Directory (Kerberos) und Microsoft Entra ID (Azure AD / OIDC) ohne Code-Änderungen an Fachkomponenten.
- **Service-Accounts & M2M-Zugriff**: Authentifizierung via OAuth2 Client-Credentials oder mutual TLS (mTLS). Im Gateway wird der Aufrufer als Dienst-Prinzipal mit `SP-<client_id>` SID geführt und erhält dedizierte, zeitlich befristete Consents mit technischer Begründung.
- **Multi-Tenant Datenisolation**: Zweistufige Mandantentrennung über SQL-Pushdown (`WHERE tenant_id = @tenant`) und native PostgreSQL Row-Level-Security mittels transaktionalem `SET LOCAL app.tenant_id = @tenant`.

---

### 2.16 `Mcp` (Enterprise Model Context Protocol Server & AI Data Guardrails)

Exponiert autorisierte GraphQL-Persisted-Queries als typisierte Tools für autonome KI-Agenten (Anthropic Claude, AutoGen, LangChain) über standardkonforme Server-Sent Events (SSE) und JSON-RPC 2.0.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Mcp:Enabled` | `bool` | `true \| false` | `false` | Aktiviert den MCP-Server (Streamable HTTP unter `Mcp:EndpointPath`, offizielles MCP-C#-SDK, zustandslos). |
| `Mcp:EndpointPath` | `string` | URL-Pfad | `"/mcp"` | Pfad des MCP-Endpunkts (Streamable HTTP). |
| `Mcp:MaxTokensPerCall` | `int` | `256 .. 128000` | `4096` | Maximales Token-Budget pro Tool-Aufruf; verhindert Context-Window-Overflows. |
| `Mcp:MaxResultRows` | `int` | `1 .. 10000` | `100` | Maximale Ergebniszeilen pro Datenabfrage. |
| `Mcp:RequirePiiMasking` | `bool` | `true \| false` | `true` | Automatisches Scrubbing von PII- (E-Mail, IBAN) und DSGVO-Art.-9-Daten vor Übermittlung an LLMs. |
| `Mcp:AllowedOperations` | `string[]` | GraphQL Operationen | `[]` | Whitelist freigegebener Abfragen. |

> `warn_allow_unmasked_ai_access` und `danger_bypass_mcp_auth` liegen ausschließlich unter `Insecure` (ADR-012, Phase 4).

Der Server arbeitet zustandslos: Jede Anfrage wird mit der Identität ihrer eigenen HTTP-Anfrage ausgeführt, es gibt keine MCP-Sitzungen und keinen alten SSE-Transport (`/mcp/sse`, `/mcp/message`) mehr. Sind Entra ID oder AD FS aktiviert, veröffentlicht das Gateway unter `/.well-known/oauth-protected-resource<EndpointPath>` die OAuth-Metadaten (RFC 9728) mit diesen Ausstellern als Autorisierungsserver; 401-Antworten des MCP-Endpunkts verweisen per `WWW-Authenticate: Bearer resource_metadata=…` darauf. MCP-Clients finden so ohne weitere Konfiguration heraus, wo sie ein Token bekommen.

Die Dataset-Tools `list_datasets`, `describe_dataset`, `query_graphql` und `sample_rows` ([F-AI-11](features/f-ai-11-mcp-dataset-tools.md)) sind immer registriert und brauchen keinen Eintrag in `AllowedOperations`. Agenten fragen Daten bevorzugt mit `query_graphql` ab. Ist `Casbin:Enabled` gesetzt, prüft Casbin die MCP-Tools zusätzlich: `list_datasets` auf dem Objekt `governance.catalog.datasets`, `describe_dataset` und `sample_rows` auf der angefragten Tabelle, `query_graphql` auf jeder Tabelle der Abfrage. Ohne aktives Casbin entfällt diese Prüfung; Consent, Zeilenfilter und Maskierung gelten in jedem Fall.

```json
"Mcp": {
  "Enabled": true,
  "EndpointPath": "/mcp",
  "MaxTokensPerCall": 4096,
  "MaxResultRows": 100,
  "RequirePiiMasking": true,
  "AllowedOperations": [
    "query_customers",
    "query_invoices",
    "query_data_catalog"
  ]
}
```

### 2.17 `DuckDbOlap` & `ArrowExport` (In-Memory OLAP & Arrow IPC Streaming)

| Schlüssel | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `DuckDbOlap:Enabled` | `bool` | `true \| false` | `false` | Aktiviert die eingebettete In-Memory DuckDB-Vektorengine (`F-DATA-03`). |
| `DuckDbOlap:MaxMemory` | `string` | z. B. `"512MB"`, `"2GB"` | `"1GB"` | Maximale RAM-Quota pro DuckDB-Session (`PRAGMA max_memory`). |
| `DuckDbOlap:MaxThreads` | `int` | `1 .. 32` | `2` | Maximale Thread-Anzahl für parallele SIMD-Ausführung. |
| `DuckDbOlap:MaxStagedRowsPerTable` | `int` | `1000 .. 5000000` | `250000` | Obergrenze gestagter Zeilen pro temporärer Quelltabelle. |
| `DuckDbOlap:MaxResultRows` | `int` | `1 ..` | `50000` | Obergrenze der Zeilen eines OLAP-Ergebnisses; ein kleineres `limit` der Anfrage bleibt erhalten. |
| `DuckDbOlap:QueryTimeoutSeconds` | `int` | `1 .. 300` | `30` | Timeout für analytische In-Memory DuckDB-Abfragen. |
| `ArrowExport:Enabled` | `bool` | `true \| false` | `false` | Aktiviert den binären Zero-Copy Apache Arrow Export (`F-DATA-04`). |
| `ArrowExport:BatchSize` | `int` | `100 .. 100000` | `10000` | Zeilenanzahl pro gestreamtem Arrow-RecordBatch. |
| `ArrowExport:MaxRowsPerExport` | `int` | `1000 .. 10000000` | `1000000` | Maximale Zeilenanzahl pro Export-Vorgang. |

```json
"DuckDbOlap": {
  "Enabled": true,
  "MaxMemory": "1GB",
  "MaxThreads": 2,
  "MaxStagedRowsPerTable": 250000,
  "QueryTimeoutSeconds": 30
},
"ArrowExport": {
  "Enabled": true,
  "BatchSize": 10000,
  "MaxRowsPerExport": 1000000
}
```


### 2.18 `Casbin` (ABAC/RBAC Policy Engine & Model-Contract)

Das Gateway integriert Casbin für feingranulare Autorisierungs- und Row-Level-Security-Regeln (ABAC/RBAC). Richtlinien und Modell können als Dateien hinterlegt oder mit dem integrierten Standardmodell betrieben werden.

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `Casbin:Enabled` | `bool` | `true \| false` | `false` | Aktiviert das Casbin-Enforcement im Gateway. Standard ist `false`. |
| `Casbin:EnforceInQueryPipeline` | `bool` | `true \| false` | `true` | Führt Casbin-Prüfungen in der Query-Pipeline aus. |
| `Casbin:ModelPath` | `string?` | Dateipfad | `null` | Pfad zur Casbin-Modell-Datei (`.conf`). **Mandatory whenever `Casbin:Enabled = true`**. Wird gegen den Model Contract validiert, sobald gesetzt (auch wenn `Enabled = false`). |
| `Casbin:PolicyPath` | `string?` | Dateipfad | `null` | Pfad zur globalen Casbin-Policy-Datei (`.csv`). Mandatory when `Casbin:Enabled = true`; must exist, not be empty, and contain at least one valid `p` rule. |
| `Casbin:WatchPolicyFile` | `bool` | `true \| false` | `true` | Überwacht die Policy-Datei auf Änderungen zur Laufzeit (Hot Reload). |

```json
"Casbin": {
  "Enabled": true,
  "EnforceInQueryPipeline": true,
  "ModelPath": "config/casbin-model.conf",
  "PolicyPath": "config/casbin-policy.csv",
  "WatchPolicyFile": true
}
```

#### Startup Validation & Model Contract (Probes M1–M8, W1–W6)

To prevent security vulnerabilities from model misconfigurations (such as syntax errors, parentheses/operator precedence bugs like `g(r.sub, p.sub) && r.tenant == p.tenant || p.tenant == "*"` bypassing subject and object checks, or missing `sub_rule` evaluation), the gateway validates Casbin models **by behavior on an isolated throw-away enforcer** rather than superficial text matching (`CasbinModelContract.Verify`):

1. **Mandatory Probes (M1–M8)**:
   - **M1: Basic allow and arity of r and p** – Validates that a standard allow rule authorizes matching `(sub, tenant, obj, act)` requests and confirms that `r` and `p` parameter arity align.
   - **M2: Tenant separation** – Ensures a rule defined for `TenantA` never grants access to `TenantB`.
   - **M3: Subject check** – Ensures a rule defined for `UserA` never grants access to `UserB`.
   - **M4: Object check** – Ensures a rule defined for `Obj` never grants access to `OtherObj`.
   - **M5: Deny overrides allow (policy_effect: deny wins)** – Validates that `policy_effect` enforces deny-overrides-allow when matching allow and deny rules coincide.
   - **M6: `sub_rule` evaluation (`eval(p.sub_rule)`)** – Validates dynamic evaluation of ABAC expressions (e.g. `eval(p.sub_rule)` evaluating to false rejects access).
   - **M7: Role resolution via `g(r.sub, p.sub)`** – Confirms RBAC role inheritance resolves permissions granted to roles via grouping rules `g`.
   - **M8: Role does not grant access to unauthorized users** – Verifies that role-based permissions do not leak to non-member users.

2. **Wildcard Tenant Probes (W1 capability, W2–W6 safety when W1 is true)**:
   - **W1: Wildcard tenant capability** – Tests whether a rule with tenant `*` matches a specific tenant request. If true, `SupportsWildcardTenant` is set to `true`. If false, wildcard tenant rules are disallowed.
   - **W2: `*` does not bypass subject check** – Verifies that a wildcard tenant rule does not authorize unauthorized subjects (guards against operator precedence / parentheses bugs).
   - **W3: `*` does not bypass object check** – Verifies that a wildcard tenant rule does not authorize access to unauthorized objects.
   - **W4: Tenant-specific deny overrides `*` allow** – Ensures that a tenant-specific deny rule overrides a global `*` allow rule.
   - **W5: Global `*` deny overrides tenant-specific allow** – Confirms that a global `*` deny rule overrides a tenant-specific allow rule (deny wins across files).
   - **W6: Request with tenant `*` does not match tenant-specific rules** – Ensures that an incoming request specifying tenant `*` cannot match tenant-specific policy rules.

If any mandatory probe (M1–M8) or, when W1 is supported, any wildcard safety probe (W2–W6) fails, process startup immediately aborts fail-fast with a `ValidationException` (wrapping `CasbinModelValidationException`).

#### Policy Semantics & Tenant Policy Invariants

- **Global Wildcard (`*`) Scope**: Rules defined with tenant `*` in the global policy file apply across all tenants.
- **Cross-File Deny Precedence**: Deny decisions take precedence file-wide and across files. If any rule (global wildcard or tenant-specific) produces a deny effect, the access is rejected regardless of allows in other files.
- **Tenant Policy File Requirement (F-1)**: Each tenant policy file must contain at least one valid `p` rule. Files containing only comments, whitespace, or grouping-only rules without `p` rules are rejected fail-closed.
- **Empty Tenant Policy File Rejection (E-6)**: An empty tenant policy file is rejected whenever global `*` rules are active. The gateway preserves the last-known-good policy set to prevent fail-open wildcard inheritance.

---

### 2.19 `WebSql` (Governed SQL & Trino REST Protocol)

Autheris bietet eine integrierte, abgesicherte WebSQL-Schnittstelle, die 100% kompatibel zur Trino/Presto-SQL-Syntax und dem nativen Trino REST Client-Protokoll ist. Abfragen können über Standard-HTTP/HTTPS abgesetzt werden, ohne Datenbank-Ports nach außen zu öffnen.

| Eigenschaft | Typ | Standard | Beschreibung |
| :--- | :--- | :--- | :--- |
| `WebSql:Enabled` | `bool` | `true` | Aktiviert die WebSQL- und Trino-Statement-Endpunkte. |
| `WebSql:DefaultDataSourceName` | `string` | `"default"` | Standard-Datenquelle, wenn kein Catalog/DataSource explizit angegeben ist. |
| `WebSql:AllowedDataSources` | `string[]` | `[]` | Liste global freigegebener Datenquellen für WebSQL-Abfragen. |
| `WebSql:TenantDataSourceAllowlist` | `Dictionary<string, string[]>` | `{}` | Mandantenspezifische Einschränkung erlaubter Datenquellen. |
| `WebSql:DefaultMaxRows` | `long` | `1000` | Zeilen einer Abfrage ohne `LIMIT` (`POST /api/v1/sql`). |
| `WebSql:MaxAllowedRows` | `long` | `10000` | Obergrenze für ein explizites `LIMIT` (`0` = keine Obergrenze). |
| `WebSql:ExecutionTimeoutSeconds` | `int` | `30` | Maximaler Timeout für die Abfrageausführung im Backend. |
| `WebSql:AllowDml` | `bool` | `false` | Erlaubt schreibende Operationen (`INSERT`, `UPDATE`, `DELETE`). |
| `WebSql:DmlWriterRoles` | `string[]` | `[]` | Rollen, die DML ausführen dürfen (Pflicht, wenn `AllowDml = true`). |
| `WebSql:MaxAffectedRows` | `long` | `1000` | Maximal erlaubte Zeilenanzahl bei DML; Überschreitung triggert automatischen Rollback. |
| `WebSql:RejectUnfilteredDml` | `bool` | `true` | Verhindert ungefilterte `UPDATE`/`DELETE`-Statements (`WHERE 1=1`, `WHERE true`). |

```json
"WebSql": {
  "Enabled": true,
  "DefaultDataSourceName": "default",
  "AllowedDataSources": ["sales", "finance", "analytics"],
  "DefaultMaxRows": 1000,
  "MaxAllowedRows": 10000,
  "ExecutionTimeoutSeconds": 30,
  "AllowDml": false,
  "DmlWriterRoles": ["DatabaseOperator"],
  "MaxAffectedRows": 1000,
  "RejectUnfilteredDml": true
}
```

#### Zeilenlimits je Transport (`RowLimits`)

Alle SQL-Transporte laufen durch dieselbe WebSQL-Pipeline. Ohne eigene Werte gelten `WebSql:DefaultMaxRows` und `WebSql:MaxAllowedRows`; je Transport lässt sich jeder der beiden Werte einzeln überschreiben:

| Schlüssel | Transport | Zusätzliche Obergrenze |
|---|---|---|
| `RowLimits:Trino:DefaultMaxRows` / `:MaxAllowedRows` | Trino-Protokoll (`POST /v1/statement`) | – |
| `RowLimits:Parquet:...` | WebSQL mit `Accept: application/vnd.apache.parquet` | `ParquetEgress:MaxRowsPerFile` |
| `RowLimits:SqlEndpoints:...` | Deklarierte SQL-Endpunkte (`/api/v1/queries/...`) | – |
| `RowLimits:ArrowExport:...` | Arrow-Export (`POST /api/v1/export/arrow`) | `Arrow:MaxExportRows` |
| `RowLimits:FlightSql:...` | Arrow Flight SQL (`/api/v1/flight/sql/*`) | `Arrow:MaxExportRows` |

Ergebnisse, die das Limit erreichen, werden als gekürzt gemeldet (`truncated`, `X-Autheris-Truncated`, bei Parquet zusätzlich `X-Export-Truncated`).

```json
"RowLimits": {
  "Parquet": { "MaxAllowedRows": 1000000 },
  "Trino": { "DefaultMaxRows": 10000, "MaxAllowedRows": 100000 }
}
```

#### Trino REST Client Protokoll (`/v1/statement`) & `wait_timeout`

Autheris unterstützt das native Trino REST Client Protokoll, womit Standard-Trino-Tools (Trino CLI, Python `trino-python-client`, DBeaver, Apache Superset) direkt angebunden werden können:

- **Endpunkte:**
  - `POST /v1/statement` (oder `POST /api/v1/sql`): Nimmt die Abfrage im Request-Body entgegen (Text oder JSON).
  - `GET /v1/statement/queued/{id}` (oder `GET /api/sql/statements/{id}`): Pollt den Status langlaufender Abfragen.
  - `DELETE /v1/statement/{id}` (oder `DELETE /api/sql/statements/{id}`): Bricht eine laufende Abfrage ab (`204 No Content`).
- **Synchronous Fast-Path via `X-Trino-Wait-Timeout`:**
  - Wird ein Timeout übergeben (z. B. `X-Trino-Wait-Timeout: 5s` oder URL-Parameter `?wait_timeout=5s`) und die Abfrage beendet innerhalb dieses Fensters, antwortet das Gateway sofort mit HTTP 200 und Status `FINISHED` inklusive aller Zeilen.
- **Asynchronous Continuation Path:**
  - Benötigt die Abfrage länger als der Timeout, antwortet das Gateway sofort mit Status `RUNNING` und einer `nextUri` (`/v1/statement/queued/{id}`), um Timeouts an Load-Balancern und Proxies zu verhindern.
- **3-Teilige Bezeichner (`<catalog>.<schema>.<table>`):**
  - Tabellen können standardmäßig als `catalog.schema.table` angesprochen werden.
  - Der `catalog`-Teil wird gegen die Datenquelle validiert bzw. automatisch als Ziel-Datenquelle inferiert.
  - Der Dialekt-Generator strippt den Catalog-Präfix vor der Ausführung auf PostgreSQL (`"schema"."table"`), SQL Server (`[schema].[table]`) oder SQLite (`[table]`), wodurch Cross-Database- und DB-Escape-Kollisionen verhindert werden.
- **Header-Unterstützung:**
  - `X-Trino-Catalog`: Wählt die Ziel-Datenquelle aus.
  - `X-Trino-Schema`: Standard-Schema.
  - `X-Trino-Wait-Timeout`: Wartefenster für synchrone Fertigstellung (z. B. `5s`, `500ms`, `1m`).
  - `X-Trino-User` & `X-Trino-Source`: Identitäts- und Auditierungskontext.

### 2.20 `VirtualFilters` (Virtuelle Filter & Access Profiles)

Steuert die Verwaltung und Cluster-Synchronisation relationsbasierter Zeilenfilter ([`F-GOV-09`](features/f-gov-09-virtual-filters.md)).

| Eigenschaft | Typ | Standard | Beschreibung |
| :--- | :--- | :--- | :--- |
| `VirtualFilters:MaxRemovals` | `int` | `10` | Maximale Anzahl an Bindungen, die ein GitOps-Sync ohne `?force=true` entfernen darf. Das Entfernen erweitert die Sichtrechte des Berechtigten; `0` deaktiviert die Schranke. |
| `VirtualFilters:GenerationCheckSeconds` | `int` | `5` | Intervall in Sekunden, in dem eine Gateway-Instanz die Generation mit der Datenbank abgleicht. `0` prüft bei jedem Zugriff. |
| `RowFilter:SubqueryStrategy` | `enum` | `Exists` | SQL-Strategie für RLS-Unterabfragen: `Exists` (`EXISTS (SELECT 1 ...)`), `InCorrelated` oder `In`. |
| `Logging:LogGeneratedSql` | `bool` | `false` | Diagnoseschalter zur Protokollierung generierter Ziel-SQL-Abfragen inklusive RLS- und Virtual-Filter-Prädikate. |

```json
"VirtualFilters": {
  "MaxRemovals": 10,
  "GenerationCheckSeconds": 5
},
"RowFilter": {
  "SubqueryStrategy": "Exists"
},
"Logging": {
  "LogGeneratedSql": false
}
```

---




## 3. Deployment & Umgebungsvariablen

```bash
# ASP.NET Core Hosting & Environment
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:5000

# High Availability
Gateway__HighAvailability__DrainDelaySeconds=5
Gateway__HighAvailability__QueryTimeoutSeconds=30
Gateway__HighAvailability__ShutdownTimeoutSeconds=40
Gateway__HighAvailability__TerminationGracePeriodSeconds=60

# Authentifizierung & Sicherheit
Gateway__Authentication__Domain=CORP.LOCAL
Gateway__Authentication__ServicePrincipalName=HTTP/gateway.corp.local
Gateway__Authentication__RequireKerberosOnly=true
Gateway__Authentication__EnableTestAuthHandler=false

# Governance-Datenbank
Gateway__GovernanceDb__Provider=PostgreSql
Gateway__GovernanceDb__ConnectionString="Host=pg-ha.corp.local;Database=governance;Username=autheris_app;Password=<secret>;SSL Mode=VerifyFull"

# Caching & Redis Cluster
Gateway__Caching__Redis__Configuration="redis-ha.corp.local:6379,abortConnect=false,ssl=true,password=SecretRedisPass!"
Gateway__Caching__EpochValidation__FailClosedOnSensitiveTables=true

# Secrets & Schlüssel
Gateway__DataMasking__HmacKeyId=key-2026-q1
Gateway__DataMasking__HmacSecretKeyVaultRef=GQL-HMAC-SECRET-KEY

# GraphQL & Origin-Sicherheit
Gateway__GraphQL__EnableIntrospection=false
Gateway__GraphQL__EnableBananaCakePop=false
Gateway__GraphQL__TrustedOrigins__0=https://bi.corp.local
Gateway__GraphQL__TrustedOrigins__1=https://portal.corp.local

# OpenMetadata
Gateway__OpenMetadata__Enabled=true
Gateway__OpenMetadata__ServerUrl=https://openmetadata.corp.local/api/v1
Gateway__OpenMetadata__AuthToken=eyJhbGciOi...
Gateway__OpenMetadata__WebhookSecret=MyWebhookHmacSecretKey
```

### 3.2 Beispiel Kubernetes Deployment & ConfigMap

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: gql-gateway
  namespace: data-governance
spec:
  replicas: 3
  selector:
    matchLabels:
      app: gql-gateway
  template:
    metadata:
      labels:
        app: gql-gateway
    spec:
      terminationGracePeriodSeconds: 60
      containers:
        - name: gateway
          image: registry.corp.local/gql-gateway:latest
          ports:
            - containerPort: 5000
          resources:
            requests:
              cpu: "1000m"
              memory: "1Gi"
            limits:
              cpu: "4000m"
              memory: "2Gi"
          lifecycle:
            preStop:
              exec:
                command: ["/bin/sh", "-c", "sleep 5"]
          livenessProbe:
            httpGet:
              path: /health/live
              port: 5000
            initialDelaySeconds: 5
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 5000
            initialDelaySeconds: 5
            periodSeconds: 5
          envFrom:
            - configMapRef:
                name: gql-gateway-config
            - secretRef:
                name: gql-gateway-secrets
```

### 3.3 Traefik Ingress & ForwardAuth Integration (Kubernetes)

Wird Autheris in Kubernetes hinter **Traefik** betrieben, übernimmt Traefik die Authentifizierung (z. B. via Authelia, Keycloak oder Authentik) und leitet die verifizierten Identitätsmerkmale an Autheris weiter.

#### 3.3.1 Traefik ForwardAuth Middleware

```yaml
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata:
  name: forward-auth
  namespace: data-governance
spec:
  forwardAuth:
    address: https://auth.corp.local/api/verify
    trustForwardHeader: true
    authResponseHeaders:
      - X-Forwarded-User
      - X-Forwarded-Email
      - X-Forwarded-Groups
      - X-Forwarded-Roles
      - X-Forwarded-Tenant
```

> [!SECURITY]
> **SEC H-1 Tenant-Isolation**: `X-Forwarded-Tenant` wird von Autheris standardmäßig ignoriert (`TrustUpstreamTenant = false`), um Header-Spoofing durch Clients zu verhindern. Falls der Reverse Proxy den Tenant setzt, muss `Authentication:ForwardAuth:TrustUpstreamTenant = true` und eine explizite Positivliste `Authentication:ForwardAuth:AllowedTenantIds` konfiguriert werden. Ist `AllowedTenantIds` leer, schlägt jede Authentifizierung fail-closed fehl.

#### 3.3.2 Traefik Shared-Secret Middleware (Anti-Spoofing)

```yaml
apiVersion: traefik.io/v1alpha1
kind: Middleware
metadata:
  name: gateway-shared-secret
  namespace: data-governance
spec:
  headers:
    customRequestHeaders:
      X-Forwarded-Secret: "OM-SHARED-SECRET-TRAEFIK-TO-GATEWAY"
```

#### 3.3.3 Traefik IngressRoute

```yaml
apiVersion: traefik.io/v1alpha1
kind: IngressRoute
metadata:
  name: gql-gateway-ingress
  namespace: data-governance
spec:
  entryPoints:
    - websecure
  routes:
    - match: Host(`graphql.corp.local`) && PathPrefix(`/graphql`)
      kind: Rule
      middlewares:
        - name: forward-auth
        - name: gateway-shared-secret
      services:
        - name: gql-gateway
          port: 5000
  tls:
    secretName: corp-wildcard-tls
```

---

## 4. Konfigurationsprofile im Vergleich

| Parameter | Development (`appsettings.Development.json`) | Production (`appsettings.Production.json`) |
| :--- | :--- | :--- |
| `Logging:LogLevel:Default` | `Debug` | `Information` / `Warning` |
| `Authentication:EnableTestAuthHandler` | `true` (erlaubt Test-Header) | `false` (**Zwingend vorgeschrieben**) |
| `Authentication:RequireKerberosOnly` | `false` | `true` |
| `Authentication:ForwardAuth:Enabled` | `false` / `true` für Tests | `true` (hinter K8s Traefik Ingress) |
| `Authentication:ForwardAuth:RequireTrustedProxy` | `false` | `true` (Validiert Traefik Pod CIDRs) |
| `GovernanceDb:Provider` | `Sqlite` (In-Memory `:memory:`) | `SqlServer` oder `PostgreSql` |
| `Caching:EpochValidation:FailClosed` | `false` | `true` (Zero-Trust Fail-Closed) |
| `GraphQL:EnableIntrospection` | `true` | `false` |
| `GraphQL:EnableBananaCakePop` | `true` (Nitro IDE im Browser) | `false` |
| `GraphQL:PersistedQueriesOnly` | `false` | `true` (empfohlen) |
| `DataMasking:HmacSecretKeyVaultRef` | `"DEV_INSECURE_TEST_KEY_ONLY"` | Key Vault Secret Referenz (**Validiert**) |
| `Audit:TierBAggregationWindowSeconds` | `5` Sekunden | `60` Sekunden |

---

## 5. Checkliste für den Produktions-Rollout

Vor Freigabe einer neuen Produktivumgebung sind folgende Punkte zu verifizieren:

- [ ] **Auth-Sicherheit**: `Gateway:Authentication:EnableTestAuthHandler` steht auf `false`.
- [ ] **ForwardAuth / Traefik Trust**: Bei Kubernetes-Betrieb ist `RequireTrustedProxy = true` gesetzt, `TrustedNetworks` enthält nur die Traefik Ingress Pod-CIDR und `SharedSecretKeyVaultRef` ist konfiguriert.
- [ ] **Kryptografie**: `Gateway:DataMasking:HmacSecretKeyVaultRef` verweist auf ein valides Key Vault Secret und nicht auf Dev-Defaults.
- [ ] **Introspektion**: `Gateway:GraphQL:EnableIntrospection` und `EnableBananaCakePop` sind auf `false` gesetzt.
- [ ] **Zero-Trust Fail-Closed**: `Gateway:Caching:EpochValidation:FailClosedOnSensitiveTables` ist auf `true`.
- [ ] **Anti-CSRF & CORS**: `Gateway:GraphQL:TrustedOrigins` enthält nur verifizierte Domänen (kein Wildcard `*` in Produktion!).
- [ ] **High Availability**: K8s `terminationGracePeriodSeconds` ist größer als `DrainDelaySeconds + ShutdownTimeoutSeconds + 10s`.
- [ ] **Proxy-Sicherheit**: `Gateway:ReverseProxy:KnownNetworks` schränkt vertrauenswürdige IPs auf tatsächliche Ingress-Controller ein.
- [ ] **Echte Datenquellen**: `Gateway:DataSources` enthält valide ConnectionStrings für produktive Fachdatenbanken.
