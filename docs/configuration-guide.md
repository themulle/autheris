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
| `RequireKerberosOnly` | `bool` | `true \| false` | `true` | Erzwingt Kerberos und lehnt unsichere NTLM-Downgrades ab. |
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
| `SharedSecret` | `string` | Geheimes Token | `""` | Optionales direktes Shared Secret für Test- oder Staging-Umgebungen. |
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
> - Passwörter müssen das PBKDF2-Format aufweisen: `$pbkdf2$<iterations>$<salt>$<hash>` (z. B. `$pbkdf2$100000$c2FsdHNhbHQ=$...`).
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
        "Password": "$pbkdf2$100000$ZXhhbXBsZXNhbHQxMjM0NQ==$dGVzdGhhc2hiYXNlNjQ=",
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

Konfiguriert echte relationale Datenbank-Backends für die abgefragten Fachdaten. Das Gateway unterstützt über `ISqlConnectionFactory` die Provider `"SqlServer"`, `"PostgreSql"`, `"Sqlite"`, `"Oracle"` und `"Databricks"`. 

Im Gegensatz zu synthetischen Stubs führt der `SqlDataSourceExecutor` echte SQL-Queries aus und **pushed Row-Level Security (RLS) Filter direkt als WHERE-Klausel in die Datenbank**:

| Eigenschaft | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `DataSources:{sourceName}:Provider` | `string` | `"SqlServer"`, `"PostgreSql"`, `"Sqlite"` | `"SqlServer"` | Datenbank-Treiber für die Ziel-Datenquelle. |
| `DataSources:{sourceName}:ConnectionString` | `string` | ADO.NET ConnStr | `""` | Verbindungszeichenfolge zur Zieldatenbank. |

```json
"DataSources": {
  "finance": {
    "Provider": "SqlServer",
    "ConnectionString": "Server=sql-finance.corp.local;Database=FinanceDb;Integrated Security=SSPI;TrustServerCertificate=true;"
  },
  "hr": {
    "Provider": "PostgreSql",
    "ConnectionString": "Host=pg-hr.corp.local;Port=5432;Database=HrDb;Username=gql_app;Password=SuperSecretPass!;SSL Mode=Require;"
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
| `HmacSecretKeyVaultRef` | `string` | Secret-Name | `"DEV_INSECURE_...` | Name des Secrets in Azure Key Vault / HashiCorp Vault. |
| `MaskingCacheTtlHours` | `int` | `1 .. 168` | `24` | Gültigkeitsdauer des Caches für vorberechnete Maskierungsregeln. |

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
| `HmacSecretKeyVaultRef` | `string` | Secret-Name | `"GQL-GATEWAY-AUDIT-HMAC-SECRET"` | Key Vault Referenz für den geheimen HMAC-Schlüssel der Audit-Kette. |
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
> **Produktions-Warnung**: Alle `danger_`- und `warn_`-Flags müssen in Produktionsumgebungen auf `false` stehen. Bei aktiviertem `danger_`-Flag loggt das Gateway auffällige `CRITICAL`-Sicherheitswarnungen.

| Eigenschaft | Typ | Standard | Sicherheits-Level | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `danger_allow_anonymous_queries` | `bool` | `false` | **CRITICAL** | Erlaubt GraphQL-Abfragen ohne jegliche Authentifizierung (anonymer Benutzer). |
| `danger_bypass_authorization` | `bool` | `false` | **CRITICAL** | Umgeht die Zero-Trust Consent-Prüfung (`ALLOW` für alle Tabellen). |
| `danger_bypass_webhook_signature_validation` | `bool` | `false` | **CRITICAL** | Erlaubt ungesignete Webhook-Aufrufe (z. B. ServiceNow, Jira, OpenMetadata ohne HMAC-Prüfung). |
| `danger_allow_untrusted_certificates` | `bool` | `false` | **CRITICAL** | Akzeptiert selbstsignierte oder abgelaufene SSL/TLS-Zertifikate bei ausgehenden HTTP-Aufrufen (Purview, Collibra, APIs). |
| `danger_allow_anonymous_webhooks` | `bool` | `false` | **CRITICAL** | Akzeptiert eingehende Webhook-Payloads ohne Auth-Token oder Secret. |
| `warn_allow_all_cors_origins` | `bool` | `false` | **WARN** | Setzt `Access-Control-Allow-Origin: *` und deaktiviert CSRF-Preflight. |
| `warn_disable_rate_limiting` | `bool` | `false` | **WARN** | Deaktiviert IP- und SID-basiertes Rate-Limiting (keine `429 Too Many Requests`). |
| `warn_bypass_query_cost_limits` | `bool` | `false` | **WARN** | Deaktiviert AST-Depth- und Complexity-Limits für tief verschachtelte Abfragen. |

```json
"Insecure": {
  "warn_allow_all_cors_origins": true,
  "warn_disable_rate_limiting": true,
  "danger_bypass_webhook_signature_validation": false,
  "danger_allow_untrusted_certificates": false
}
```

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
| `Mcp:Enabled` | `bool` | `true \| false` | `false` | Aktiviert den nativen MCP-Server (`/mcp/sse`, `/mcp/message`). |
| `Mcp:EndpointPath` | `string` | URL-Pfad | `"/mcp"` | Basis-Endpunkt für den MCP SSE-Handshake und Message-Endpunkt. |
| `Mcp:MaxTokensPerCall` | `int` | `256 .. 128000` | `4096` | Maximales Token-Budget pro Tool-Aufruf; verhindert Context-Window-Overflows. |
| `Mcp:MaxResultRows` | `int` | `1 .. 10000` | `100` | Maximale Ergebniszeilen pro Datenabfrage. |
| `Mcp:RequirePiiMasking` | `bool` | `true \| false` | `true` | Automatisches Scrubbing von PII- (E-Mail, IBAN) und DSGVO-Art.-9-Daten vor Übermittlung an LLMs. |
| `Mcp:AllowedOperations` | `string[]` | GraphQL Operationen | `[]` | Whitelist freigegebener Abfragen. |
| `Mcp:warn_allow_unmasked_ai_access` | `bool` | `true \| false` | `false` | **WARN**: Deaktiviert PII-Maskierung für KI-Streams (nur Dev/Sandbox). |
| `Mcp:danger_bypass_mcp_auth` | `bool` | `true \| false` | `false` | **DANGER**: Umgeht MCP-Authentifizierung (in Produktion verboten). |

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

### 2.17 `VectorSearch` (Native Vector Database & RAG Egress)

| Schlüssel | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `VectorSearch:Enabled` | `bool` | `true \| false` | `false` | Aktiviert die native Vektor- und RAG-Egress-Engine (`F-AI-09`). |
| `VectorSearch:MaxTopK` | `int` | `1 .. 1000` | `50` | Obergrenze für zurückgegebene Vektor-Treiber-Chunks pro Abfrage. |
| `VectorSearch:EnablePiiRedaction` | `bool` | `true \| false` | `true` | Automatisches In-Stream Scrubbing von PII-Daten (E-Mails, IBANs, Telefone) in Chunks. |
| `VectorSearch:DefaultDistanceMetric` | `string` | `Cosine \| Euclidean \| DotProduct` | `"Cosine"` | Standard-Distanzmetrik für Ähnlichkeitssuchen. |
| `VectorSearch:Sources` | `dict` | Vektor-Datenquellen | `{}` | Konfigurierte Quellen (`PgVector`, `Qdrant`, `Milvus`). |

```json
"VectorSearch": {
  "Enabled": true,
  "MaxTopK": 50,
  "EnablePiiRedaction": true,
  "DefaultDistanceMetric": "Cosine",
  "Sources": {
    "pgvector-kb": {
      "Provider": "PgVector",
      "ConnectionString": "Host=postgres;Database=vectordb;Username=rag;Password=secret;",
      "DefaultCollection": "knowledge_chunks"
    },
    "qdrant-docs": {
      "Provider": "Qdrant",
      "Endpoint": "http://qdrant:6333",
      "ApiKey": "secret-qdrant-key"
    },
    "milvus-archive": {
      "Provider": "Milvus",
      "Endpoint": "http://milvus:19530"
    }
  }
}
```

### 2.18 `SemanticCache` (Semantischer Embedding-Cache)

| Schlüssel | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `SemanticCache:Enabled` | `bool` | `true \| false` | `false` | Aktiviert den semantischen Cache für Prompt- und Vektorabfragen (`F-AI-10`). |
| `SemanticCache:MinSimilarityScore` | `double` | `0.0 .. 1.0` | `0.85` | Mindest-Cosinus-Ähnlichkeit für einen semantischen Cache-Hit. |
| `SemanticCache:TtlMinutes` | `int` | `1 .. 10080` | `60` | Gültigkeitsdauer (TTL) von Cache-Einträgen in Minuten. |
| `SemanticCache:MaxEntriesPerPartition` | `int` | `1 .. 10000` | `100` | Maximale Einträge pro isolierter Mandanten-/Benutzer-Partition (LRU). |
| `SemanticCache:MaxPartitions` | `int` | `1 .. 50000` | `1000` | Obergrenze aktiver Cache-Partitionen (DoS-Schutz). |

```json
"SemanticCache": {
  "Enabled": true,
  "MinSimilarityScore": 0.85,
  "TtlMinutes": 60,
  "MaxEntriesPerPartition": 100,
  "MaxPartitions": 1000
}
```

### 2.19 `PolicyRecommendation` (Autonome Least-Privilege Empfehlungen)

| Schlüssel | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `PolicyRecommendation:Enabled` | `bool` | `true \| false` | `false` | Aktiviert die Analyse von 403-Mustern und Consent-Vorschlägen (`F-AI-10`). |
| `PolicyRecommendation:MaxQueueCapacity` | `int` | `10 .. 5000` | `500` | Maximale Anzahl gepufferter Empfehlungsvorschläge. |
| `PolicyRecommendation:MinDenialCountThreshold` | `int` | `1 .. 50` | `3` | Mindestanzahl an Zugriffsverweigerungen vor Auslösung einer Empfehlung. |
| `PolicyRecommendation:ProposalExpiryMinutes` | `int` | `1 .. 43200` | `1440` | Gültigkeitsdauer eines Vorschlags im Triage-Postfach. |

```json
"PolicyRecommendation": {
  "Enabled": true,
  "MaxQueueCapacity": 500,
  "MinDenialCountThreshold": 3,
  "ProposalExpiryMinutes": 1440
}
```

### 2.20 `DuckDbOlap` & `ArrowExport` (In-Memory OLAP & Arrow IPC Streaming)

| Schlüssel | Typ | Wertebereich | Standard | Beschreibung |
| :--- | :--- | :--- | :--- | :--- |
| `DuckDbOlap:Enabled` | `bool` | `true \| false` | `false` | Aktiviert die eingebettete In-Memory DuckDB-Vektorengine (`F-DATA-03`). |
| `DuckDbOlap:MaxMemory` | `string` | z. B. `"512MB"`, `"2GB"` | `"1GB"` | Maximale RAM-Quota pro DuckDB-Session (`PRAGMA max_memory`). |
| `DuckDbOlap:MaxThreads` | `int` | `1 .. 32` | `2` | Maximale Thread-Anzahl für parallele SIMD-Ausführung. |
| `DuckDbOlap:MaxStagedRowsPerTable` | `int` | `1000 .. 5000000` | `250000` | Obergrenze gestagter Zeilen pro temporärer Quelltabelle. |
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
Gateway__GovernanceDb__Provider=SqlServer
Gateway__GovernanceDb__ConnectionString="Server=sql-ha.corp.local;Database=Governance;Integrated Security=SSPI;TrustServerCertificate=True;"

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
```

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
