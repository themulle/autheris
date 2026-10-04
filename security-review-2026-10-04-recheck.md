# Nachprüfung Security Review 2026-10-04

**Geprüfter Stand:** Commit `ca53775` ("remediate all findings … C-1, H-1..H-4, M-1..M-10"), verglichen mit dem Review-Stand `e59c2e2`.
**Methode:**
- Diff aller geänderten Quelldateien manuell gelesen.
- Betroffene Aufrufketten nachverfolgt.
- Kein Build und kein Testlauf, weil hier kein .NET SDK verfügbar ist.

## Ergebnis auf einen Blick

| Status | Findings |
|---|---|
| ✅ Behoben | C-1, H-1, H-3, H-4, M-5, M-6, M-7, M-8 |
| ✅ Behoben, aber mit Nebenwirkung | H-2 |
| ⚠️ Teilweise behoben | M-2, M-3, M-4, M-9, M-10 |
| ❌ Nicht behoben | M-1 |
| ❌ Offen (nicht im Commit-Scope) | Low 1–17 |
| 🆕 Neu eingeführt | N-1 (Medium), N-2 (Low) |

---

## Details

### ✅ C-1: Envoy ext_authz
**Was jetzt gilt:**
- Identität, Tenant und Gruppen kommen nur noch aus dem validierten `context.User`.
- Die Header-Fallbacks und das Parsen unsignierter JWTs sind entfernt.
- `x-autheris-rls-filter` wird nicht mehr ausgegeben.
- `allowed_headers` ist bereinigt, `RequireAuthorization` ist gesetzt.

**Restpunkte (Low):**
- Der Endpoint ist nicht auf eine Mesh-Service-Identität beschränkt. Jeder Nutzer kann Entscheidungen für sich selbst abfragen; das ist unkritisch.
- Eingehende `x-autheris-*` Header werden am Edge nicht gestrippt. Weil der Gateway `x-autheris-rls-filter` nicht mehr setzt, kann ein Client diesen Header jetzt **selbst** an Upstreams durchreichen. Wenn ein Upstream den Header auswertet, muss er in der Envoy-Konfiguration entfernt werden (`request_headers_to_remove`).

### ✅ H-1: ForwardAuth-Tenant
**Was jetzt gilt:** Mit `TrustUpstreamTenant=false` (Default) wird der Tenant-Header ignoriert. Mit `true` ist eine nicht-leere `AllowedTenantIds` Pflicht, sonst wird fail-closed abgelehnt.

**Restpunkt:** Die neue Option ist in `docs/configuration-guide.md` noch nicht dokumentiert.

### ✅ H-2: WebSocket-Hijacking
**Was jetzt gilt:** `WebSocketAuthInterceptor` lehnt den Ambient-Credential-Fallback ab, sobald ein `Origin` Header vorhanden ist.

**Nebenwirkung:** Browser-Clients senden immer `Origin` und brauchen deshalb jetzt ein Token in `connection_init`. `JwtSocketTokenValidator` validiert aber nur mit statisch konfiguriertem `IssuerSigningKey(s)`. Bei Entra ID / AD FS kommen die Keys über die Authority-Metadaten, deshalb wird jedes Token abgelehnt (siehe Low-17 aus dem Review). Kerberos-Browsernutzer haben gar kein Token.

**Folge:** Subscriptions aus dem Browser funktionieren in Produktion nicht mehr. Das ist sicher (fail-closed), aber funktional kaputt.

**Fix:** Signing-Keys im Validator über `jwtOptions.ConfigurationManager` auflösen.

### ✅ H-3: Iceberg REST
**Was jetzt gilt:**
- Listings sind auf den Tenant gefiltert.
- Der Cross-Tenant-Fallback ist entfernt (404).
- Credential-Vending liefert 501.

**Restpunkt (Low):** Innerhalb des Tenants gibt `LoadTable` die physische `metadata-location` weiterhin ohne Consent- oder ReBAC-Prüfung aus.

### ✅ H-4: Client-IP-Fallback
Alle drei Stellen nutzen jetzt `IPAddress.None`, der `ip`-Claim wird nicht mehr vertraut:
- `DefaultCrossDomainAccessResolver`
- `GatewayExecutionService` (2×)
- `AiDataGuardrailService`

Ebenso in Envoy.

### ❌ M-1: Admin-Aliase
**Was geändert wurde:** `GatewayAdmin`, `PlatformAdmin` und `PrivacyAdmin` werden jetzt auf die neue Rolle `TenantAdmin` gemappt, nicht mehr auf `ClusterAdmin`. Damit bestehen sie `IsCanonicalClusterAdmin` nicht mehr.

**Warum das nicht reicht:** `GatewayRoleExtensions.Implies` lässt `TenantAdmin` → `GovernanceAdmin` zu (`GatewayRole.cs:79-82`). Sowohl `EndpointSecurity.IsGlobalGovernanceAdmin` als auch die Policy `GatewayPolicies.GovernanceAdmin` werten über `HasAnyRole` die Hierarchie aus.

**Folge:** Ein Tenant-Admin kann weiterhin:
- HitL-Tickets fremder Tenants genehmigen,
- globale Sunsetting-Regeln setzen,
- dbt-Proposals freigeben,
- über `/api/admin/tokens/revoke` beliebige Subjects sperren, auch ClusterAdmins.

`PrivacyAdmin` hat dadurch effektiv dieselben Rechte wie vorher.

**Fix:** `TenantAdmin` darf `GovernanceAdmin` nicht implizieren. Alternativ dürfen globale Checks die Hierarchie nicht nutzen und nur die literalen Rollen `GovernanceAdmin`/`ClusterAdmin` akzeptieren.

### ⚠️ M-2: ITSM-Freigabe
**Verbessert:**
- Der Ablauf läuft jetzt über `ApproveConsentRequestStepAsync`. Damit greifen Four-Eyes (zwei verschiedene Approver nötig) und die Duplikatprüfung.
- Ein Webhook-Replay scheitert am Status.

**Offen:**
- **SoD ist praktisch wirkungslos.** Der Actor ist `ITSM_<SYS>:<instance>:<Approver-Name>`. Der Check `approverSid.EndsWith(requesterSid)` greift nur, wenn das ITSM-System die Windows-SID des Requesters als Approver meldet. ServiceNow und Jira melden Benutzernamen. Wer im ITSM seinen eigenen Antrag genehmigt, wird also nicht erkannt.
- **Data-Owner-Berechtigung wird für ITSM komplett übersprungen.** Mehr dazu in N-1.

**Fix:** Das ITSM-Approver-Konto auf eine SID mappen (Directory-Lookup) und gegen alle Requester-Identitäten prüfen. Alternativ die Freigabe ablehnen, wenn der Approver nicht auflösbar ist.

### ⚠️ M-3: Selbstgenehmigung
**Was neu ist:** `MutationTypes.IsSameIdentity` vergleicht die gespeicherte Requester-SID mit allen Identitäts-Claims des Approvers.

**Erkannt wird:** Requester hat Kerberos genutzt, Approver kommt per OIDC mit `onprem_sid`.

**Nicht erkannt wird:** Requester hat über OIDC eine `oid` gespeichert, Approver kommt per Kerberos. Dessen Claims enthalten die `oid` nicht.

**Nebenbefund:** Die neue Repository-Prüfung `approverSid.EndsWith(requesterSid)` ist ein String-Suffixvergleich. Sicherheitlich ist er nicht schädlich, er kann aber falsch-positiv blockieren.

**Fix wie empfohlen:** Beim Antrag alle Identifier des Requesters speichern und mit `HitLStepUpApprovalService.IsSameIdentity` vergleichen.

### ⚠️ M-4: Cross-Tenant für Tenant-Admins
**Behoben:** FinOps und das EU-AI-Act-Zertifikat. Dort sind fremde Tenants jetzt nur für den kanonischen ClusterAdmin erlaubt.

**Nicht wirksam:** der DP-Budget-Reset (`GovernanceEndpoints.cs:218-250`).
- Die Tenant-Prüfung greift nur, wenn `clientId` ein `:` enthält.
- Echte Client-IDs enthalten keins: `client_id`, `azp`, `sub` und beim Audit-Exporter die Tenant-ID selbst.
- Damit kann ein Tenant-Admin weiterhin jedes Budget zurücksetzen.

**Fix:** Budgets serverseitig an den Tenant binden (Key `tenant:clientId`) oder den Reset nur dem ClusterAdmin erlauben.

### ✅ M-5: CloudEvents-Subscriptions
**Was jetzt gilt:**
- Rollenprüfung ist aktiv.
- Die ID wird serverseitig vergeben.
- Das Secret wird redigiert.
- Limit von 100 Subscriptions pro Tenant.
- Die SSRF-Prüfung läuft über `EgressUrlPolicy.ValidateResolvedAsync` und `AddSecureOutboundHandlers` (IP-Pinning, keine Redirects).

**Restpunkt (Low):** Der Fallback auf den Tenant `"default"` ist geblieben.

### ✅ M-6: OpenAPI-Ingestion
Der Default ist jetzt `AuthMode.None`, sowohl im Domain-Modell als auch bei der Ingestion.

### ✅ M-7: Katalog-Enumeration
- Backstage ist auf Governance-Admin, CatalogSync und ClusterAdmin beschränkt.
- Die Lese-Endpoints der Schema-Registry sind auf Publisher-Rollen beschränkt.
- Flight SQL und Iceberg sind auf den Tenant gefiltert.

**Hinweis:** Backstage hängt an `IsGlobalGovernanceAdmin` und ist deshalb von M-1 betroffen.

### ✅ M-8: ForwardAuth-SIDs
Benutzer bekommen immer `…-FORWARD-USR-…`, Gruppen `…-FORWARD-GRP-…`. Es gibt keinen Passthrough mehr.

### ⚠️ M-9: Query-Kosten
**Behoben:** Paginierte Listen multiplizieren die Kindkosten jetzt mit `effectiveRows`.

**Offen:** Unpaginierte Relationslisten (`isList` ohne `first`/`limit`) werden weiter mit pauschal +5 und additiv bewertet (`QueryCostAnalyzerRule.cs:300-309`).

### ⚠️ M-10: NuGet-Audit
**Behoben:**
- `NuGetAudit` und `NuGetAuditMode=all` sind aktiv.
- NU1901–1904 sind aus `NoWarn` entfernt. Dadurch bricht der Build bei bekannten Schwachstellen.

**Offen:**
- Es gibt keine Lock-Files (`RestorePackagesWithLockFile`).
- Es gibt keinen `dotnet list package --vulnerable` Schritt in der CI.

### ❌ Low 1–17
Keine der Low-Dateien wurde geändert, darunter:
- `FusionGatewayExtensions`
- `TrafficShadowing*`
- `BasicAuthAttemptGuard`
- `JwtSocketTokenValidator`
- `IncrementalDeliveryMiddleware`
- `GatewayMcpQueryExecutor`
- `.github/workflows`
- `deploy/`

Beispiel: `ArrowFlightSqlServer.cs:44` enthält weiterhin `"default-secret"`.

---

## Neu eingeführte Findings

### 🆕 N-1 (Medium): Data-Owner-Prüfung per String-Präfix umgangen
**Ort:** `SqliteGovernanceRepository.Catalog.cs:690-694`

```csharp
if (approverSid.Value.StartsWith("ITSM", OrdinalIgnoreCase) ||
    approverSid.Value.StartsWith("S-1-5-21-ITSM-", OrdinalIgnoreCase)) return true;
```

**Problem:** `IsAuthorizedApproverForTableInternalAsync` wird für **alle** Genehmigungen genutzt, auch für die GraphQL-Mutation mit der SID des eingeloggten Nutzers. Jede Identität, deren SID mit „ITSM" beginnt, gilt damit für jede Tabelle als Data Owner. Das betrifft zum Beispiel:
- Basic-Auth-Nutzer mit konfigurierter SID `ITSM…`,
- ein `sub`/`NameIdentifier` mit diesem Präfix bei IdPs mit frei wählbaren Kennungen.

Auch für echte ITSM-Freigaben entfällt die Prüfung, ob der Approver Data Owner der Tabelle ist.

**Fix:**
- Den Bypass entfernen.
- ITSM-Freigaben über einen expliziten Parameter oder eine eigene Methode kennzeichnen, nicht über die Form der SID.
- Den ITSM-Approver auf einen Owner oder Delegate der Tabelle mappen.

### 🆕 N-2 (Low, latent): Consent-Aktivierung akzeptiert Status `APPROVED`
**Ort:** `SqliteGovernanceRepository.Consent.cs:743-766`

**Problem:** `ActivateConsentAsync` erlaubt jetzt auch Anträge mit Status `APPROVED`. Der Doppel-Check zählt nur **nicht widerrufene** Consents. Ein erneuter Aufruf nach einem Widerruf würde deshalb einen neuen aktiven Consent anlegen.

**Aktuelle Erreichbarkeit:** Der ITSM-Pfad ruft die Methode nur direkt nach einem erfolgreichen Approval-Schritt auf, ein Replay scheitert am Status. Die Lücke ist also latent.

**Fix:** Entweder die Aktivierung atomar im Approval-Schritt durchführen, oder für jede `consent_request_id` (unabhängig von `is_revoked`) nur eine Aktivierung zulassen.

---

## Empfohlene nächste Schritte
1. **M-1:** `TenantAdmin` darf nicht mehr `GovernanceAdmin` implizieren. Ein Test für `IsGlobalGovernanceAdmin` mit dem Claim `GatewayAdmin` muss `false` ergeben.
2. **N-1:** ITSM-Präfix-Bypass entfernen.
3. **H-2-Nebenwirkung:** `JwtSocketTokenValidator` über den `ConfigurationManager` reparieren.
4. **M-4 DP-Reset, M-2, M-3:** Identitäts- und Tenant-Bindung sauber machen.
5. Low-Liste abarbeiten.
