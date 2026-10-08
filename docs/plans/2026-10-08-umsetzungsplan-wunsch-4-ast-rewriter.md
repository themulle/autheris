# Umsetzungsplan Wunsch 4: AST-Rewriter (`SqlRewriterEngine=AstCompiler`)

**Stand:** `feat/ast-target-dialect-generator` e39cd70, geprüft am 08.10.2026 gegen Code und mit einer Wegwerf-Probe beider Engines (nichts im Repo geändert).
**Bezug:** [Befund Wunsch 4](2026-10-08-befund-autheris.md), Abschnitt 1 („WebSQL mit `SqlRewriterEngine=AstCompiler`“) und 5.
**Vorgehen:** TDD (erst roter Test, dann Fix), ein Thema je Commit, Standard-Engine `LegacyTokenStream` bleibt unverändert.

## 1. Ziel und Abnahme

Der AST-Rewriter soll für WebSQL und deklarierte Abfragen dieselben Abfragen korrekt ausführen wie der Legacy-Rewriter, in allen Zieldialekten. Ein Konstrukt, das er nicht kann, wird **laut abgelehnt (400)**, nie still weggelassen und nie als 500 an die Datenbank durchgereicht.

Abnahme:
- Alle Punkte aus Abschnitt 2 grün, mit Tests.
- Der Differenztest (Phase 0) läuft über den Korpus ohne „stille Abweichung“; jede verbleibende AST-Ablehnung ist bewusst und als 400 dokumentiert.
- Die Benchmark-Tests schreiben keine falsche Ausgabe mehr fest.
- Erst danach: Entscheidung, ob `AstCompiler` Standard wird (nicht Teil dieses Plans).

## 2. Befund gegen den aktuellen Code

| # | Punkt aus dem Befund | Status | Ursache |
|---|---|---|---|
| 4.1 | `COUNT(*)` wird leerer Aufruf | gilt | `SqlAstBuilder.cs:797`: `context.argument()` ist nie `null`, der `ASTERISK`-Zweig (`:812`) wird nie erreicht. `label.*` geht genauso verloren |
| 4.2 | Funktionsnamen gequotet | gilt | `SqlDialectGeneratorBase.cs:496`: Name läuft durch `FormatQualifiedName` → `"coalesce"(…)` (Postgres: „function does not exist“, COALESCE/NULLIF/GREATEST/LEAST sind dort Schlüsselwörter), `[SUM](…)` (SQL Server) |
| 4.3 | `@param` positional | gilt | `NormalizeClientParameters` macht `@name` → `__param_name`; der Builder erzeugt `ParameterReference(IsSynthetic: true)`; `FormatParameter` (`SqlDialectGeneratorBase.cs:~590`) gibt dafür `$1`/`@p0`/`?1`/`:p1` aus; `RestoreClientParameters` findet kein `__param_` mehr, gebunden wird aber `@name` → 500 |
| 4.4 | `EXTRACT` nur ANSI | gilt | `SqlDialectGeneratorBase.cs:~555`, kein Generator überschreibt |
| 4.5 | `ROLLUP`/`CUBE`/`GROUPING SETS` fallen weg | gilt, still | `SqlAstBuilder.cs:358` kennt nur `SingleGroupingSetContext`. `GROUP BY ROLLUP(dept)` ergibt **gar kein** GROUP BY; `GROUP BY DISTINCT` verliert den Quantor; `GROUPING(dept)` → 500 |
| 4.6a | SQL Server mit Zeilenfilter → 400 | gilt | Einwilligungsfilter werden im Zieldialekt gerendert (`RowFilterSqlBuilder.cs:382`, `[dept] = 'Sales'`); `AstSecurityVisitor.ParseFilterExpression` (`:36-43`) parst sie mit der Trino-Grammatik neu und lehnt eckige Klammern ab. Parametrisierte Filter (`@__gql_*`) scheitern in allen Dialekten. Legacy fügt den Filter als Text ein |
| 4.6b | `CAST … GROUP BY` → 500 | Folge von 4.1/4.2 | CAST/GROUP BY selbst wird richtig erzeugt; der Syntaxfehler kommt von `[COUNT]()`/`[SUM]` im selben Statement |
| 4.6c | Deklarierte Abfragen | Folge von 4.3 | `SqlEndpointExecutionService:75-87` läuft durch denselben `GovernedSqlExecutionService`; jede parametrisierte Abfrage trifft 4.3 |

**Zusätzlich gefunden (nicht im Befund):**

| # | Lücke | Wirkung |
|---|---|---|
| Z1 | Fenster-ORDER BY: `row_number() OVER (ORDER BY id DESC)` → `OVER ()` (`SqlAstBuilder.cs:827` ruft `Visit(orderBy)`, es gibt kein `VisitOrderBy`); ohne ASC/DESC `InvalidCastException`; Rahmen (`ROWS BETWEEN`) und `IGNORE NULLS` → 500 | **still falsch** |
| Z2 | `FILTER (WHERE …)` an Aggregaten fällt weg: `COUNT(*) FILTER (…)` zählt alle Zeilen | **still falsch** |
| Z3 | `ORDER BY` in Aggregaten (`array_agg(x ORDER BY y)`) fällt weg | **still falsch** |
| Z4 | Typisierte Literale (`DATE '2024-01-01'`, `TIMESTAMP …`, `INTERVAL '1' DAY`) werden zu String `'DATE''2024-01-01'''` (`VisitLiterals`, Default-Zweig `:781`, `GetText()`) | **still falsch** |
| Z5 | Fehlende Builder-Visitors → `ArgumentNullException` (500): `substring … FROM`, `trim`, `position`, `current_date`/`current_timestamp`/`localtime`, `GROUPING()`, `listagg`, `normalize`, `overlay`, JSON-Funktionen, Lambdas. 13 von 86 Korpus-Statements, die Legacy akzeptiert | 500 |
| Z6 | SQL Server: CASE in der Projektion wird doppelt eingepackt (`CASE WHEN CASE WHEN …`, `InProjectionContext` wird im CASE-Zweig `:472` nicht zurückgesetzt); nach einer skalaren Unterabfrage wird ein späteres boolesches Projektionsfeld nicht mehr eingepackt | 500 |
| Z7 | SQL Server: `IS DISTINCT FROM` (erst ab 2022) und `JOIN … USING` (nie T-SQL) werden unverändert ausgegeben | 500 |
| Z8 | Trino-Typnamen werden in keinem Dialekt abgebildet: `double`, `boolean`, `timestamp` (in T-SQL = rowversion!) | 500 bzw. falscher Typ |
| Z9 | `ShadowDualRun` (`FastSqlEngine.cs:761-772`) verwirft das AST-Ergebnis und schluckt jede Ausnahme: kein Vergleich, kein Log, nur Latenz. `LegacyVsAstDifferentialTests` prüft nur „beide nicht leer“ | kein Sicherheitsnetz |

Korrekt in der Probe: DISTINCT, UNION/INTERSECT/EXCEPT [ALL], LIMIT/OFFSET/FETCH je Dialekt, IN-Unterabfrage, ALL/ANY, EXISTS, LIKE … ESCAPE, NULLS FIRST/LAST (SQL Server emuliert), BETWEEN, CASE (Postgres).

## 3. Phasen

Reihenfolge so gewählt, dass zuerst die stillen Fehler rot werden und danach jeder Fix einen Test hat. Aufwand: S ≤ ½ Tag, M ≈ 1–2 Tage, L > 2 Tage.

### Phase 0: Sicherheitsnetz (M)

1. **Fail-loud im Builder.** `SqlAstBuilder` überschreibt `VisitChildren`/`DefaultResult` und wirft `AstBuildException("Unsupported construct: <Regelname>")`; `GovernedSqlExecutionService` bildet sie auf 400 mit Meldung ab. Damit wird jedes nicht behandelte Konstrukt (Z5, 4.5, Z1, Z2, Z3) zu einem klaren 400 statt still falsch oder 500.
   - Test zuerst: `Ast/AstBuilderFailLoudTests` mit `GROUP BY ROLLUP(a)`, `COUNT(*) FILTER (WHERE a>1)`, `trim(a)`, `array_agg(x ORDER BY y)` → `AstBuildException`.
   - Achtung: Visitors, die heute absichtlich auf `VisitChildren` bauen, gezielt durchreichen; die bestehende Testsuite zeigt sie.
2. **Differenztest per Ausführung.** `TrinoSqlEngine.Tests/Differential/ExecutionDifferentialTests`: beide Engines über `Fixtures/trino_statements.json` plus einen kuratierten WebSQL-Korpus (alle Fälle aus Abschnitt 2), ausgeführt gegen SQLite in-memory mit Seed-Tabellen (Microsoft.Data.Sqlite ist referenziert); Vergleich der Zeilen-Multimengen. Ausgabe je Statement: gleich / AST lehnt ab (400) / **Abweichung**. Textvergleich taugt nicht (Quoting, Aliase).
   - Postgres und SQL Server als Integrationstest über Testcontainers (Infrastruktur vorhanden), gleicher Korpus.
   - Abweichungen, die erst spätere Phasen beheben, als `Skip` mit Verweis auf die Phase markieren, damit die Suite grün bleibt und die Liste sichtbar schrumpft.
3. **ShadowDualRun nützlich machen (S, optional).** Gesampelt loggen: Ausnahmetyp der AST-Engine, Hash beider Ausgaben, „unterschiedlich ja/nein“. Nie SQL-Text mit Literalen loggen.

### Phase 1: Funktionsaufrufe (4.1 + 4.2, löst 4.6b) (S)

- `VisitFunctionCall`: `ASTERISK()` zuerst prüfen (bzw. `argument().Length > 0`). Den Stern als eigenen Knoten `StarArgument` darstellen, **nicht** als `ColumnReference("*")`, damit Security-Visitor und Masken-Guard ihn nicht als Spalte behandeln. `label.*` ebenso.
- Neuer virtueller `FormatFunctionName` im Generator: einteiliger Name, der `^[A-Za-z_][A-Za-z0-9_]{0,127}$` erfüllt (hat `SqlFunctionPolicy` bereits passiert) → unquoted, Großbuchstaben; mehrteilige Namen (UDF) → Teile gequotet; alles andere → `SecurityException`.
- Tests zuerst: `Ast/FunctionCallEmissionTests`
  - `SELECT COUNT(*) FROM t` → Postgres `SELECT COUNT(*) FROM (…) AS "t"`, SQL Server `SELECT COUNT(*) …`
  - `HAVING COUNT(*) > 5` → `HAVING COUNT(*) > 5`
  - `SELECT COALESCE(a,0), NULLIF(b,'x') FROM t` → Postgres `COALESCE("a", 0), NULLIF("b", 'x')`; SQL Server `COALESCE([a], 0), NULLIF([b], N'x')`
  - `SELECT CAST(created_at AS date) d, COUNT(*) n FROM t GROUP BY CAST(created_at AS date)` (SQL Server, maxRows) → `… COUNT(*) AS [n] … GROUP BY CAST([created_at] AS date) …` (deckt 4.6b ab)
- Die festgeschriebenen Falschausgaben in `ComplexTrinoBenchmarkQueriesTests.cs` korrigieren (`:51`, `:63`, `:96`, `:108`, `:228`, `:240`).

### Phase 2: Client-Parameter (4.3, löst 4.6c) (S)

- `FormatParameter`: bei `IsSynthetic` und gültigem Namen `__param_<name>` unquoted ausgeben, statt eines positionalen Markers. `RestoreClientParameters` wirkt dann für beide Engines gleich; die Abbildung auf den Dialekt bleibt an einer Stelle. Das Parameter-Budget zählt diese Parameter weiter mit.
- Tests zuerst:
  - `Ast/ClientParameterEmissionTests`: `WHERE dept = __param_d AND amount > __param_m` → in jedem Dialekt `__param_d`/`__param_m`, kein `$1`/`@p0`.
  - `Autheris.Tests.Unit`: Rundlauf durch `RestoreClientParameters` mit `RewriterEngine="AstCompiler"` → `@d`.
  - `SqlEndpoints/SqlEndpointAstCompilerTests`: deklarierte Abfrage mit `@param` über eine Fake-Verbindung, die Text und Parameter mitschreibt → Text enthält `@name`, kein `$1`/`@p0`.

### Phase 3: Zeilenfilter im Zieldialekt (4.6a) (M)

- Vom Gateway gerenderte Filter sind bereits durch `ValidatePredicateSql` geprüft. Sie werden als Text eingesetzt, nach dem Vorbild von SQL-4 für Masken:
  - neue Option `RlsOptions.PolicyFiltersAreTargetDialectSql`, gesetzt von `GovernedSqlExecutionService`;
  - ist sie gesetzt, packt `CreateSecuredSubqueryTableSource` den Filter in eine geklammerte `TrustedSqlExpression`; der Generator klammert sie auch im WHERE (Kommentar `:552` sagt heute „nur Projektion“);
  - DML (`AstSecurityVisitor` `:260`, `:337`) gleich behandeln;
  - ohne Option bleibt das Parsen (Bibliotheksnutzer).
- Tests zuerst (`AstSecurityVisitorRlsTests`): SQL Server mit Filter `tenant_id = 't1' AND ([dept] = 'Sales')` → `WHERE (tenant_id = 't1' AND ([dept] = 'Sales'))`, keine Ausnahme; `@__gql_rf0` bleibt unverändert erhalten; ohne Option weiter Ablehnung eckiger Klammern.
- Sicherheitsprüfung: Der Text kommt ausschließlich aus `RowFilterSqlBuilder`/`ConsentResolutionService`, nie vom Client; das im Test festhalten (Client-SQL mit `[x]` bleibt 400).

### Phase 4: Stille Fehler in Fenstern, Aggregaten, Literalen (Z1–Z4) (M)

- Fenster: Sortierschleife aus `:205-220` für `OVER (ORDER BY …)` wiederverwenden; `WindowSpecification` um Rahmen (`ROWS`/`RANGE BETWEEN …`) erweitern; `IGNORE NULLS` abbilden oder ablehnen.
- `FILTER (WHERE …)`: Postgres/DuckDB nativ; SQL Server, Oracle, SQLite → `AGG(CASE WHEN … THEN <arg> END)` (bei `COUNT(*)` `THEN 1`).
- `ORDER BY` in Aggregaten: Postgres nativ; SQL Server `STRING_AGG … WITHIN GROUP (ORDER BY …)` wo möglich, sonst 400.
- Typisierte Literale: eigener Knoten `TypedLiteral(type, text)`; Ausgabe je Dialekt (`DATE '…'` ANSI, SQL Server `CAST('…' AS date)`, SQLite als ISO-Text, `INTERVAL` je Dialekt oder 400).
- Tests: je Konstrukt ein Emissionstest je Dialekt plus Einträge im Differenzkorpus (Phase 0), deren `Skip` hier entfällt.

### Phase 5: GROUP BY-Varianten (4.5) (M)

- `GroupByClause` (`Clauses.cs:29`) auf `IReadOnlyList<GroupingElement>` umstellen: `Simple(expr)`, `Rollup(sets)`, `Cube(sets)`, `GroupingSets(sets)` plus `Distinct`-Flag; neuer Ausdruck `GroupingOperation` für `GROUPING(…)`.
- `SqlAstRewriter`, `AstSecurityVisitor` (Masken-/Deny-Prüfung auch in den neuen Elementen!), `AstSimplificationVisitor` und Generator nachziehen.
- Ausgabe: Postgres, Oracle, Snowflake, DuckDB, SQL Server ANSI; `GROUP BY DISTINCT` auf SQL Server → 400; SQLite → 400 (`NotSupportedException`). Nie still weglassen.
- Tests: `GROUP BY ROLLUP (dept)` → `GROUP BY ROLLUP ("dept")`; `GROUP BY GROUPING SETS ((dept), ())` gleich; `CUBE(a,b)` auf SQLite → Ausnahme; Builder-Guard für unbekannte Elemente.

### Phase 6: EXTRACT, Sonderformen, Typnamen (4.4, Z5, Z8) (M–L)

- Virtueller `FormatExtract` je Dialekt:
  - Postgres/DuckDB: ANSI; `DAY_OF_WEEK`→`ISODOW`, `DAY_OF_YEAR`→`DOY`, `YEAR_OF_WEEK`→`ISOYEAR` (Trino zählt ISO, 1–7).
  - SQL Server: `DATEPART(year|quarter|month|iso_week|day|dayofyear|hour|minute|second|millisecond|microsecond, x)`; Wochentag `((DATEPART(weekday,x)+@@DATEFIRST+5)%7)+1`; `EPOCH`, Zeitzonenfelder → 400.
  - SQLite: `CAST(strftime('%Y'|'%m'|'%d'|'%H'|'%M'|'%S'|'%j', x) AS INTEGER)`; Wochentag `((CAST(strftime('%w',x) AS INTEGER)+6)%7)+1`; Quartal `(Monat+2)/3`.
  - Oracle: nativ für YEAR…SECOND; Quartal `TO_NUMBER(TO_CHAR(x,'Q'))`; Wochentag ablehnen (NLS-abhängig).
- Sonderformen aus Z5 (`substring … FROM`, `trim`, `position`, `current_date`/`current_timestamp`/`localtime`, `GROUPING()`, `listagg`) mit eigenen Knoten oder als 400 (über den Guard aus Phase 0); JSON-Funktionen und Lambdas bewusst 400.
- `FormatTypeName` je Dialekt: `double` → `float`/`double precision`/`REAL`, `boolean` → `bit` (SQL Server), `timestamp` → `datetime2` (SQL Server!), `varchar` ohne Länge → `nvarchar(max)` usw.
- Tests: `EXTRACT(YEAR FROM d)` → SQL Server `DATEPART(year, [d])`, SQLite `CAST(strftime('%Y', "d") AS INTEGER)`, Oracle `EXTRACT(YEAR FROM "D")`; `EXTRACT(EPOCH FROM d)` auf SQL Server → 400; `CAST(x AS timestamp)` auf SQL Server → `datetime2`.

### Phase 7: SQL-Server-Projektionskontext (Z6, Z7) (S)

- CASE-Zweig (`:472`) setzt `InProjectionContext` für Bedingungen zurück; nach verschachteltem `GenerateQuerySpecification` den Kontext wiederherstellen.
- `IS DISTINCT FROM` auf SQL Server < 2022 → `NOT EXISTS (SELECT a INTERSECT SELECT b)`, sonst nativ (Version aus den Optionen der Datenquelle, Standard: Emulation); `JOIN … USING` → `ON l.c = r.c`.
- Tests: `SELECT CASE WHEN a > 1 THEN 'x' END, b > 2 AS f FROM t` auf SQL Server → genau ein CASE je Feld; `a IS DISTINCT FROM b`; `JOIN u USING (id)`.

## 4. Abhängigkeiten

- Phase 0 vor allem anderen: Sie macht die stillen Fehler rot und fängt neue ab.
- 4.6b ist erst mit Phase 1 erledigt, 4.6c erst mit Phase 2.
- Phasen 4 und 5 ändern Knoten-Records; erst nach Phase 0, damit Tests vorher existieren. Der `AstSecurityVisitor` muss jeden neuen Knoten prüfen (Masken, Deny, Filter-Guard SEC-FILTER-01), sonst entsteht eine Umgehung.
- Keine Phase braucht eine Grammatikänderung.
- Der Filter-Guard aus Wunsch 8 (`SqlQueryAnalyzer`) arbeitet auf dem Parse-Baum, nicht auf dem AST; er gilt für beide Engines und muss nicht angepasst werden.

## 5. Aufwand und Reihenfolge

| Phase | Inhalt | Aufwand | Löst |
|---|---|---|---|
| 0 | Fail-loud-Guard, Differenztest (SQLite, dann Postgres/SQL Server), Shadow-Logging | M | macht Z1–Z5 sichtbar |
| 1 | `COUNT(*)`, Funktionsnamen | S | 4.1, 4.2, 4.6b |
| 2 | Client-Parameter | S | 4.3, 4.6c |
| 3 | Zeilenfilter als Zieldialekt-SQL | M | 4.6a |
| 4 | Fenster, FILTER, Aggregat-ORDER BY, typisierte Literale | M | Z1–Z4 |
| 5 | ROLLUP/CUBE/GROUPING SETS/GROUP BY DISTINCT | M | 4.5 |
| 6 | EXTRACT, Sonderformen, Typnamen | M–L | 4.4, Z5, Z8 |
| 7 | SQL-Server-Projektionskontext, IS DISTINCT FROM, USING | S | Z6, Z7 |

Gesamt grob 8–12 Personentage. Nach Phase 3 ist der AST-Rewriter für die im Spike genutzten Abfragen (Aggregate, Parameter, Zeilenfilter, deklarierte Abfragen) brauchbar; Phasen 4–7 schließen die übrigen Lücken.

## 6. Nicht Teil dieses Plans

- Umschalten des Standards auf `AstCompiler` (eigene Entscheidung nach grünem Differenztest über alle Dialekte).
- Entfernen des Legacy-Rewriters.
- Neue SQL-Funktionen über das hinaus, was `SqlFunctionPolicy` heute erlaubt.
