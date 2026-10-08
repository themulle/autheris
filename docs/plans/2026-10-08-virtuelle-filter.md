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

**Bindung** (`FilterBinding`): wendet einen Filter auf eine Menge von Objekten für Berechtigte an.

| Feld | Bedeutung |
| :--- | :--- |
| `filter` | Name des virtuellen Filters |
| `target` | Muster `quelle.schema.objekt.spalte`, siehe 3.2 |
| `grantee` | Benutzer (SID) oder Rolle, wie bei Einwilligungen |
| `object_kinds` | `table`, `view`, `procedure_result` (Standard: alle) |
| `time_column` | optional: Spalte des Objekts, die gegen das Zeitfenster des Filters geprüft wird (siehe 3.6) |
| `on_unmatched` | `skip` oder `deny`: Was gilt für Objekte, die das Muster nicht trifft (siehe 5); Vorschlag: `deny` |

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

**Objekte im Muster, aber ohne die Spalten** (etwa `tem.<tabelle>` mit `client_id`, aber ohne `ts`): `on_unmatched` entscheidet.
`deny` sperrt sie für die Berechtigten (fail closed, Vorschlag), `skip` lässt sie ungefiltert zur Einwilligung durch. Jede Bindung nennt
das ausdrücklich; es gibt keinen stillen Standard.

**Mehrere Filter auf einem Objekt: die Reihenfolge der Konfiguration spielt keine Rolle.** Alle zutreffenden Filter wirken zusammen (AND, siehe
3.3), und AND ist in der Reihenfolge unabhängig. Der Spaltenbedarf entscheidet, was zutrifft:

| Objekt hat | Filter A (`client_id`) | Filter B (`client_id`, `ts`) | Wirkung |
| :--- | :--- | :--- | :--- |
| `client_id`, `ts` | trifft | trifft | A **und** B |
| nur `client_id` | trifft | trifft nicht | nur A |
| keins von beiden | trifft nicht | trifft nicht | `on_unmatched` je Bindung |

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
| Definition (Filter und Bindungen als Dateien) | **Talos**, `dbt_sample/governance/access/` | Rolle Datenschutz / Data Owner, mit Vererbung und CODEOWNERS wie die übrige Governance. |
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

Beispiel der Definition in Talos:

```yaml
filters:
  nicht_ausgelieferte_krane:
    key_columns: [client_id]
    from: conf.client
    joins:
      - table: md.crane
        on: { crane.serial_number: client.crane_serial_number }
    where:
      - crane.is_delivered: null
bindings:
  - filter: nicht_ausgelieferte_krane
    target: lwetem_prod.*.*.client_id
    grantee: { user: S-1-5-21-LWE-DAVID }
```

## 5. Offene Fragen

Die Vorschläge der ersten beiden Zeilen sind von Manuel Müller angenommen (08.10.2026): Objekte ohne passende Spalte sind für den
David-Fall gesperrt (`deny`), Bindungen gewähren nichts. Die übrigen Zeilen sind offen.

| Frage | Folge |
| :--- | :--- |
| Was gilt für Objekte **ohne** passende Spalte (zum Beispiel Stammdaten ohne `client_id`)? Heute bekommt David nur Tabellen mit Bezug zu Kranen; alle anderen sind gesperrt. | `on_unmatched: deny` bildet das ab (Standard für den David-Fall); `skip` lässt die Einwilligungen allein entscheiden. |
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
   unmöglich, `on_unmatched`, Gleichheit der Ergebnisse in WebSQL, GraphQL, OData, Trino.
3. **Autheris, Erweiterung:** SQL-Definition mit AST-Prüfung, Prozedur-Ergebnisse, MCP-Stichproben, Zwischenspeicher.
4. **Talos:** Bereitstellung auf die API umstellen, `grant_row_scoped_user.py` entfernen.

Jeder Schritt beginnt mit einem fehlschlagenden Test.
