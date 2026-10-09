# Feature Request: Datenquellen aus Swagger anlegen und Freigaben per MCP steuern (Administratoren)

**Stand:** 09.10.2026, geprüft gegen den Quelltext von `v1.1.5` (nicht im laufenden Container ausprobiert, wo nicht anders vermerkt).
**Anlass:** Der PoC „Citizen Dev“ (Talos) will, dass Administratoren im Chat eine Swagger-Beschreibung samt Zugangsdaten eingeben, daraus in Autheris automatisch eine Datenquelle wird, und dass sie Freigaben in einem Satz erteilen („die Schnittstelle soll für david und philipp freigegeben werden, david sieht alle Spalten unmaskiert, philipp nur den Kranstatus“). Das Gegenstück auf Seite Talos steht in `docs/anforderung-admin-chat-datenquellen.md` im PoC.
Fortsetzung von [Befunde v1.1.5](2026-10-09-poc-befunde-v1-1-5.md), [R-52 Klartext je Person](2026-10-09-feature-request-klartext-je-person.md) und [R-53 Maskierungsregeln](2026-10-09-feature-request-maskierungsregeln.md). IDs ab R-54.

## 1. Zielbild

1. Ein Administrator übergibt eine OpenAPI-/Swagger-Beschreibung und Zugangsdaten. Autheris legt daraus Datensätze an, **noch gesperrt**, und meldet, was es gefunden hat.
2. Der Administrator gibt die Datensätze frei. Erst dann tauchen sie in `list_datasets`, GraphQL und OData auf.
3. Der Administrator erteilt Freigaben je Person und Spalte (Klartext, maskiert, kein Zugriff) und gegebenenfalls Zeilenfilter, über REST **und** über MCP-Werkzeuge.
4. Jede Änderung ist im Audit mit der Person, dem Weg (REST/MCP) und dem Auftrag nachvollziehbar.

## 2. Was Autheris heute schon kann

| Baustein | Stand in v1.1.5 | Quelle |
|---|---|---|
| OpenAPI einlesen | `POST /api/governance/catalog/ingest-openapi` (Rollen `GovernanceAdmin`, `SchemaPublisher`), Spec als Body (bis 20 MB), `domain` und `baseUrl` als Parameter; legt Tabellen als `HttpDeclarative` an, **inaktiv** (SEC M-30), Audit `OPENAPI_CATALOG_INGESTED` | `GovernanceEndpoints.cs`, `OpenApiIngestionService.cs` |
| Aufruf der Fremd-API | `DeclarativeHttpDataSourceExecutor` kennt `None`, `StaticApiKey`, `ClientCredentials`, `ForwardBearerToken`; Geheimnis über Namen (`ApiKeySecretName`) und `SecretReferenceResolver` | `HttpEndpointDescriptor.cs`, `DeclarativeHttpDataSourceExecutor.cs` |
| MCP | Nur lesend: `list_datasets`, `describe_dataset`, `sample_rows`, `query_graphql` | `McpDatasetTools.cs` |
| Freigaben | GraphQL `RequestTableAccess` / `ApproveConsentRequest` (Antrag und Genehmigung); Spaltenstufen Mask/Clear/Deny und Zeilenfilter liegen in `CONSENT_COLUMN_RULES` und `CONSENT_ROW_FILTERS` | `MutationTypes.cs`, `ConsentModels.cs` |
| Rollen | `ClusterAdmin`, `TenantAdmin`, `GovernanceAdmin`, `SchemaPublisher`, `DataOwner` u. a. | `GatewayRole.cs` |

## 3. Lücken

| Lücke | Beleg |
|---|---|
| Der Import setzt `AuthMode = None` fest. Zugangsdaten lassen sich beim Anlegen nicht angeben; sie gehen nur über Konfiguration der Instanz. Eine Quelle, die Anmeldung braucht, ist so nicht ohne Eingriff am Server nutzbar | `OpenApiIngestionService.cs:261` |
| Es gibt keine Verwaltung der Geheimnisse (anlegen, ersetzen, löschen) und keinen Verbindungstest | Quelltext: nur `SecretReferenceResolver.Resolve` (lesend) |
| `ClientCredentials` verwendet das Geheimnis als fertigen Bearer-Token; einen Token-Abruf mit Client-ID und Secret gibt es nach Quelltext nicht | `DeclarativeHttpDataSourceExecutor.cs:522-527` |
| Eingelesen wird nur `components.schemas` (OpenAPI 3). Swagger 2.0 (`definitions`, `host`, `basePath`) hat keinen Pfad: Meldung „no components.schemas“. Alle Aufrufe sind `GET` | `OpenApiIngestionService.cs:129, 260, 287` |
| Die Spec muss als Body kommen; Abruf von einer URL fehlt | `GovernanceEndpoints.cs:86` |
| Es gibt keinen Endpunkt zum Freigeben (Aktivieren) eines Datensatzes für Administratoren; ob es einen über die Katalogpflege gibt, habe ich nicht gefunden | nicht belegt |
| Freigaben je Person und Spalte erteilt kein Administrator über eine Schnittstelle. Der PoC schreibt dafür direkt in die SQLite-Datei (`autheris/scripts/grant_row_scoped_user.py`) | PoC; siehe R-52 |
| Keine schreibenden MCP-Werkzeuge. Namen wie „david“ lassen sich nicht in eine Kennung (SID) auflösen | `McpDatasetTools.cs` |
| Der Import kann Regeln nur ergänzen, nicht entfernen (B-02); Freigaben zurückzunehmen geht damit nur über den Widerruf der Einwilligung | Befunde v1.1.5 |

## 4. Anforderungen

### R-54 Datenquelle in einem Schritt anlegen (Muss)
Ein Endpunkt, z. B. `POST /api/governance/datasources`, nimmt entgegen:
- `name`, `domain`,
- die Spec als Body **oder** als `specUrl` (Abruf durch Autheris unter der Egress-Richtlinie `EgressUrlPolicy`, ohne Zugangsdaten der Quelle an fremde Hosts zu senden),
- `baseUrl` (Überschreibung von `servers[0]`),
- `auth` (R-55),
- `dryRun` (Vorschau ohne Speichern).

Antwort: Quell-ID, angelegte und übersprungene Datensätze mit Spaltenzahl, Warnungen, Zustand je Datensatz (`inactive`), Hinweis auf nicht unterstützte Teile. Rollen: `GovernanceAdmin` und `SchemaPublisher` wie heute; ein Mandantenadministrator nur für die eigene `domain`. Der bestehende `ingest-openapi` bleibt für Rückwärtsverträglichkeit.
**Abnahme:** Spec und Zugangsdaten gehen in einem Aufruf ein; nach dem Aufruf sind die Datensätze angelegt, aber für niemanden sichtbar (auch nicht für den Anleger) und laufen nach der Freigabe ohne Eingriff am Server.

### R-55 Zugangsdaten sicher speichern und verwenden (Muss)
- **Zugangsart aus der Spec ableiten.** Der Import liest `components.securitySchemes` (OpenAPI 3) bzw. `securityDefinitions` (Swagger 2.0) und schlägt die Art vor; die Person gibt nur noch die Werte ein. Heute wertet der Einleseservice `securitySchemes` nicht aus (kein Treffer im Quelltext außer der eigenen Spec von WebSQL).
- Der Aufruf nimmt `auth` entgegen. Eine Beschreibung statt fester Liste: `type` = `apiKey` (Ort `header`, `query` oder `cookie`, Name), `http` (`basic`, `bearer`), `oauth2` (`clientCredentials` mit `tokenUrl`, `clientId`, `clientSecret`, `scope`), jeweils mit Wert(en). Heute kennt der Executor nur Header-Schlüssel und ein fertiges Token (`HttpAuthMode`).
- Der Wert geht **in den Geheimnisspeicher** (`IKeyVaultSecretProvider`; Dev: verschlüsselte Ablage), die Quelle behält nur die Referenz. Klartext steht weder in der Governance-DB noch im Audit, in Protokollen, in Antworten oder in Fehlermeldungen (bestehende Regel Review G5 gilt weiter).
- Ersetzen und Löschen des Geheimnisses ohne neue Spec; Antwort zeigt nur „gesetzt/nicht gesetzt“ und Zeitpunkt.
- `ClientCredentials` ruft den Token selbst ab, hält ihn bis zum Ablauf im Speicher und erneuert ihn.
- Verbindungstest `POST …/datasources/{id}/test` (ein harmloser Aufruf, keine Zeilen im Ergebnis, nur Status und Latenz).
**Abnahme:** Ein Test legt eine Quelle mit Schlüssel an und prüft, dass der Schlüssel weder in `GET`-Antworten noch im Audit noch in den Protokollen vorkommt.

### R-56 Swagger 2.0 und mehr vom OpenAPI-Umfang (Soll)
- Swagger 2.0 lesen (`host`, `basePath`, `schemes`, `definitions`) oder zumindest sauber mit Meldung „Version nicht unterstützt“ ablehnen statt „keine Schemas“.
- Datensätze nicht nur aus `components.schemas`, sondern aus den `GET`-Pfaden mit Listen-Antwort (`array` von Objekten); Pfadparameter und `JsonRootPath` aus der Antwortform ableiten.
- **Seitenweiser Abruf.** Der deklarative Executor schickt **eine** Anfrage und liest **eine** Antwort (`ExecuteSingleRequestAsync`); `HttpBatchType` betrifft nur Schlüssellisten, nicht das Blättern. Quellen mit mehreren Seiten liefern damit nur die erste. Gewünscht: je Quelle `paging` (`offset/limit`, `page/size`, `cursor`, `nextLink`, Parameternamen, Pfad zum nächsten Zeiger, Obergrenze Seiten/Zeilen) und feste Zusatzparameter und -kopfzeilen (z. B. API-Version).
- Schreibende Operationen (`POST`, `PUT`, `DELETE`) werden **nicht** übernommen und im Ergebnis als „übersprungen“ aufgeführt.
**Abnahme:** Eine Beispiel-Spec in Swagger 2.0 und eine in OpenAPI 3 ohne `components.schemas` ergeben Datensätze oder eine klare Ablehnung.

### R-57 Strenge Voreinstellung für neue Quellen (Muss)
Neue Datensätze aus fremden APIs starten gesperrt und mit der strengsten sinnvollen Einstufung: Tabelle `CONFIDENTIAL` oder höher, alle Spalten sensibel und **maskiert**, bis ein Administrator etwas anderes festlegt. Ein Hinweis nennt Spalten, deren Namen auf Personenbezug deuten (`name`, `email`, `phone`, `iban`, `birth`, `address`, `lat`, `lon`). Eine erneute Einlesung lockert nichts (SEC M-30 / `CatalogGovernanceRatchet` bleiben).
Für Quellen ohne Zeilenfilter-Möglichkeit (Fremd-API kann nicht nach Person filtern) meldet Autheris das ausdrücklich: „Zeilenfilter nicht erzwingbar; Freigabe gilt für alle Zeilen“, bevor ein Administrator freigibt.

### R-58 Datensätze freigeben und sperren (Muss)
`PUT /api/governance/datasets/{id}/state` mit `active` / `inactive`, Rolle `GovernanceAdmin` oder `DataOwner` der Quelle. Optional konfigurierbar: Vier-Augen-Prinzip (bestehende HitL-Endpunkte `/api/governance/hitl/*`), sodass die Person, die die Quelle angelegt hat, nicht allein freigeben kann. Audit `DATASET_ACTIVATED`/`DATASET_DEACTIVATED` mit Akteur.
**Abnahme:** Vor der Freigabe liefert `list_datasets` die Quelle für niemanden; danach nur für Personen mit Einwilligung (R-59).

### R-59 Freigaben je Person und Spalte durch Administratoren (Muss)
Ein REST-Endpunkt und eine GraphQL-Mutation, die dasselbe tun wie `grant_row_scoped_user.py`, aber als Schnittstelle:
```
PUT /api/governance/datasets/{id}/access/{principal}
{ "columns": { "*": "mask", "crane_status": "clear" },
  "rowFilter": "…",               // optional, über virtuellen Filter oder Profil
  "validUntil": "2026-12-31",     // optional
  "reason": "Freigabe Betrieb" }
```
- Stufen `clear`, `mask`, `deny`; `*` für alle Spalten; Spalten, die es nicht gibt, ergeben 400 (nicht stilles Ignorieren).
- Wirkung sofort ohne Neustart; Widerruf und Änderung ebenso (`DELETE`, `PUT`).
- `GET …/access` liefert die geltende Freigabe je Person und Spalte (für Rückfrage und Kontrolle).
- Voraussetzung: Klartext je Person nach R-52 (gilt dort für jede Spalte, auch bei Regeln der Klasse `CONFIDENTIAL`/`SECRET`; ausdrückliche Freigabe nötig).
- Die Person ist über `principal` als Anmeldename oder SID angegeben; Auflösung siehe R-61.
**Abnahme:** Das Beispiel oben setzt für `david` alle Spalten auf Klartext und für `philipp` nur `crane_status` auf Klartext, den Rest maskiert; ein Test mit beiden Nutzern bestätigt es über WebSQL, GraphQL und OData.

### R-60 Schreibende MCP-Werkzeuge für Administratoren (Muss)
Neue Werkzeuge, **nur sichtbar und aufrufbar** für Konten mit der Rolle `GovernanceAdmin` (oder einer neuen Rolle `McpAdmin`) und einem Token, das ausdrücklich für Administration ausgestellt ist:

| Werkzeug | Wirkung |
|---|---|
| `admin_register_datasource` | wie R-54, **ohne** Geheimnisse im Argument; Geheimnis über `secretRef` (R-55) |
| `admin_set_dataset_state` | R-58 |
| `admin_resolve_principal` | Name → Kennung(en); mehrdeutig ergibt Liste statt Raten |
| `admin_get_access` | geltende Freigaben eines Datensatzes (R-59) |
| `admin_plan_access` | nimmt Freigaben wie in R-59 für mehrere Personen entgegen und liefert einen **Plan** (Vorher/Nachher je Person und Spalte, Warnungen) mit `planId` und Prüfsumme; ändert nichts |
| `admin_apply_access` | wendet einen Plan an, nur mit `planId` und Bestätigungsnachweis (R-62) |
| `admin_revoke_access` | wie `DELETE` in R-59 |

Sicherheitsregeln:
- **Eigener Endpunkt** (z. B. `/mcp/admin`) oder eigener Werkzeugsatz je Token: Ein Agent mit normalem Datenzugriff, etwa der App-Agent im Talos-Sandbox-Container, sieht diese Werkzeuge nie. Fehlt die Rolle, antwortet `tools/list` ohne sie und der Aufruf mit 403.
- Der Guardrail `AiDataGuardrailService` (Prompt-Injection-Prüfung, ABAC, Audit) gilt auch hier; Texte aus fremden Specs und Datenfeldern sind **nicht vertrauenswürdig** und dürfen kein Admin-Werkzeug auslösen. Empfohlen: In einer Admin-Sitzung sind keine Werkzeuge verfügbar, die Zeilen lesen (`sample_rows`, `query_graphql`).
- Werkzeuge, die schreiben, sind idempotent über `idempotencyKey` und je Konto ratenbegrenzt.
**Abnahme:** Ein Konto ohne Admin-Rolle sieht in `tools/list` kein `admin_*`; mit Rolle zeigt `admin_plan_access` den Plan, ohne etwas zu ändern; `admin_apply_access` ohne gültige Bestätigung ergibt 403.

### R-61 Personen auflösen (Muss)
`admin_resolve_principal` und `GET /api/governance/principals?q=` liefern zu „david“, „philipp“ oder einem Teil des Namens die Kennung (SID/Objekt-ID), Anzeigename, Gruppen und ob die Person bereits Zugang hat. Quelle: konfigurierte Nutzer (PoC), Entra ID/Microsoft Graph (MVP). Unscharfe Treffer („phillip“ gegen „philipp“) werden als Vorschlag mit `exact: false` gemeldet, nie stillschweigend gewählt. Es gelten nur Personen innerhalb des Mandanten.

### R-62 Bestätigung außerhalb des Modells (Muss)
Ein Modell darf Freigaben vorbereiten, aber nicht allein ausführen. `admin_apply_access` verlangt einen **Bestätigungsnachweis**, den nur die Administrator-Oberfläche erzeugen kann (kurzlebiger, an `planId` und Person gebundener Token aus `POST /api/governance/plans/{planId}/confirm`, aufgerufen mit der Sitzung des Administrators, nicht mit dem MCP-Token). Damit kann auch ein getäuschtes Modell keine Freigabe auslösen. Alternative, falls das zu schwer ist: Vier-Augen über die bestehenden HitL-Endpunkte.

### R-63 Audit und Nachvollziehbarkeit (Muss)
Jede Änderung (Quelle angelegt, Geheimnis gesetzt/ersetzt, Datensatz aktiviert, Freigabe erteilt/geändert/entzogen) schreibt einen Audit-Eintrag mit Akteur, Weg (`rest`/`mcp`), `planId`, Vorher/Nachher und einer Korrelations-ID, die der Client mitgeben kann (z. B. Chat-Auftrag). Der Wortlaut des Auftrags darf mitgegeben werden (`reason`), Geheimnisse nie. Das Audit lässt sich nach Person und Datensatz abfragen („Wer hat philipp wann welche Spalten freigegeben?“). Die Audit-Kette bleibt unverändert signiert.

### R-64 Freigaben lassen sich zurücknehmen (Muss)
Der Import der dbt-Governance (B-02) und die Freigaben aus R-59 dürfen sich nicht gegenseitig überschreiben: Freigaben je Person stammen aus R-59, Maskierungsregeln aus dem Import. Wer eine Regel entfernen will, kann das über einen Modus `replace` oder ein ausdrückliches Löschen (B-02). Ohne das bleibt jede Lockerung ein Eingriff in die Datenbank.

### R-65 Plugin-Weg für Quellen, die sich nicht beschreiben lassen (Soll)
**Befund:** Ein Plugin (`DataSourceType.HttpPlugin`) ist eine .NET-Assembly mit `IHttpDataSourcePlugin` (`ConfigureServices`, `ExecuteAsync`). Sie wird beim Start aus einem Verzeichnis geladen, jede Datei gegen SHA-256-Hashes in der Konfiguration (`Plugins:TrustedPluginHashes`) geprüft, außerhalb von Development verpflichtend (`PluginManager.cs`, `PluginTrustList.cs`). Das Plugin ruft die Fremd-API selbst (über einen gegen SSRF geschützten HttpClient); Maskierung, Zeilenfilter und Audit setzt danach das Gateway durch. Eine Tabelle verweist über `PluginName`/`SourceName` auf das Plugin. Der Import aus Spec (R-54) legt nur `HttpDeclarative` an; einen Weg, eine Tabelle einem vorhandenen Plugin zuzuordnen, habe ich nicht gefunden.
**Folge:** Ein Plugin kann nie im Chat entstehen (Code, Auslieferung, Hash, Neustart); es bleibt Aufgabe des Plattform-Teams.
**Gewünscht:**
- `POST /api/governance/datasources` mit `plugin: "<name>"` ordnet die Datensätze einem **bereits geladenen** Plugin zu (Liste `GET /api/governance/plugins` mit Name, Version, Hash, Beschreibung der erwarteten Einstellungen).
- Ein Plugin deklariert seine Einstellungen und Geheimnisse (Namen, Typen, Pflicht); Werte gehen wie in R-55 in den Geheimnisspeicher. Heute holt sich ein Plugin Konfiguration über `IConfiguration`, also über den Server.
- Meldet ein Administrator eine Quelle, die weder deklarativ noch über ein vorhandenes Plugin geht, erzeugt Autheris einen **Bedarf** (Eintrag mit Spec und Begründung für das Plattform-Team), statt etwas Halbes anzulegen.
**Abnahme:** Ein Beispiel-Plugin mit eigener Signierung der Anfrage wird über die Schnittstelle einer Quelle zugeordnet; ohne geladenes Plugin ergibt der Aufruf 400 mit klarer Meldung.

#### R-65b Adapter als eigener Dienst statt DLL (Soll, bevorzugt)
Statt einer geladenen Assembly ruft Autheris einen **Adapter-Dienst** über HTTP oder gRPC auf. Der Dienst übersetzt, was die Fremd-API verlangt (Anmeldeverfahren, Parameter, Blättern, Signatur), und hält deren Zugangsdaten. Vorteile gegenüber der DLL: keine Auslieferung in den Autheris-Container, kein Neustart und keine Hash-Pflege je Änderung, beliebige Sprache, getrennte Prozesse und Rechte; ein Fehler im Adapter legt Autheris nicht lahm.
- **Heute schon möglich (HTTP):** Der Adapter bietet eine einfache API mit OpenAPI-Beschreibung, Autheris registriert sie wie jede andere Quelle (`HttpDeclarative`). Das gilt, sobald R-55/R-56 (Zugangsart, Blättern) bei Autheris erfüllt sind; die Zugangsdaten der **Fremd-API** liegen im Adapter, nicht in Autheris.
- **Adapter-Vertrag (neu):** Ein kleiner, festgelegter Vertrag, damit Adapter austauschbar sind und Autheris keine Sonderfälle braucht. Als `.proto` (gRPC) und als gleichwertige HTTP-Form:
  - `Describe()` liefert Datensätze, Spalten mit Typ, Primärschlüssel, Hinweise auf personenbezogene Spalten, unterstützte Filter und Sortierung, Seitenmodell;
  - `Query(dataset, columns, filter, orderBy, pageToken, limit, caller)` liefert Zeilen als Strom oder Seite mit nächstem Zeiger;
  - `Health()`.
  Der Vertrag gehört dem Gateway; die Beschreibung ersetzt die Ableitung aus Swagger, wo die Fremd-API keine brauchbare hat.
- **Spec kommt vom Adapter, Autheris lädt sie selbst.** Der Adapter liefert seine Beschreibung (`Describe()` bzw. `GET /spec`, OpenAPI oder die Beschreibung aus dem Vertrag) über einen Endpunkt. Autheris holt sie
  - **bei der Eintragung** des Adapters (einmalig, Grundlage der Datensätze, die gesperrt angelegt werden, R-54/R-57),
  - **bei jedem Start** (und auf Wunsch per `POST …/adapters/{name}/refresh`) zur Nachkontrolle.
  Die Spec trägt eine Version und einen Hash. Bei der Nachkontrolle vergleicht Autheris mit dem Stand der Eintragung:
  - **unverändert:** nichts passiert;
  - **neue Spalten oder Datensätze:** werden **nicht** übernommen, sondern als „Änderung wartet auf Freigabe“ gemeldet (passt zur bestehenden Regel SG-11: bei vorhandenen Tabellen keine neuen Spalten durch Einlesen); ein Administrator übernimmt sie ausdrücklich, danach gelten sie gesperrt und strikt maskiert (R-57);
  - **entfernte Spalten oder Datensätze oder geänderte Typen:** der betroffene Datensatz wird bis zur Prüfung **gesperrt** (fail-closed) und im Katalog als „veraltet“ markiert, mit Warnung im Audit;
  - **Adapter nicht erreichbar beim Start:** Autheris startet trotzdem mit dem gespeicherten Stand; die Datensätze der Quelle antworten mit 503, bis der Adapter wieder erreichbar ist. Ein Fehler eines Adapters darf den Start von Autheris nicht verhindern.
  Die Spec darf **nie** Rechte oder Regeln lockern: Einstufung, Maskierungsregeln und Freigaben werden von Autheris gehalten, nicht vom Adapter geliefert (`CatalogGovernanceRatchet`, SEC M-30). Auch der Wert `baseUrl` aus der Spec wird nicht übernommen; maßgeblich ist die zugelassene Adresse des Adapters (sonst könnte ein Adapter Autheris auf einen anderen Host lenken).
  Größe (bis 20 MB wie bei `ingest-openapi`) und Zeit sind begrenzt; die Spec ist **nicht vertrauenswürdig** wie jede andere Eingabe (Namen, Beschreibungen).
- **Vertrauen:** Autheris ruft nur Adapter auf, die ein Administrator mit Rolle `ClusterAdmin` in einer **Liste zugelassener Adapter** eingetragen hat (Name, Adresse, Zertifikat/Fingerabdruck). Verbindung mit mTLS oder einem Dienst-Token, und der Adapter nimmt nur Aufrufe von Autheris an. Die Egress-Richtlinie gilt für die Adresse. Eine beliebige Adresse eines Administrators im Chat reicht nicht (sonst wäre es ein Umweg um SSRF-Schutz).
- **Wer entscheidet über Rechte:** Der Adapter prüft keine Berechtigungen. Autheris übergibt die Kennung des Aufrufers nur zur Nachvollziehbarkeit (`caller`) und wendet Maskierung, Zeilenfilter und Audit auf das Ergebnis an. Filter, die der Adapter nicht ausführen kann, wendet Autheris nach dem Abruf an; Quellen, bei denen das zu teuer wäre, meldet `Describe()` ohne Filterunterstützung, und Autheris warnt (R-57).
- **Eintragung durch die Plattform:** Ein Dienstkonto der Plattform (Talos-Deployer) darf Adapter **nach menschlicher Freigabe** über `POST /api/governance/adapters` eintragen, ändern und entfernen (Name, Adresse, Zertifikat, Vertragsversion, Besitzer); alles andere bleibt `ClusterAdmin`. Das Konto hat kein Recht auf Datenzugriff und keines auf Freigaben (R-59). Audit wie R-63. Hintergrund: Talos kann Adapter aus dem Chat erzeugen (Talos T-12); Autheris soll den Adapter erst nach dieser Eintragung aufrufen.
- **Grenzen:** Zeitlimit, Obergrenze für Zeilen und Nachrichtengröße, Abbruch bei Überschreitung.
- **Zusammenspiel mit R-66:** Ein gRPC-Adapter nach diesem Vertrag ist der erste Anwendungsfall des Quelltyps `GrpcDeclarative`; eine beliebige Fremd-gRPC-Quelle mit eigener `.proto` ist der zweite.
**Abnahme:** Ein Beispiel-Adapter (HTTP) und einer (gRPC), die dieselbe Fremd-API übersetzen, werden eingetragen, liefern nach Freigabe Zeilen, ein nicht eingetragener Adapter wird abgelehnt, und ein Adapter, der mehr Zeilen liefert als erlaubt, wird abgebrochen.
**Offen:** Ob Autheris den Vertrag selbst definiert oder einen vorhandenen Standard übernimmt (Arrow Flight, Trino-Connector, OData); ich habe das nicht geprüft.

### R-66 gRPC-Quellen (Soll)
**Befund:** Im Quelltext (`src`, `*.cs`, `*.csproj`) kommt gRPC nicht vor. `DataSourceType` kennt `Sql`, `HttpDeclarative`, `HttpPlugin`, `LakehouseIceberg`, `LakehouseDelta`. In den Feature-Dokumenten steht gRPC nur für Arrow Flight (Weg der Clients zu Autheris, `f-data-04`) und als geplanter Coprocess für Middleware (`p09`); keines davon macht eine gRPC-Schnittstelle zur Datenquelle. Ein Plugin könnte gRPC selbst sprechen, bekommt aber nur einen HTTP-Client (`PluginExecutionContext.HttpClientFactory`) und müsste einen eigenen Kanal aufbauen; die Schutzregeln gegen SSRF gelten dafür nicht.
**Gewünscht:** Ein Quelltyp `GrpcDeclarative`, der aus einer **Protobuf-Beschreibung** (`.proto`, `FileDescriptorSet` oder gRPC-Reflection) Datensätze ableitet:
- Eingelesen werden nur Methoden ohne Nebenwirkung: Unary-Methoden und Server-Streaming mit Listen-Antwort, vorzugsweise erkennbar an Namen wie `List*`/`Get*` oder einer Kennzeichnung durch die Person. Alles andere wird als „übersprungen“ gemeldet.
- Antwortnachrichten werden zu Spalten (verschachtelte Felder flach mit Pfad, `repeated` als Zeilen oder JSON, `oneof`/`Any` mit Warnung).
- Zugangsdaten wie in R-55: Metadaten-Header (`authorization`, API-Schlüssel), TLS und mTLS (Zertifikate im Geheimnisspeicher), OAuth-Token.
- Gleiche Schutzregeln wie bei HTTP: Egress-Richtlinie für Ziel-Host und -Port, Zeitlimit, Obergrenze für Zeilen und Nachrichtengröße, Seitenweise Abrufe über `page_token` oder Streaming mit Abbruch.
- Maskierung, Zeilenfilter und Audit setzt wie bisher das Gateway durch.
- `dryRun` und Verbindungstest wie in R-54/R-55; ohne Spec-Datei über Server-Reflection, wenn die Quelle sie anbietet (Reflection nur auf ausdrücklichen Wunsch, weil sie die Schnittstelle offenlegt).
**Abnahme:** Ein Beispiel-Dienst mit `ListCranes` (Server-Streaming) wird aus der `.proto` als Datensatz angelegt, ist nach Freigabe abfragbar, und ein Aufruf mit unerlaubtem Host oder zu großer Antwort wird abgelehnt.
**Hinweis:** Bis R-66 vorhanden ist, bleibt gRPC ein Fall für R-65 (Plugin des Plattform-Teams).

## 5. Beispiel durchgespielt

> „Die Schnittstelle soll für david und philipp freigegeben werden. david soll alle Spalten unmaskiert sehen, philipp nur den Kranstatus.“

1. Modell ruft `admin_resolve_principal("david")` und `("philipp")`; je ein eindeutiger Treffer.
2. `admin_get_access(dataset)` zeigt, dass der Datensatz gesperrt und niemand eingetragen ist.
3. `admin_plan_access`: `david: * → clear`, `philipp: * → deny, crane_status → clear`. Der Plan nennt, dass die Spalten `lat`, `lon` und `owner_email` als personenbezogen gelten und dass `philipp` sie nicht erhält, dass die Fremd-API keinen Zeilenfilter zulässt (R-57).
4. Die Administrator-Oberfläche zeigt den Plan als Tabelle; der Administrator bestätigt (R-62).
5. `admin_set_dataset_state(active)` und `admin_apply_access(planId)`; Audit-Einträge für beide Schritte.
6. Kontrolle: Abfragen als `david` und `philipp` bestätigen Spalten und Maskierung.

## 6. Reihenfolge

1. R-55, R-54, R-58 (Quelle anlegen und freigeben, ohne MCP).
2. R-59, R-61 (Freigaben als Schnittstelle; Voraussetzung R-52).
3. R-60, R-62, R-63 (MCP und Sicherheit).
4. R-56, R-57, R-64, R-65, R-66 (Qualität der Einlesung, strenge Voreinstellung, Rücknahme, Plugin-Zuordnung, gRPC).

## 7. Grenzen
- Ich habe den Quelltext von `v1.1.5` gelesen (Endpunkte, Ingestion, Executor, MCP-Werkzeuge); **keine** der genannten Wege im laufenden Container ausprobiert. Die Aussage „Swagger 2.0 wird nicht gelesen“ stützt sich auf das Fehlen von `definitions`/`swagger` im Einleseservice.
- Gelesen habe ich `PluginHttpDataSourceExecutor`, `IHttpDataSourcePlugin`, den Anfang von `PluginManager` (Laden, Vertrauensliste) und den Executor bis zur Weiterleitung. Nicht gelesen: das Laden der Konfiguration eines Plugins, der Rest des Executors (Fehlerbehandlung, Batching im Detail) und ob ein Plugin zur Laufzeit nachgeladen werden kann (nach Quelltext nur beim Start, nicht belegt).
- Ob ein Endpunkt zum Aktivieren einzelner Datensätze existiert, habe ich nicht gefunden; falls er im Verwaltungsteil liegt, den ich nicht gelesen habe, reduziert sich R-58 auf Dokumentation.
- Die Wirkung von `HttpDeclarative`-Quellen auf Zeilenfilter und Maskierung (greifen sie bei Fremd-APIs?) ist nicht geprüft; R-57 setzt voraus, dass die Spaltenmaskierung auch dort greift.
- Der Bestätigungsweg in R-62 ist ein Vorschlag; entscheidend ist das Ziel (kein Modell allein löst eine Freigabe aus), nicht die Form.
