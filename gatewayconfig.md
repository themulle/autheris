# Konfigurationsspezifikation: C# GraphQL Enterprise Gateway

**Status:** Normative Dokumentation  
**Referenz-Dokumente:** [README.md](file:///root/gql/README.md), [gatewayconfig.md](file:///root/gql/gatewayconfig.md)  
**Muster:** ASP.NET Core Strongly-Typed Options Pattern mit `ValidateDataAnnotations()` & `ValidateOnStart()`

---

## 1. Konfigurations-Architektur & Sicherheitsprinzipien

Das Gateway folgt der **12-Factor-App**-Methodik und dem Prinzip des **Fail-Fast**:
1. **Keine Secrets im Klartext (NF-SEC-03):** Kennwörter für Datenbanken, Redis, Dienstkonten und kryptografische HMAC-Schlüssel dürfen niemals in `appsettings.json` oder im Git-Repository liegen. Sie werden zur Laufzeit via Umgebungsvariablen (`GQLGATEWAY__*`) oder Secrets-Stores (Azure Key Vault / HashiCorp Vault) injiziert.
2. **Validierung beim Anwendungsstart:** Ungültige Wertebereiche oder fehlende Pflichtparameter führen zum sofortigen Prozessabbruch beim Booten (`ValidateOnStart()`), bevor der Pod Datenverkehr annimmt.
3. **Umgebungs-Profile:** Trennung zwischen `appsettings.json` (Produktions-Defaults), `appsettings.Development.json` (Zero-Dependency In-Memory Profile) und container-spezifischen Umgebungsvariablen.

---

## 2. Referenzdatei: `appsettings.json` (Produktions-Vorlage)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "HotChocolate": "Information",
      "Autheris": "Information"
    }
  },

  "Gateway": {
    "HighAvailability": {
      "DrainDelaySeconds": 5,
      "QueryTimeoutSeconds": 30,
      "ShutdownTimeoutSeconds": 40,
      "TerminationGracePeriodSeconds": 60
    },

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
            "PasswordHashSha256": "a665a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3",
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
      },
      "Adfs": {
        "Enabled": false,
        "MetadataAddress": "https://adfs.corp.local/federationmetadata/2007-06/federationmetadata.xml",
        "Audience": "microsoft:identityserver:gql-gateway"
      }
    },

    "GovernanceDb": {
      "Provider": "SqlServer",
      "ConnectionString": "Server=gov-db.corp.local;Database=AutherisGovernance;Integrated Security=true;TrustServerCertificate=false;",
      "CommandTimeoutSeconds": 15,
      "EnableOutboxProcessor": true
    },

    "DataSources": {
      "finance": {
        "Provider": "SqlServer",
        "ConnectionString": "Server=sql-finance.corp.local;Database=FinanceDb;Integrated Security=SSPI;TrustServerCertificate=true;"
      },
      "hr": {
        "Provider": "PostgreSql",
        "ConnectionString": "Host=pg-hr.corp.local;Port=5432;Database=HrDb;Username=gql_app;Password=SuperSecretPass!;SSL Mode=Require;"
      }
    },

    "Caching": {
      "L1MemoryCache": {
        "SizeLimitMb": 512,
        "DefaultTtlMinutes": 10,
        "SensitiveTableTtlSeconds": 60
      },
      "Redis": {
        "Configuration": "redis-cluster.corp.local:6379,ssl=true,abortConnect=false",
        "InstanceName": "Autheris:",
        "InvalidationChannel": "consent:invalidations",
        "ConnectTimeoutMs": 2000,
        "SyncTimeoutMs": 1000
      },
      "EpochValidation": {
        "FailClosedOnSensitiveTables": true,
        "DegradedMaxStalenessSeconds": 30,
        "PipelinedMGetEnabled": true
      }
    },

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
    },

    "GraphQL": {
      "MaxAllowedExecutionDepth": 10,
      "MaxAllowedComplexity": 1500,
      "EnableIntrospection": false,
      "PersistedQueriesOnly": true,
      "EnableBananaCakePop": false,
      "MaxResponseRows": 5000,
      "MaxResponseBytes": 10485760
    },

    "DataMasking": {
      "HmacKeyId": "key-2026-q1",
      "HmacSecretKeyVaultRef": "GQL-HMAC-SECRET-KEY",
      "MaskingCacheTtlHours": 24
    },

    "Audit": {
      "TierAEnabled": true,
      "TierBAggregationWindowSeconds": 60,
      "AuditLogRetentionDays": 3650,
      "VerifyHashChainIntervalHours": 24,
      "ElasticsearchSinkUrl": "https://elastic-audit.corp.local:9200"
    }
  }
}
```

---

## 3. Entwickler-Profil: `appsettings.Development.json` (Zero-Dependency)

Ermöglicht den lokalen Start ohne Docker, Active Directory oder externe Datenbanken:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "HotChocolate": "Debug",
      "Autheris": "Debug"
    }
  },

  "Gateway": {
    "HighAvailability": {
      "DrainDelaySeconds": 1,
      "QueryTimeoutSeconds": 10,
      "ShutdownTimeoutSeconds": 15,
      "TerminationGracePeriodSeconds": 20
    },

    "Authentication": {
      "Domain": "DEV.LOCAL",
      "ServicePrincipalName": "HTTP/localhost",
      "RequireKerberosOnly": false,
      "GroupCacheTtlMinutes": 1,
      "EnableTestAuthHandler": true
    },

    "GovernanceDb": {
      "Provider": "Sqlite",
      "ConnectionString": "Data Source=:memory:;Mode=Memory;Cache=Shared",
      "CommandTimeoutSeconds": 5,
      "EnableOutboxProcessor": true
    },

    "Caching": {
      "L1MemoryCache": {
        "SizeLimitMb": 64,
        "DefaultTtlMinutes": 5,
        "SensitiveTableTtlSeconds": 10
      },
      "Redis": {
        "Configuration": "localhost:6379,abortConnect=false"
      },
      "EpochValidation": {
        "FailClosedOnSensitiveTables": false,
        "DegradedMaxStalenessSeconds": 60,
        "PipelinedMGetEnabled": false
      }
    },

    "RateLimiting": {
      "PreAuthIpRateLimit": {
        "PermitLimit": 1000,
        "WindowSeconds": 60
      },
      "PostAuthSidRateLimit": {
        "TokenBucketCapacity": 5000,
        "TokensPerSecond": 500,
        "MaxCostPerMinute": 100000
      }
    },

    "GraphQL": {
      "MaxAllowedExecutionDepth": 15,
      "MaxAllowedComplexity": 5000,
      "EnableIntrospection": true,
      "PersistedQueriesOnly": false,
      "EnableBananaCakePop": true,
      "MaxResponseRows": 10000,
      "MaxResponseBytes": 52428800
    },

    "DataMasking": {
      "HmacKeyId": "dev-key",
      "HmacSecretKeyVaultRef": "DEV_INSECURE_TEST_KEY_ONLY",
      "MaskingCacheTtlHours": 1
    },

    "Audit": {
      "TierAEnabled": true,
      "TierBAggregationWindowSeconds": 5,
      "AuditLogRetentionDays": 30,
      "VerifyHashChainIntervalHours": 1,
      "ElasticsearchSinkUrl": ""
    }
  }
}
```

---

## 4. Detaillierte Parameter-Referenz

### 4.1 High Availability & Draining (`Gateway:HighAvailability`)
Implementiert die 6 Phasen des Zero-Downtime Reboots gemäß **NF-HA-01**.

| Schlüssel | Typ | Standard | Gültiger Bereich | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---:|:---|
| `DrainDelaySeconds` | `int` | `5` | 1 – 30 s | NF-HA-01 | Wartezeit nach Setzen von `/health/ready` auf 503, bevor Kestrel neue Verbindungen ablehnt (Puffer für Load-Balancer-Deregistrierung). |
| `QueryTimeoutSeconds` | `int` | `30` | 5 – 120 s | NF-HA-01 | Maximale Ausführungszeit einer GraphQL-Abfrage vor dem Abbruch. |
| `ShutdownTimeoutSeconds` | `int` | `40` | 10 – 180 s | NF-HA-01 | Kestrel Graceful Shutdown Timeout. Muss mind. `QueryTimeoutSeconds + 10s` betragen. |
| `TerminationGracePeriodSeconds` | `int` | `60` | 20 – 300 s | NF-HA-01 | Entspricht der Kubernetes Pod-Grace-Period. Muss $\ge \text{DrainDelay} + \text{ShutdownTimeout} + 10\text{s}$ sein. |

---

### 4.2 Authentifizierung & Multi-Protocol Identity (`Gateway:Authentication`)
Steuert Windows SSO, Kerberos-Validierung, Traefik ForwardAuth Ingress, Basic Auth, Microsoft Entra ID (Azure AD), AD FS und AD-Gruppenauflösung (**F-AUTH-01 bis F-AUTH-05**).

#### 4.2.1 Kerberos & Base Auth
| Schlüssel | Typ | Standard | Gültiger Bereich | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---:|:---|
| `Domain` | `string` | `"CORP.LOCAL"` | Gültiger FQDN | F-AUTH-01 | Active Directory Domäne. |
| `ServicePrincipalName` | `string` | `"HTTP/..."` | SPN-Format | F-AUTH-04 | Clusterweiter SPN für Kerberos-Tickets. |
| `RequireKerberosOnly` | `bool` | `true` | `true / false` | F-AUTH-04 | Verhindert NTLM-Fallbacks im Cluster-Betrieb (NTLM ist zustandsbehaftet). |
| `GroupCacheTtlMinutes` | `int` | `5` | 1 – 60 min | F-AUTH-01 | Cache-Dauer für aufgelöste transitive AD-Gruppen-SIDs im Redis. |
| `EnableTestAuthHandler` | `bool` | `false` | `true / false` | QA | Aktiviert Injection von Benutzer-SIDs via Header (`X-Test-User-Sid`). In Produktion streng verboten! |

#### 4.2.2 Kubernetes Ingress ForwardAuth (`Gateway:Authentication:ForwardAuth`)
| Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `Enabled` | `bool` | `false` | F-AUTH-06 | Aktiviert Traefik / Kubernetes Ingress ForwardAuth Scheme. |
| `UserHeader` | `string` | `"X-Forwarded-User"` | F-AUTH-06 | HTTP-Header für Benutzer-Identifikator oder SID. |
| `EmailHeader` | `string` | `"X-Forwarded-Email"` | F-AUTH-06 | HTTP-Header für Benutzer-E-Mail. |
| `GroupsHeader` | `string` | `"X-Forwarded-Groups"` | F-AUTH-06 | HTTP-Header für kommagetrennte Gruppen-SIDs / Gruppennamen. |
| `RolesHeader` | `string` | `"X-Forwarded-Roles"` | F-AUTH-06 | HTTP-Header für kommagetrennte Gateway-Rollen. |
| `SharedSecretHeader` | `string` | `"X-Forwarded-Secret"` | NF-SEC-01 | HTTP-Header für Pre-Shared Secret zwischen Traefik und Gateway. |
| `SharedSecretKeyVaultRef`| `string` | `""` | NF-SEC-03 | Key Vault Secret-Referenz für das Shared Secret. |
| `RequireTrustedProxy` | `bool` | `true` | NF-SEC-01 | Zero-Trust: Erfordert zwingend, dass ForwardAuth-Header nur von IPs aus `TrustedNetworks` oder `TrustedProxies` stammen. |
| `TrustedNetworks` | `List<string>` | `["127.0.0.1/32", "::1/128"]` | NF-SEC-01 | CIDR-Netzwerke vertrauenswürdiger Proxies (z. B. Kubernetes Pod-CIDR `"10.244.0.0/16"`). |
| `TrustedProxies` | `List<string>` | `[]` | NF-SEC-01 | Feste IP-Adressen vertrauenswürdiger Ingress-Controller. |

#### 4.2.3 Basic Authentication (`Gateway:Authentication:BasicAuth`)
| Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `Enabled` | `bool` | `false` | F-AUTH-07 | Aktiviert HTTP Basic Auth auf `/graphql` und den Login-Endpunkt `/api/auth/login`. |
| `Users` | `List<User>` | `[]` | F-AUTH-07 | Konfigurierte Konten mit `Username`, `PasswordHashSha256`, `Roles`, `UserSid` und `GroupSids`. |

#### 4.2.4 Microsoft Entra ID & AD FS (`Gateway:Authentication:EntraId` / `Adfs`)
| Sektion / Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `EntraId:Enabled` | `bool` | `false` | F-AUTH-08 | Aktiviert JWT Bearer Validierung für Microsoft Entra ID (Azure AD). |
| `EntraId:TenantId` | `string` | `""` | F-AUTH-08 | Azure AD Tenant-ID GUID. |
| `EntraId:ClientId` | `string` | `""` | F-AUTH-08 | Azure AD App Client-ID. |
| `EntraId:Audience` | `string` | `""` | F-AUTH-08 | Erwartete Audience (z. B. `"api://gql-gateway"`). |
| `Adfs:Enabled` | `bool` | `false` | F-AUTH-09 | Aktiviert JWT Bearer Validierung für AD FS. |
| `Adfs:MetadataAddress` | `string` | `""` | F-AUTH-09 | AD FS Federation Metadata Endpoint URL. |
| `Adfs:Audience` | `string` | `""` | F-AUTH-09 | AD FS Relying Party Identifier. |

---

### 4.3 Fachdaten-Verbindungen & RLS-Pushdown (`Gateway:DataSources`)
Konfiguriert relationale Datenbanken für Fachdatenabfragen mit SQL-Where-Pushdown (**F-DATA-01, NF-SEC-02**).

| Schlüssel | Typ | Standard | Optionen | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---:|:---|
| `DataSources:{name}:Provider` | `string` | `"SqlServer"` | `SqlServer`, `PostgreSql`, `Sqlite`, `Oracle`, `Databricks` | F-DATA-01 | Zieldatenbank-Dialekt. |
| `DataSources:{name}:ConnectionString` | `string` | *(geheim)* | Gültige Connection String | F-DATA-01 | Verbindung zur Zieldatenbank. RLS-Filter werden direkt als SQL WHERE gepusht. |

---

### 4.3 Governance-Datenbank (`Gateway:GovernanceDb`)
Verwaltet Consents, Metadatenkatalog und die Audit-Hashkette (**F-DATA-02, NF-OBS-03**).

| Schlüssel | Typ | Standard | Optionen | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---:|:---|
| `Provider` | `string` | `"SqlServer"` | `SqlServer`, `PostgreSql`, `Sqlite` | F-DATA-02 | Verwendeter Datenbanktreiber. |
| `ConnectionString` | `string` | *(geheim)* | Gültige ADO.NET Connection String | F-DATA-02 | Verbindungszeichenfolge zur Governance-DB. |
| `CommandTimeoutSeconds` | `int` | `15` | 1 – 60 s | NF-PERF | Timeout für Governance-Abfragen. |
| `EnableOutboxProcessor` | `bool` | `true` | `true / false` | NF-PERF-04 | Aktiviert den Hintergrund-Worker für transaktionale Outbox-Events nach Redis. |

---

### 4.4 Caching & Epoch-Validierung (`Gateway:Caching`)
Zweistufiges Caching mit atomarer Epoch-Invalidierung (**NF-PERF-02, NF-PERF-04**).

| Sektion / Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `L1MemoryCache:SizeLimitMb` | `int` | `512` | NF-PERF-02 | Maximaler RAM-Verbrauch des In-Process L1-Caches in Megabyte. |
| `L1MemoryCache:DefaultTtlMinutes` | `int` | `10` | NF-PERF-02 | Maximale Fallback-Gültigkeit eines Consent-Entscheids in L1. |
| `L1MemoryCache:SensitiveTableTtlSeconds` | `int` | `60` | NF-PERF-04 | Reduzierte TTL für Tabellen mit Vier-Augen-Prinzip / hoher Sensitivität. |
| `Redis:Configuration` | `string` | *(geheim)* | NF-PERF-02 | StackExchange.Redis Verbindungszeichenfolge (TLS, Cluster). |
| `Redis:InvalidationChannel` | `string` | `"consent:invalidations"` | NF-PERF-04 | Redis Pub/Sub Broadcast-Kanal für sofortige L1-Eviction. |
| `EpochValidation:FailClosedOnSensitiveTables` | `bool` | `true` | NF-PERF-04 | Verweigert Zugriff auf sensible Tabellen, falls Redis nicht erreichbar ist. |
| `EpochValidation:DegradedMaxStalenessSeconds` | `int` | `30` | NF-PERF-04 | Erlaubtes Weiterarbeiten mit lokalem L1 bei Redis-Ausfall für Standard-Tabellen. |
| `EpochValidation:PipelinedMGetEnabled` | `bool` | `true` | NF-PERF-04 | Liest alle Tabellen-Epochs eines Requests in einem einzigen Redis-Roundtrip. |

---

### 4.5 Rate Limiting & Query Protection (`Gateway:RateLimiting` & `GraphQL`)
Schutz vor DoS und teuren Big-Data-Abfragen (**NF-SEC-01, NF-SEC-02**).

| Sektion / Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `PreAuthIpRateLimit:PermitLimit` | `int` | `100` | NF-SEC-01 | Maximale Requests pro IP-Adresse vor Windows-Auth (Schutz vor Kerberos-DoS). |
| `PreAuthIpRateLimit:WindowSeconds` | `int` | `60` | NF-SEC-01 | Zeitfenster für Pre-Auth IP-Begrenzung. |
| `PostAuthSidRateLimit:TokenBucketCapacity` | `int` | `500` | NF-SEC-01 | Maximales Token-Guthaben pro authentifizierter Benutzer-SID. |
| `PostAuthSidRateLimit:TokensPerSecond` | `int` | `50` | NF-SEC-01 | Nachfüllrate des Token-Buckets. |
| `PostAuthSidRateLimit:MaxCostPerMinute` | `int` | `10000` | NF-SEC-01 | Maximales kumulatives Query-Kostenbudget pro Minute je SID. |
| `GraphQL:MaxAllowedExecutionDepth` | `int` | `10` | NF-SEC-02 | Maximale Schachtelungstiefe der GraphQL-Query. |
| `GraphQL:MaxAllowedComplexity` | `int` | `1500` | NF-SEC-02 | Maximale statische Kostenkomplexität. |
| `GraphQL:PersistedQueriesOnly` | `bool` | `true` | NF-SEC-02 | Erlaubt in Produktion nur registrierte Trusted Documents (kein freies GraphQL). |
| `GraphQL:EnableIntrospection` | `bool` | `false` | NF-SEC-02 | Deaktiviert Schema-Introspection in Produktion. |
| `GraphQL:EnableBananaCakePop` | `bool` | `false` | NF-SEC-03 | Deaktiviert die Hot Chocolate Web-IDE in Produktion. |
| `GraphQL:MaxResponseRows` | `int` | `5000` | NF-SEC-02 | Maximale Zeilenanzahl pro GraphQL-Abfrage. |
| `GraphQL:MaxResponseBytes` | `long` | `10485760` (10 MB) | NF-SEC-02 | Maximale Response-Größe zur Vermeidung von Out-of-Memory. |

---

### 4.6 Data Masking & Pseudonymisierung (`Gateway:DataMasking`)
Steuert die Maskierungs-Engine (**F-CONS-08**).

| Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `HmacKeyId` | `string` | `"key-2026-q1"` | F-CONS-08 | Bezeichner des aktiven HMAC-Kryptoschlüssels (für Schlüsselrotation). |
| `HmacSecretKeyVaultRef` | `string` | `"GQL-HMAC-SECRET-KEY"` | F-CONS-08 | Referenzname des Geheimnisses im Key Vault / Secret Manager. |
| `MaskingCacheTtlHours` | `int` | `24` | F-CONS-08 | Cache-Dauer deterministischer Pseudonyme im Gateway-Arbeitsspeicher. |

---

### 4.7 Audit-Logging & Compliance (`Gateway:Audit`)
Steuert revisionssichere Hashketten-Protokollierung und SIEM-Streaming (**NF-OBS-03, NF-DSGVO-01**).

| Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `TierAEnabled` | `bool` | `true` | NF-OBS-03 | Transaktionales Logging aller Denies, Admin-Events und sensiblen Tabellenzugriffe in die DB. |
| `TierBAggregationWindowSeconds` | `int` | `60` | NF-OBS-03 | Aggregations-Intervall für normale Lese-Zugriffe (Verdichtung vor DB-Schreiben). |
| `AuditLogRetentionDays` | `int` | `3650` (10 J.) | NF-DSGVO-01 | Aufbewahrungsfrist vor Partitionslöschung (GoBD/SOX). |
| `VerifyHashChainIntervalHours` | `int` | `24` | NF-OBS-03 | Geplanter Hintergrund-Job zur Verifikation der SHA-256 Hashkette. |
| `ElasticsearchSinkUrl` | `string` | `https://...` | NF-OBS-03 | Ziel-URL für asynchrones Audit-Streaming nach Kibana/Elastic. |

---

### 4.8 Enterprise Data Catalog Integration (`Gateway:Catalog`)
Steuert die Anbindung an Microsoft Purview, Collibra, Alation und OpenMetadata zur automatischen Metadaten- und Sensitivitäts-Synchronisation.

| Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `Enabled` | `bool` | `false` | NF-GOV-01 | Aktiviert die Hintergrund-Synchronisation mit dem externen Katalog. |
| `Provider` | `string` | `"OpenMetadata"` | NF-GOV-01 | Provider-Auswahl: `OpenMetadata`, `MicrosoftPurview`, `Collibra`, `Alation`. |
| `SyncMode` | `string` | `"Mirror"` | NF-GOV-01 | `Mirror` (persistente SQLite-Spiegelung) oder `Reference` (On-Demand Lookup). |
| `SyncIntervalMinutes` | `int` | `60` | NF-GOV-01 | Periodisches Synchronisationsintervall des Hintergrund-Workers. |
| `TagToMaskingRuleMap` | `Dictionary` | *(Standard-Map)* | NF-SEC-02 | Zuordnung von externen Katalog-Tags zu internen Maskierungsalgorithmen. |
| `GdprArticle9Tags` | `List<string>` | *(Art. 9 Tags)* | NF-DSGVO-02 | Liste von Tags für Art. 9 DSGVO Daten (erzwingt `HIGH` und `RequiresFourEyes`). |
| `PiiTags` | `List<string>` | *(PII Tags)* | NF-DSGVO-01 | Liste von Tags für personenbezogene Daten (PII). |

---

### 4.9 Insecure Modes & Getting-Started (`Gateway:Insecure`)
Ermöglicht das gezielte Lockern von Sicherheitsbarrieren für Entwicklungs- und Onboarding-Zwecke.

| Schlüssel | Typ | Standard | Level | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `danger_allow_anonymous_queries` | `bool` | `false` | **CRITICAL** | Erlaubt unauthentifizierte GraphQL-Abfragen (Standardbenutzer `S-1-5-21-ANONYMOUS`). |
| `danger_bypass_authorization` | `bool` | `false` | **CRITICAL** | Deaktiviert Zero-Trust Consent-Prüfungen vollständig (`ALLOW` für alle Tabellen). |
| `danger_bypass_webhook_signature_validation` | `bool` | `false` | **CRITICAL** | Überspringt HMAC-Signaturprüfung eingehender Webhooks (z. B. ServiceNow/Jira). |
| `danger_allow_untrusted_certificates` | `bool` | `false` | **CRITICAL** | Deaktiviert TLS-Zertifikatsvalidierung bei ausgehenden API-Verbindungen. |
| `danger_allow_anonymous_webhooks` | `bool` | `false` | **CRITICAL** | Akzeptiert Webhooks ohne jegliche Authentifizierung. |
| `warn_allow_all_cors_origins` | `bool` | `false` | **WARN** | Erlaubt alle Origins (`*`) und deaktiviert CSRF-Preflight-Prüfungen. |
| `warn_disable_rate_limiting` | `bool` | `false` | **WARN** | Deaktiviert IP- und SID-Token-Bucket-Rate-Limiting. |
| `warn_bypass_query_cost_limits` | `bool` | `false` | **WARN** | Hebt GraphQL AST-Depth- und Query-Complexity-Limits auf. |

---

### 4.10 Identity & M2M Service-Accounts (`Gateway:Identity`)
Ermöglicht hybride Migration zu Microsoft Entra ID (OIDC) und Machine-to-Machine Authentifizierung.

| Schlüssel | Typ | Standard | Anforderung | Beschreibung |
|:---|:---:|:---:|:---:|:---|
| `IdpType` | `string` | `"Hybrid"` | NF-AUTH-06 | `ActiveDirectory`, `EntraId` oder `Hybrid` via `IIdentityProvider`. |
| `EnableServicePrincipals` | `bool` | `true` | NF-AUTH-07 | Aktiviert M2M-Dienst-Prinzipale via Client-Credentials / mTLS (`SP-<id>` SIDs). |
| `ServicePrincipalConsentTtlDays` | `int` | `90` | NF-AUTH-07 | Maximale Gültigkeitsdauer für technische Service-Account-Consents. |

---

## 5. C# Strongly Typed Model & Startup-Validierung

Folgender C#-Code in `Autheris.Domain.Options` erzwingt die Validierung zur Compile- und Startzeit:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    [Required] public HighAvailabilityOptions HighAvailability { get; init; } = new();
    [Required] public AuthenticationOptions Authentication { get; init; } = new();
    [Required] public GovernanceDbOptions GovernanceDb { get; init; } = new();
    [Required] public CachingOptions Caching { get; init; } = new();
    [Required] public RateLimitingOptions RateLimiting { get; init; } = new();
    [Required] public GraphQLOptions GraphQL { get; init; } = new();
    [Required] public DataMaskingOptions DataMasking { get; init; } = new();
    [Required] public AuditOptions Audit { get; init; } = new();
}

public sealed class HighAvailabilityOptions
{
    [Range(1, 30)] public int DrainDelaySeconds { get; init; } = 5;
    [Range(5, 120)] public int QueryTimeoutSeconds { get; init; } = 30;
    [Range(10, 180)] public int ShutdownTimeoutSeconds { get; init; } = 40;
    [Range(20, 300)] public int TerminationGracePeriodSeconds { get; init; } = 60;
}

public sealed class AuthenticationOptions
{
    [Required] public string Domain { get; init; } = string.Empty;
    [Required] public string ServicePrincipalName { get; init; } = string.Empty;
    public bool RequireKerberosOnly { get; init; } = true;
    [Range(1, 60)] public int GroupCacheTtlMinutes { get; init; } = 5;
    public bool EnableTestAuthHandler { get; init; } = false;
}

public sealed class GraphQLOptions
{
    [Range(1, 20)] public int MaxAllowedExecutionDepth { get; init; } = 10;
    [Range(100, 10000)] public int MaxAllowedComplexity { get; init; } = 1500;
    public bool EnableIntrospection { get; init; } = false;
    public bool PersistedQueriesOnly { get; init; } = true;
    public bool EnableBananaCakePop { get; init; } = false;
    [Range(100, 100000)] public int MaxResponseRows { get; init; } = 5000;
    [Range(1048576, 104857600)] public long MaxResponseBytes { get; init; } = 10485760;
}
```

### Registrierung in `Program.cs`

```csharp
builder.Services.AddOptions<GatewayOptions>()
    .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(opts => 
        opts.HighAvailability.ShutdownTimeoutSeconds >= opts.HighAvailability.QueryTimeoutSeconds + 10,
        "NF-HA-01 Verletzung: ShutdownTimeoutSeconds muss mindestens 10s größer als QueryTimeoutSeconds sein.")
    .Validate(opts =>
        opts.HighAvailability.TerminationGracePeriodSeconds >= opts.HighAvailability.DrainDelaySeconds + opts.HighAvailability.ShutdownTimeoutSeconds + 10,
        "NF-HA-01 Verletzung: TerminationGracePeriodSeconds muss größer als DrainDelay + ShutdownTimeout + 10s sein.")
    .Validate(opts =>
        !builder.Environment.IsProduction() || !opts.Authentication.EnableTestAuthHandler,
        "Sicherheitsverletzung: EnableTestAuthHandler darf in PRODUKTION niemals true sein!")
    .ValidateOnStart();
```

---

## 6. Secrets & Umgebungsvariablen (Kubernetes / Docker)

Im Container werden sensitive Werte über Standard-ASP.NET-Core-Umgebungsvariablen übergeben (`__` als Trenner):

```bash
# Governance-DB Verbindungszeichenfolge
GQLGATEWAY__GOVERNANCEDB__CONNECTIONSTRING="Server=sql-ha.corp.local;Database=Governance;User Id=svc_autheris_gov;Password=SuperSecret123!;Encrypt=True;"

# Redis-Verbindung mit Kennwort
GQLGATEWAY__CACHING__REDIS__CONFIGURATION="redis-cluster.corp.local:6379,password=Red1sSecretPass!,ssl=true"

# HMAC-Geheimnis für Data-Masking (rotierbar)
GQLGATEWAY__DATAMASKING__HMACSECRETKEYVAULTREF="4f3a8b...64-hex-chars-secret-salt..."

# Elasticsearch API Key
GQLGATEWAY__AUDIT__ELASTICSEARCHAPIKEY="V3Z...API-KEY..."
```
