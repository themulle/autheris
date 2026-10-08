# Virtuelle Filter mit Mustern: Entwurf

**Stand:** 08.10.2026, Entwurf zur Entscheidung. Nichts davon ist gebaut. Den Quellcode (a98200f) habe ich gelesen, aber nicht
gebaut oder getestet (kein .NET-SDK auf dem Rechner). Ausgangspunkt ist der „David-Fall“ im Talos-PoC.

## 1. Anlass

Nutzer David soll nur Daten von Kranen sehen, die noch nicht ausgeliefert sind:

```sql
select client_id
from conf.client
inner join md.crane on crane.serial_number = client.crane_serial_number
where crane.is_delivered is null
```

Das soll **einmal** als virtueller Filter (eine Art View) definiert und per Muster auf alle Objekte angewendet werden, die
eine passende Spalte haben, zum Beispiel `lwetem_prod.*.*.client_id`. Für jede passende Tabelle, View und jedes
Prozedur-Ergebnis gilt dann sinngemäß:

```sql
select * from (select * from fms.air1 where client_id in (<virtueller Filter>)) air1
```

## 2. Heute

- Zeilenfilter hängen in `CONSENT_ROW_FILTERS` **je Einwilligung und je Tabelle** (`ConsentRowFilter`, Typ
  `SubqueryCorrelated` mit `DependentTable`, `ForeignKeyColumn`, `PrimaryKeyColumn`, `SubqueryFilterPredicateJson`,
  `AdditionalHops`). `RowFilterSqlBuilder` und `AdvancedRlsFilterGenerator` bauen daraus `EXISTS` oder `IN`
  (`SubqueryStrategy`: `Exists`, `InCorrelated`, `In`).
- Das Muster gibt es nicht in Autheris, es steckt im PoC-Skript `autheris/scripts/grant_row_scoped_user.py`: Es geht alle
  Tabellen durch, erkennt `client_id` oder `crane_serial_number` und schreibt **je Tabelle eine Einwilligung mit
  Zeilenfilter** direkt in die SQLite-Datei (ohne Audit-Eintrag und ohne die API).
- Neue Tabellen im Katalog bekommen keinen Filter, bis jemand das Skript erneut laufen lässt. Zero Trust verhindert zwar
  den Zugriff ohne Einwilligung, aber der Filter ist Handarbeit je Tabelle.
- Mehrere ALLOW-Einwilligungen werden mit OR verknüpft; **eine** Einwilligung ohne Zeilenfilter hebt jeden Filter auf
  (`ConsentResolutionService`: `unconstrainedConsents`). Ein Filter, der immer gelten muss, lässt sich so nicht ausdrücken.

## 3. Vorschlag

### 3.1 Zwei neue Begriffe

**Virtueller Filter** (`VirtualFilter`): benannt, wiederverwendbar, ohne Bezug zu einer Tabelle.

| Feld | Bedeutung |
| :--- | :--- |
| `name` | `nicht_ausgelieferte_krane` |
| `key_columns` | `[client_id]` oder `[client_id, ts]`: Namen der Ausgabespalten der Abfrage; das Zielobjekt braucht Spalten **gleichen Namens** (oder eine Zuordnung `map`) |
| `valid_from`, `valid_to` | optional: Ausgabespalten mit einem Zeitfenster je Schlüssel (siehe 3.6) |
| `definition` | die Abfrage, anfangs strukturiert (siehe 3.4), später als SQL |
| `source` | Datenquelle (`lwetem_prod`); alle Tabellen der Abfrage müssen dort liegen |

**Bindung** (`FilterBinding`): wendet einen Filter auf eine Menge von Objekten an.

| Feld | Bedeutung |
| :--- | :--- |
| `filter` | Name des virtuellen Filters |
| `target` | Muster `quelle.schema.objekt[.spalte]`, siehe 3.2; fehlt es, gilt der Bereich (`scope`) des Profils |
| `object_kinds` | `table`, `view`, `procedure_result` (Standard: alle) |
| `time_column` | optional: Spalte des Objekts, die gegen das Zeitfenster des Filters geprüft wird (siehe 3.6) |

**Profil** (`AccessProfile`): fasst die Bindungen **eines Berechtigten** zusammen (Benutzer-SID oder Rolle, wie bei Einwilligungen).

| Feld | Bedeutung |
| :--- | :--- |
| `grantee` | Benutzer oder Rolle |
| `scope` | Bereich, für den das Profil gilt, als Muster `quelle.schema.objekt` (`lwetem_prod.*.*`) |
| `bindings` | die Filter mit je eigenem `target` und `time_column` |
| `uncovered` | `deny` oder `skip`: Was gilt für Objekte im Bereich, auf die **kein** Filter des Profils zutrifft (siehe 3.9); Vorschlag: `deny` |

`uncovered` gehört bewusst ans Profil und nicht an die einzelne Bindung: Ein Berechtigter braucht oft mehrere Filter, die einander ergänzen (einer
für Objekte mit `client_id`, einer für Objekte mit `crane_serial_number`). Würde jede Bindung die Objekte ohne ihre Spalten sperren, sperrte sie
auch die, die ein anderer Filter des Profils abdeckt.

### 3.2 Muster

Das Muster hat vier Segmente `quelle.schema.objekt.spalte`, getrennt durch Punkte. Ein Segment ist entweder

- ein **einfacher Name mit Platzhaltern** (`*` = beliebig viele Zeichen, `?` = ein Zeichen): `fms`, `*`, `client_*`, oder
- ein **regulärer Ausdruck in Klammern**, der das ganze Segment treffen muss: `(fms|tem)`, `(client_.*|device_id)`.

Getrennt wird nur an Punkten **außerhalb** von Klammern. Damit bleibt `lwetem_prod.*.*.client_id` wörtlich gültig, und ein echter
Ausdruck ist nur in Klammern möglich, genau wie vorgeschlagen. Ein reiner Ausdruck über den ganzen Pfad taugt nicht: Der Punkt wäre
Platzhalter *und* Trenner, `lwetem_prod.*.*.client_id` träfe auch `lwetem_prodXclient_id`, und ein Ausdruck wie `client_.*` ließe sich
nicht mehr vom Trenner unterscheiden. Namen mit Sonderzeichen (das Schema `LWEW2K\LWEMUM2`) brauchen als einfacher Name kein Escaping.

Zusätzlich gibt es die Schreibweise mit vier Feldern, die keine Trennerfrage kennt und für Werkzeuge gedacht ist:
`{ source: lwetem_prod, schema: "(fms|tem)", object: "*", column: client_id }`. Ein Objekt trifft, wenn das Muster auf eine seiner
Spalten passt. Beispiele: `lwetem_prod.(fms|tem).*.client_id`, `lwetem_prod.md.crane.serial_number`.

### 3.3 Wirkung: einschränkend, nicht erlaubend

Eine Bindung **gewährt nichts**. Sie schränkt ein, was die Einwilligungen ohnehin erlauben, und wird per **AND** an das Ergebnis
der Einwilligungen gehängt, nie per OR wie ein Consent-Filter. Das schließt die Lücke aus Abschnitt 2: Eine weitere
Einwilligung ohne Zeilenfilter kann den Filter nicht aufheben. In der Datenbanksprache entspricht das einer
*restrictive policy*.

```
sichtbar = (Consent-Zeilenfilter, wie heute)  AND  (client_id IN (Filter 1))  AND  (client_id IN (Filter 2)) ...
```

Mehrere Bindungen auf dasselbe Objekt wirken zusammen (AND). Ein Filter auf eine Spalte, die das Objekt nicht hat, trifft nicht.

### 3.4 Wo es greift

Die Kanäle lesen Zeilenfilter an vielen Stellen (`RowFilterSqlBuilder`, `RlsListener` für Trino, `StreamRlsPolicyEnforcer`,
`SqlProcedureRowScopeResolver`, `CasbinEnforcementService`). **Ungeprüft:** ob alle über `ConsentResolutionService` laufen; das ist vor
der Umsetzung für jeden Kanal nachzulesen. Wo es so ist, gehört der Eingriff **dorthin**, nicht in jeden Kanal: `TableAccessDecision` bekommt zusätzlich zum
Consent-Prädikat ein verpflichtendes Prädikat (`MandatoryRowPredicate`), das `RowFilterSqlBuilder` in dialektgerechtes SQL
umsetzt (Exists/In wie bisher). Dadurch ändert sich in den Kanälen nichts.

Der Filter-Teil ist ein Ausdruck, kein Join der Hauptabfrage; so bleibt die Form des Wunsches erhalten:
`select * from (select * from fms.air1 where client_id in (…)) air1`. Für Mehrfach-Hops nutzt er die vorhandene
`AdditionalHops`-Logik.

**Prozedur-Ergebnisse:** Ein Ergebnis einer gespeicherten Prozedur lässt sich nicht in SQL Server umschließen
(`select … from (exec …)` geht nicht). Ob `SqlProcedureRowScopeResolver` dafür taugt, habe ich nicht geprüft. Vorschlag: Prozeduren mit
deklarierter Ergebnisspalte `key_column` werden **nach** der Ausführung gefiltert (Wertemenge des Filters, zwischengespeichert,
Zeilen mit anderem Schlüssel entfallen); Prozeduren ohne diese Spalte werden für Berechtigte mit Bindung **abgelehnt**
(fail closed), nicht ungefiltert ausgeführt.

### 3.5 Definition der Abfrage

1. **Strukturiert (Stufe 1):** Startdokument für Tabelle, Join-Kette und `WHERE` aus Gleichheit und `IS [NOT] NULL`, also genau, was
   `ConsentRowFilter` heute kann. Der David-Fall passt: `conf.client` ⟕ `md.crane` auf `crane_serial_number = serial_number`,
   Bedingung `is_delivered IS NULL`.
2. **SQL (Stufe 2):** frei formuliertes korreliertes Prädikat mit `target` (siehe 3.7) oder eine `select`-Abfrage. Sie wird mit dem vorhandenen AST (`TrinoSqlEngine`) geparst und geprüft,
   bevor sie gespeichert wird: nur `SELECT`, genau eine Ausgabespalte, keine Parameter, keine Unterprogramme, nur Tabellen
   der Datenquelle, Berechtigungen des Autors auf diese Tabellen. Bei Fehlern bei der Auswertung gilt: **fail closed**
   (keine Zeilen).

### 3.6 Mehrere Schlüsselspalten und Zeitfenster

**Tupel-Vergleich ist nicht überall verfügbar.** `(client_id, ts) IN (select client_id, ts …)` kennen PostgreSQL, MySQL, Oracle,
SQLite und Trino; **SQL Server kennt es nicht**, und meines Wissens auch BigQuery nicht (nicht geprüft). Der Plan nimmt deshalb für
mehrere Schlüsselspalten immer die Form, die in jeder Datenbank geht und die Autheris als Standard schon erzeugt (`Exists`):

```sql
where exists (select 1 from (<virtueller Filter>) f
              where f.client_id = air1.client_id and f.ts = air1.ts)
```

Bei einer Spalte bleibt `IN` oder `Exists` frei wählbar (`SubqueryStrategy`); bei mehreren erzwingt der Dialekt `Exists`, wenn er kein
Tupel-`IN` kann.

**Gleichheit auf einem Zeitstempel ist selten gewollt.** Der übliche Fall ist ein *Zeitfenster*: David sieht Daten eines Geräts nur
**bis** zur Auslieferung, oder nur in den Zeiträumen, in denen eine Einwilligung des Kunden gilt. Dafür gibt der Filter neben `client_id`
ein Fenster aus, und die Bindung nennt die Zeitspalte des Ziels:

```sql
-- Filter
select c.client_id, cr.date_of_delivery as valid_to from conf.client c join md.crane cr on cr.serial_number = c.crane_serial_number
-- Bindung: time_column: ts
-- Wirkung: exists (… where f.client_id = air1.client_id and (f.valid_to is null or air1.ts < f.valid_to))
```

Das Modell kann das schon: `ConsentRowFilter` hat `TargetTemporalColumn`, `DependentValidFromColumn` und `DependentValidToColumn`, und
`AdvancedRlsFilterGenerator` erzeugt daraus `(valid_to IS NULL OR target < valid_to)`. Der Vorschlag hebt das nur von der einzelnen
Einwilligung auf den virtuellen Filter.

### 3.7 Allgemeine Form: korreliertes Prädikat mit `target`

Schlüsselspalten (3.6) und Zeitfenster sind Sonderfälle derselben Idee. Allgemeiner ist ein **Prädikat, das das geschützte Objekt unter
dem festen Namen `target` anspricht**:

```sql
-- Filter "nicht_ausgelieferte_krane_letzter_tag"
from conf.client client
join md.crane crane on crane.serial_number = client.crane_serial_number
where crane.is_delivered is null
  and target.client_id = client.client_id
  and target.ts between date_add('day', -1, current_timestamp) and current_timestamp
```

Autheris setzt daraus je Zielobjekt zusammen:

```sql
select * from (
  select * from fms.air1 as target
  where exists (select 1 from conf.client client
                join md.crane crane on crane.serial_number = client.crane_serial_number
                where crane.is_delivered is null
                  and target.client_id = client.client_id
                  and target.ts between date_add('day', -1, current_timestamp) and current_timestamp)
) air1
```

Das macht den Filter frei formulierbar: beliebig viele Schlüssel, Zeitfenster, Vergleiche, Bedingungen in einem Ausdruck (Trino-Syntax), ohne dass es
Felder wie `key_columns` oder `time_column` braucht. Diese bleiben als Kurzschreibweisen, die zu einem solchen Prädikat übersetzt
werden.

Was dabei zu klären ist:

- **Passende Objekte:** Welche Spalten des Ziels der Filter braucht, steht im Prädikat (`target.client_id`, `target.ts`). Autheris liest
  sie aus der geparsten Abfrage; ein Objekt trifft nur, wenn es **alle** hat. Das Muster der Bindung (3.2) grenzt zusätzlich ein, es
  muss die erste Spalte nicht mehr allein bestimmen.
- **Prüfung vor dem Speichern** (AST aus `TrinoSqlEngine`): nur lesende Ausdrücke, `target` ist reserviert und darf nicht neu
  definiert werden, nur Tabellen der Datenquelle, keine Unterprogramme, Funktionen nur aus der Liste des Dialekts. Dafür gibt es schon
  `SqlFunctionAllowlists` mit je einer Liste für SQL Server (enthält `getdate` und `dateadd`), PostgreSQL und andere, und eine Sperrliste
  als zweite Verteidigungslinie (`SqlFunctionPolicy`). Bei Fehlern in der Auswertung gilt: keine Zeilen.
- **Dialekt:** Autheris parst Trino-Syntax und erzeugt die Zieldialekte (`SqlServerDialectGenerator` und weitere). In Trino-Syntax
  heißt das Beispiel `target.ts between date_add('day', -1, current_timestamp) and current_timestamp`. Ob die Dialekt-Generatoren
  `date_add` nach `dateadd` übersetzen, habe ich nicht gefunden: Im Quelltext der Generatoren kommt nur `CURRENT_TIMESTAMP` vor. Und ob
  T-SQL-Schreibweise wie `dateadd(dd, …)` (Datumsteil als bloßes Wort) durch den Trino-Parser geht, ist ungeprüft.
  **Entschieden (08.10.2026): Filter werden in Trino-Syntax geschrieben** und je Datenquelle in deren Dialekt übersetzt. Die Übersetzung
  der Datumsfunktionen fehlt noch und steht als Aufgabe in 3.8. Eine Schreibweise im Dialekt der Datenquelle (`dateadd`, `getdate`) wird
  nicht angenommen, damit ein Filter von SQL Server auf eine andere Quelle übertragbar bleibt.
- **Zeitabhängige Prädikate** (`getdate()`): Das Ergebnis ändert sich mit der Zeit. Zwischengespeicherte Abfragepläne
  (`CompiledSqlQueryPlanCache`) und die Policy-Epoche dürfen den **Wert** nie festhalten, nur die Form. Ein Test muss das belegen.
- **Leistung:** Ein Zeitfenster auf `target.ts` nützt nur mit Index auf `ts`; bei den Zeitreihentabellen im PoC (Millionen Zeilen) ist das
  der Unterschied zwischen Millisekunden und einem Scan. Das Prädikat gehört in `EXISTS`, das SQL Server als Semi-Join plant; messen.

### 3.8 TODO: Datumssyntax in Trino und Übersetzung

Die Filter nutzen die Datums- und Zeitsyntax von Trino. Zu tun:

| Nr. | Aufgabe |
| :--- | :--- |
| 1 | **Eingabe festlegen und testen:** erlaubt sind `current_timestamp`, `current_date`, `now()`, `date_add('<einheit>', n, x)`, `date_diff('<einheit>', a, b)`, `date_trunc('<einheit>', x)` und Intervalle (`x - interval '1' day`). Einheiten: `second`, `minute`, `hour`, `day`, `week`, `month`, `year`. Ein Parsertest je Form, auch mit falscher Einheit (Fehler, nicht still ignorieren). |
| 2 | **Übersetzung je Dialekt** in den Generatoren (`SqlDialectGeneratorBase` und Ableitungen), bisher nur `CURRENT_TIMESTAMP`: SQL Server `DATEADD(day, n, x)`, `DATEDIFF(day, a, b)`; PostgreSQL `x + n * interval '1 day'`, `date_trunc`; Oracle Datumsarithmetik oder `NUMTODSINTERVAL`; SQLite `datetime(x, '-1 day')`; Snowflake und DuckDB `DATEADD`/`date_add`. Je Dialekt ein Test mit erwartetem SQL. |
| 3 | **Zeitzone festlegen:** Trino-`current_timestamp` hat eine Zeitzone, SQL-Server-`getdate()` und `CURRENT_TIMESTAMP` nicht (`datetime`, Ortszeit des Servers). Die Spalten `ts` im PoC sind `datetimeoffset`. Vorschlag: `current_timestamp` wird auf SQL Server zu `SYSDATETIMEOFFSET()` übersetzt und alles gilt in UTC. Mit einem Test für Zeilen kurz vor und nach der Grenze belegen. |
| 4 | **Funktionslisten abstimmen:** Die Trino-Namen (`date_add`, `date_diff`, `date_trunc`) müssen in der Liste der erlaubten Funktionen stehen und werden vor der Übersetzung geprüft, nicht danach. Die SQL-Server-Namen aus `SqlFunctionAllowlists` gelten für Abfragen im Dialekt der Quelle, nicht für Filter. |
| 5 | **Meldung bei Unbekanntem:** Eine Funktion, die ein Dialekt nicht kann, lehnt den Filter beim **Speichern** ab (mit Dialekt und Funktion in der Meldung), nicht erst beim Zugriff. |
| 6 | **Test gegen Zwischenspeicher:** Zeitwert wird nie in Abfragepläne oder Policy-Epoche übernommen (siehe oben). |

### 3.9 Objektmengen und mehrere Filter

**Anwendung auf Mengen.** Das Muster einer Bindung darf mit drei Segmenten enden: `lwetem_prod.fms.*` meint alle Objekte des
Schemas. Das Spaltensegment ist dann nicht nötig, weil der Filter selbst festlegt, was er braucht (alle `target.<spalte>` aus 3.7). Regel:

> Ein Objekt bekommt einen Filter, wenn es zum Muster passt **und** alle Spalten hat, die der Filter an `target` verwendet.

Im PoC (Katalogstand 21.09.2026) heißt das für `client_id` und `ts`: `fms` 52 von 52 Tabellen mit beiden Spalten, `tem` 22 von 43 mit beiden
(42 mit `client_id`), `dm` 5 von 8 (6 mit `client_id`), `conf` 0 von 8 mit `ts` (6 mit `client_id`), `md` und `ud` keine mit `client_id`.

**Objekte im Bereich, auf die kein Filter des Profils zutrifft** (etwa `tem.<tabelle>` ohne `client_id` und `ts`): `uncovered` im Profil
entscheidet. `deny` sperrt sie für den Berechtigten (fail closed, Vorschlag), `skip` lässt sie ungefiltert zur Einwilligung durch. Das Profil
nennt es ausdrücklich; es gibt keinen stillen Standard. Maßgeblich ist, ob **irgendein** Filter des Profils trifft, nicht jeder einzeln.

**Mehrere Filter auf einem Objekt: die Reihenfolge der Konfiguration spielt keine Rolle.** Alle zutreffenden Filter wirken zusammen (AND, siehe
3.3), und AND ist in der Reihenfolge unabhängig. Der Spaltenbedarf entscheidet, was zutrifft:

| Objekt hat | Filter A (`client_id`) | Filter B (`client_id`, `ts`) | Wirkung |
| :--- | :--- | :--- | :--- |
| `client_id`, `ts` | trifft | trifft | A **und** B |
| nur `client_id` | trifft | trifft nicht | nur A |
| keins von beiden | trifft nicht | trifft nicht | `uncovered` des Profils (sofern auch kein anderer Filter trifft) |

Bewusst **kein „der erste Treffer gewinnt“ und keine Priorität nach Reihenfolge**: Wer in einer Sicherheitskonfiguration zwei Zeilen
vertauscht, würde sonst unbemerkt einen Filter abschwächen. Soll B den A **ersetzen** (nur das Zeitfenster, nicht beides), steht das
ausdrücklich in B: `supersedes: [filter_a]`. Das gilt unabhängig von der Reihenfolge, wird beim Speichern auf Zyklen und unbekannte Namen
geprüft und erscheint in der Auswertung unten.

**Auswertung für den Betrieb.** Ohne eine Anzeige ist das nicht zu verantworten: Für Benutzer und Objekt muss abfragbar sein, welche
Bindungen zutreffen, welche Spalten fehlen und welches SQL daraus entsteht (`effective-filters`; in Talos ergänzt `resolve_governance.py`
diese Sicht für die Dateien). Der Audit-Eintrag eines Zugriffs nennt die angewendeten Filter.

## 4. Aufteilung Autheris und Talos

| Teil | Wo | Begründung |
| :--- | :--- | :--- |
| Durchsetzung (Bindung, AND-Verknüpfung, Prozeduren, Audit) | **Autheris** | Nur dort greifen alle Kanäle gleich; ein Filter im PoC-Skript lässt sich über eine zusätzliche Einwilligung umgehen. |
| Definition (Filter und Profile als Dateien) | **Talos**, `dbt_sample/governance/access/` | Eigener Ordner unter Verantwortung von **Data Owner und Data Steward**: Sie legen Filter und Profile an und verantworten sie; ein Pull Request dorthin geht an sie (CODEOWNERS). |
| Bereitstellung (Dateien nach Autheris) | **Talos**, ersetzt `grant_row_scoped_user.py` | Liest die Dateien, ruft die Autheris-API auf (mit Audit), nicht mehr die SQLite-Datei. |

### 4.1 Speicherung und Abgleich

Die Dateien in Talos sind die **Quelle der Wahrheit**, Autheris speichert eine **Kopie** und wertet sie aus. Autheris muss sie speichern, weil
die Durchsetzung zur Laufzeit ohne Zugriff auf ein Git-Repository auskommen muss.

| Frage | Vorschlag |
| :--- | :--- |
| Wo in Autheris? | Zwei Tabellen in der Governance-Datenbank (`VIRTUAL_FILTERS`, `FILTER_BINDINGS`), je Zeile Herkunft (`managed_by`: Dateipfad und Commit) und Hash der Definition. |
| Richtung | Nur von Talos nach Autheris. `plan` zeigt Unterschiede, `apply` gleicht ab (anlegen, ändern, **entfernen** was in den Dateien fehlt). |
| Änderung direkt in Autheris | Für Zeilen mit `managed_by` gesperrt (API lehnt ab, außer dem Abgleich). Abweichungen, die doch entstehen (etwa durch direktes Schreiben in die Datenbank), meldet `plan` als Drift. |
| Wer darf schreiben? | Eine eigene Berechtigung für den Abgleich, getrennt von den Einwilligungen; jede Änderung mit Audit-Eintrag (anders als das heutige PoC-Skript). |
| Was liegt in Git, was nicht? | Nur Definitionen (Filter, Muster, Berechtigte), keine Daten. Die Datenbank `governance.db` des PoC ist eingecheckt; das bleibt für Ausnahmen, aber Filter und Bindungen sollen von dort nicht von Hand gepflegt werden. |

Bis Autheris das kann (Schritt 1 in Abschnitt 6), wird **nichts davon in Autheris gespeichert**: Die Bereitstellung erzeugt wie heute
Einwilligungen mit Zeilenfilter je Tabelle. Der Filter als eigener Begriff existiert dann nur in den Dateien in Talos.

Ablage in Talos (angelegt in `dbt_sample/governance/access/`):

```text
governance/access/
├── filters/<filter>.yml      Definition: name, source, sql (Prädikat mit `target`, Trino-Syntax)
└── profiles/<profil>.yml     Berechtigter, Bereich, Filter mit Muster, uncovered
```

```yaml
# filters/nicht_ausgelieferte_krane__client_id.yml
name: nicht_ausgelieferte_krane__client_id
source: lwetem_prod
sql: |
  from conf.client client
  join md.crane crane on crane.serial_number = client.crane_serial_number
  where crane.is_delivered is null
    and target.client_id = client.client_id
# profiles/david.yml
grantee: { user: S-1-5-21-LWE-DAVID }
scope: lwetem_prod.*.*
uncovered: deny
bindings:
  - filter: nicht_ausgelieferte_krane__client_id
  - filter: nicht_ausgelieferte_krane__crane_serial_number
  - filter: nicht_ausgelieferte_krane__crane
```

### 4.2 Neuladen aus GitHub (periodisch und per Webhook)

Statt dass ein Werkzeug die Konfiguration nach Autheris schreibt (4.1), holt Autheris sie selbst: ein Hintergrunddienst fragt in einem
festen Takt, und ein Endpunkt stößt den Abgleich sofort an. Beides führt in denselben **Abgleich** (`plan` und `apply` aus 4.1); ein
Aufruf von Hand und ein Aufruf aus einer CI-Pipeline benutzen ihn ebenfalls.

**Was es in Autheris schon gibt** (gelesen, nicht gebaut oder getestet): Webhook-Endpunkte mit HMAC-SHA256-Prüfung (`X-Hub-Signature-256`
wird akzeptiert), Größenbegrenzung des Bodys und festen Zeitfenstern gegen Wiederholung (`WebhookEndpoints.cs`, `DbtWebhookReceiver`), den
Endpunkt `/api/extensions/dbt/sync` mit `dryRun` und eigener Rolle `DbtAdmin`, und eine Rolle `CatalogSync`. Kein Git-Client und kein
Hintergrunddienst zum Holen von Konfiguration.

| Baustein | Vorschlag |
| :--- | :--- |
| Quelle | Feste Konfiguration, **nicht** aus dem Webhook: `Repository` (`lis-github.liebherr.com/...`), `Ref` (ein geschützter Branch oder ein Tag), `Path` (zum Beispiel `dbt_sample/governance/access`), `Interval` (Standard 5 Minuten), `Enabled`. |
| Holen | Über die GitHub-REST-API, nicht über einen Git-Client: Commit des `Ref` abfragen (mit `ETag`, ein unverändertes Ergebnis ist ein günstiges `304`), nur bei neuem Commit den Pfad als Archiv laden. Zugriff mit einem **nur lesenden** Token oder Deploy-Key aus dem Secret-Provider (`DefaultEnvironmentSecretProvider`). |
| Webhook | `POST /api/webhooks/config-sync`, `push`-Ereignis. Er ist nur ein **Auslöser**: Autheris liest den Inhalt immer selbst vom Commit, nie aus dem Body. Geprüft werden Signatur, `ref` gleich dem konfigurierten, und `X-GitHub-Delivery` (jede Lieferung nur einmal). GitHub schickt keinen Zeitstempel, deshalb passt der Zeitfenster-Test des Katalog-Webhooks hier nicht; die Lieferungs-ID ersetzt ihn. Antwort sofort `202`, der Abgleich läuft danach; mehrere Ereignisse werden zusammengefasst. |
| Abgleich | 1. Alles parsen und prüfen (Schema, Muster, SQL mit AST und Funktionsliste, Zyklen bei `supersedes`) **vor** jeder Änderung. 2. Unterschiede zum gespeicherten Stand berechnen. 3. In **einer Transaktion** anwenden: alles oder nichts. 4. Policy-Epoche der betroffenen Tabellen erhöhen, damit Zwischenspeicher verfallen. 5. Audit-Eintrag mit Commit, Autor, Anzahl der Änderungen und Hash. |
| Fehlerfall | Schlägt Holen oder Prüfen fehl, bleibt der **letzte gute Stand** bestehen (nichts wird entfernt), der Status wird `Fehler`, und eine Warnung geht raus. |
| Sicht | `GET /api/…/config-sync/status`: letzter Commit, Zeit, Ergebnis, Drift. `POST /api/…/config-sync/run?dryRun=true` für Administratoren (eigene Rolle `ConfigSyncAdmin`, nicht die Einwilligungsrechte). |
| Mehrere Instanzen | Nur eine wendet an (Sperre in der Governance-Datenbank), sonst laufen zwei Abgleiche gegeneinander. |

**Sicherheit.** Die Konfiguration entscheidet über Zugriffe, deshalb gilt:

- **Mitentscheidend ist GitHub, nicht Autheris:** Ein vertrauenswürdiger Stand ist nur ein geschützter Branch mit Pflicht zur Prüfung durch die
  Zuständigen (`CODEOWNERS.example`). Autheris liest nur diesen `Ref`. Optional lehnt es Commits ab, die GitHub nicht als signiert
  (`verification.verified`) meldet.
- **Freigabe:** Standard ist, dass der Pull-Request-Review auf GitHub die Freigabe war und Autheris ohne weiteren Schritt anwendet
  (`RequireApproval: None`). Wer in Autheris zusätzlich prüfen will, schaltet `Loosening` oder `All` ein (siehe 4.3, Punkt 4).
- **Der gefährliche Fall ist das Entfernen.** Wird eine Bindung gelöscht, sehen die Berechtigten **mehr**. Ein Schutz gegen Fehler (zum Beispiel
  ein versehentlich geleerter Ordner): Entfernt ein Abgleich mehr als `MaxRemovals` Bindungen oder alle, wird er nicht angewendet und braucht
  `force` durch `ConfigSyncAdmin`. Der Wert ist einstellbar, `0` schaltet die Prüfung ab.
- **Veraltung ist ebenfalls ein Risiko:** Ein Entzug im Repository wirkt erst nach dem nächsten Abgleich. Das Intervall begrenzt dieses Fenster,
  der Webhook verkürzt es; ist der letzte erfolgreiche Abgleich älter als eine Schwelle, schlägt der Gesundheitsstatus an.
- **Kein Ausführen:** Es werden nur Dateien eines erlaubten Pfads gelesen und als Daten geparst; keine Symlinks, Größenlimit für das Archiv,
  Repository und Pfad nur aus der Konfiguration (kein SSRF über den Webhook-Body).
- **Die vorhandenen „Bypass“-Schalter** (`IsWebhookSignatureBypassed`, `IsWebhookTimestampToleranceIgnored`) dürfen für diesen Endpunkt nicht gelten.

**Erreichbarkeit.** Autheris muss `lis-github.liebherr.com` erreichen (Firmenzertifikat und Proxy: der PoC-Container hat keine Firmen-CAs, im
Cluster ist das zu prüfen). Der Webhook braucht die Gegenrichtung: GitHub muss Autheris erreichen, im lokalen PoC mit Podman geht das nicht.
Das Abfragen im Takt ist deshalb die **Grundlage**, der Webhook nur eine Beschleunigung.

**Alternative: Schieben aus der CI.** Eine Pipeline ruft nach dem Zusammenführen `apply` auf. Vorteil: Autheris braucht keinen Zugang zu GitHub
und kein Token, der Zeitpunkt ist exakt. Nachteil: Die Pipeline braucht eine Berechtigung in Autheris und Zugang dorthin, und ein Ausfall der
Pipeline bleibt unbemerkt (kein Takt, der nachholt). Beide Wege nutzen denselben Abgleich; das Schieben ist ohne weiteren Aufwand mit
abgedeckt.

**Ablauf von der Änderung bis zur Wirkung** (ohne dbt):

1. Owner oder Steward ändert eine Datei unter `governance/access/` und öffnet einen Pull Request.
2. **Vor dem Zusammenführen** läuft in der CI `resolve_governance.py --check` als Pflichtprüfung: Filter vollständig und nur lesend,
   Profile verweisen auf vorhandene Filter, `uncovered` gesetzt, Muster treffen Objekte. Ein roter Lauf verhindert das Zusammenführen.
3. CODEOWNERS verlangt die Freigabe von Owner und Steward; der Branch ist geschützt. Das ist die Freigabe (siehe 4.3, Punkt 4).
4. Nach dem Zusammenführen kommt der **Auslöser** (siehe unten) bei Autheris an.
5. Autheris holt den Commit selbst, prüft **erneut** mit seinen eigenen Regeln (AST, Funktionslisten), berechnet `plan`, wendet in einer Transaktion
   an, erhöht die Policy-Epochen und schreibt den Audit-Eintrag mit Commit und Autor.
6. Autheris meldet das Ergebnis zurück (als Commit-Status an GitHub und in `config-sync/status`). Bei einem Fehler bleibt der letzte gute Stand.

**Mögliche Auslöser**, alle führen in denselben Abgleich (`plan`/`apply`), und mehrere lassen sich kombinieren:

| Auslöser | Ablauf | Vorteil | Nachteil |
| :--- | :--- | :--- | :--- |
| Takt in Autheris (Grundlage) | Hintergrunddienst fragt alle N Minuten den Commit des `Ref` ab (`ETag`) | Braucht nur Zugang von Autheris zu GitHub; holt Verpasstes nach | Verzögerung bis zum nächsten Takt |
| GitHub-Webhook (`push`) | `POST /api/webhooks/config-sync`, löst den Abgleich sofort aus | Wirkt in Sekunden | GitHub muss Autheris erreichen; kann ausfallen, deshalb nie allein |
| CI-Schritt nach dem Zusammenführen | Eine Pipeline ruft `config-sync/run` mit einem Dienstkonto auf (oder ein Werkzeug `apply` aus `tools/`) | Exakter Zeitpunkt, Ergebnis im Pipeline-Lauf sichtbar, Autheris braucht keinen GitHub-Zugang | Pipeline braucht Zugang und Berechtigung in Autheris; fällt sie aus, wird nichts nachgeholt |
| Zeitgesteuerter Job im Cluster | Kubernetes-CronJob führt denselben `apply` aus | Keine Änderung in Autheris, wenn der Abgleich als Werkzeug läuft | Zusätzlicher Baustein mit eigenem Zugriff auf die Datenbank |
| Von Hand | Administrator ruft `config-sync/run` auf (mit `dryRun`) | Für Tests und Notfälle | Nicht für den Regelbetrieb |

**Empfehlung:** Takt als Grundlage plus Webhook als Beschleunigung, wie oben. Läuft Autheris dort, wo GitHub es nicht erreicht (lokaler PoC,
strenge Netze), ist der CI-Schritt der Ersatz für den Webhook. Der Takt bleibt in jedem Fall als Sicherheitsnetz, damit ein verpasster Auslöser
nicht zu einem dauerhaft veralteten Stand führt.

### 4.3 Die dbt-Integration in Autheris (Ist-Stand, gelesen, nicht gebaut oder getestet)

Autheris nimmt dbt-Metadaten über `POST /api/extensions/dbt/sync[?dryRun=true]` entgegen (`DbtEndpoints.cs`,
`DbtMetadataIngestionService`, `DbtArtifactStreamingParser`). Gedacht ist es als **Vorschlagsverfahren für Metadaten**, nicht als
Konfiguration, die Zugriffe setzt:

| Schritt | Was passiert |
| :--- | :--- |
| Aufruf | Ein dbt-`manifest.json` (bis 100 MB) wird **hineingeschickt** (Push); Autheris holt nichts. Erlaubt nur für globale Governance-Administratoren und die Rolle `DbtAdmin`. `dryRun` zählt nur. Daneben: `validate-contract`, `run-results`, `exposures`, `health` und ein dbt-Cloud-Webhook mit HMAC-Prüfung. |
| Lesen | Nur Knoten `model.*` und `seed.*` aus `nodes`: Name, Datenbank, Schema, Beschreibung, `tags`, `meta`, `columns`, `depends_on`, Vertrag. **Quellen (`sources`) werden nicht gelesen.** |
| Vorschläge | Spalten mit `meta.pii: true` oder `email`/`ssn`/`pseudonym` im Namen oder Tag werden zu **Vorschlägen** (`PendingReview`) für eine Maskierung (`MASK_EMAIL`, `REDACT`, `HMAC_SHA256`). Ebenso `meta.rls_filter` (Spalte oder Modell) und `meta.casbin_roles`. |
| Freigabe | Ein Mensch gibt jeden Vorschlag frei oder lehnt ihn ab (`proposals/{id}/approve`). Freigegeben wird **nur eine Maskierungsregel**; ein Vorschlag mit `RLS_FILTER:` oder `CASBIN_ROLES:` wird bei der Freigabe **abgelehnt** („als Einwilligungs-Zeilenfilter oder Casbin-Richtlinie umsetzen“). Eine Freigabe darf eine stärkere vorhandene Maskierung nicht abschwächen (`CatalogGovernanceRatchet`); danach steigt die Policy-Epoche der Tabelle. |
| Sofort wirksam | Beschreibung und `long_description` werden in die Tabellen-Metadaten übernommen (wenn die Tabelle im Katalog existiert), die Herkunft (Lineage) wird aktualisiert, `meta.owner` und `meta.owner_email` landen im Lineage-Knoten; auf Wunsch (`SqlEndpoints.AutoSyncFromDbt`) entstehen `.sql`-Dateien für SQL-Endpunkte. |

**Folgerungen für dieses Vorhaben:**

1. **`dbt_sample` passt heute nicht.** Alle Tabellen sind dort `sources`; von `nodes` kommen nur die 115 Seeds, mit dem Schema des
   Ziels (nicht `md`, `tem` …). Für eine Übernahme müsste der Parser `source.*` lesen und `database` und `schema` der Quelle verwenden. Ob die
   Bezeichner (`LWETEM_PROD`) zu den Katalog-Bezeichnern (`lwetem_prod`) passen, ist ungeprüft.
2. **Governance fehlt im Manifest.** Eigentümer, Klassifizierung und PII stehen in `dbt_sample/governance/` **außerhalb** von dbt. Autheris sieht
   davon nichts, bis `resolve_governance.py` sie als `meta` in die Quellen schreibt (oder Autheris `target/governance/` selbst liest).
3. **Virtuelle Filter gehören nicht dorthin.** dbt kennt kein Filterkonzept, und Autheris lehnt `RLS_FILTER`-Vorschläge aus dbt ausdrücklich ab.
   Filter brauchen den eigenen Weg aus 4.1 und 4.2.
4. **Grundsatzfrage, die der Code schon beantwortet:** Autheris wendet Metadaten aus dbt **nicht von selbst** auf Zugriffsregeln an, es verlangt
   die Freigabe durch einen Menschen in Autheris. Der Entwurf in 4.2 wendet Filter nach dem Zusammenführen auf GitHub **automatisch** an.
   Das ist ein anderer Maßstab. **Entschieden (08.10.2026): Die manuelle Freigabe in Autheris ist per Konfiguration schaltbar. Standard ist
   „aus“: Es gilt als gegeben, dass die Freigabe bereits auf GitHub per Pull-Request-Review erfolgt ist** (geschützter Branch, `CODEOWNERS`), und
   Autheris wendet einen geprüften Stand selbst an. Ist die Freigabe **eingeschaltet** (`ConfigSync:RequireApproval`), landet jede Änderung als
   **Vorschlag** in Autheris und wird erst nach Freigabe wirksam, nach dem Muster der dbt-Vorschläge (`PendingReview`, `approve`, `reject`). Feiner
   einstellbar: `RequireApproval: Loosening` verlangt die Freigabe nur für Lockerungen (Bindung entfernt, Filter abgeschwächt, `uncovered` von `deny`
   auf `skip`), im Sinne der vorhandenen Ratsche (`CatalogGovernanceRatchet`). Werte: `None` (Standard), `Loosening`, `All`. Der Vorschlagsspeicher
   muss dafür **dauerhaft** sein (heute nur im Arbeitsspeicher, siehe Punkt 5).
5. **Wiederverwendbar aus dbt:** Rolle und Muster der Endpunkte (`DbtAdmin`, `dryRun`), HMAC-Prüfung des Webhooks, Vorschlags- und Freigabespeicher
   (`IDbtProposalRepository`, bisher nur im Speicher: `InMemoryDbtProposalRepository`, nach einem Neustart weg), Erhöhung der Policy-Epoche.

**Empfehlung:** dbt nur für **beschreibende** Metadaten (Beschreibungen, Herkunft, PII-Vorschläge) nutzen, wie vorgesehen, und `sources` dazu
im Parser ergänzen. Filter und Bindungen über den eigenen Weg 4.1/4.2; die Freigabe in Autheris ist per Konfiguration schaltbar, Standard aus (Review auf GitHub gilt als Freigabe).

## 5. Offene Fragen

Die Vorschläge der ersten beiden Zeilen sind von Manuel Müller angenommen (08.10.2026): Objekte ohne passende Spalte sind für den
David-Fall gesperrt (`deny`), Bindungen gewähren nichts. Die übrigen Zeilen sind offen.

| Frage | Folge |
| :--- | :--- |
| Was gilt für Objekte **ohne** passende Spalte (zum Beispiel Stammdaten ohne `client_id`)? Heute bekommt David nur Tabellen mit Bezug zu Kranen; alle anderen sind gesperrt. | `uncovered: deny` im Profil bildet das ab (Standard für den David-Fall); `skip` lässt die Einwilligungen allein entscheiden. |
| Dürfen Bindungen auch **gewähren** (Zugriff ohne Einwilligung)? | Vorschlag nein: Einwilligungen bleiben die einzige Quelle für Zugriff. |
| Wie lange darf die Wertemenge des Filters zwischengespeichert werden? | Betrifft Prozedur-Ergebnisse und Sicht auf neu ausgelieferte Krane; Vorschlag: Policy-Epoche wie bei Einwilligungen. |
| Leistung bei einer Menge von ~46 700 Kranen und Zeitreihentabellen mit Millionen Zeilen | `InCorrelated` oder `Exists` je Tabelle messen, nicht raten. Im PoC ist `InCorrelated` bereits Standard (wegen derselben Lage). |
| Wer darf Filter und Bindungen anlegen? | Eigene Berechtigung, nicht die der Einwilligungen; jede Änderung mit Audit-Eintrag. |

## 6. Reihenfolge

1. **Talos, ohne Änderung an Autheris:** Definition als Datei, Bereitstellung erzeugt wie heute je Tabelle eine Einwilligung mit
   Zeilenfilter, nur jetzt aus Filter und Muster statt aus fest eingebauten Regeln. Das deckt den David-Fall ab und neue Tabellen
   laufen automatisch mit. Es bleibt die Umgehungslücke aus Abschnitt 2.
2. **Autheris, Kern:** `VirtualFilter` und `FilterBinding` (Speicher, API, Audit), `MandatoryRowPredicate` in
   `ConsentResolutionService`, Strukturierte Definition. Tests: Muster, AND-Verknüpfung, Umgehung durch zusätzliche Einwilligung
   unmöglich, `uncovered`, Gleichheit der Ergebnisse in WebSQL, GraphQL, OData, Trino.
3. **Autheris, Erweiterung:** SQL-Definition mit AST-Prüfung, Prozedur-Ergebnisse, MCP-Stichproben, Zwischenspeicher.
4. **Talos:** Bereitstellung auf die API umstellen, `grant_row_scoped_user.py` entfernen.

Jeder Schritt beginnt mit einem fehlschlagenden Test.
