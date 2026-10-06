# Implementierungsplan: AST Target Dialect Generator (SQL-Pipeline)

**Referenz:** [ADR-017: Multi-Node State Synchronisation, AST Dialect Generator und RBAC-Konsolidierung](file:///root/autheris/docs/adr/ADR-017-distributed-state-ast-generator-and-rbac.md) (Abschnitt 2)  
**Branch:** `feat/ast-target-dialect-generator`  
**Datum:** 2026-10-06  
**Autoren:** C# Solution Architect & C# Application Security Expert  
**Status:** In Review / Genehmigt für Umsetzung  

---

## 1. Executive Summary & Zielsetzung

Bisher stützt sich die SQL-Sicherheits- und RLS-Transformation im Gateway auf den `TokenStreamRewriter` von ANTLR ([`RlsListener.cs`](file:///root/autheris/src/TrinoSqlEngine/RlsListener.cs)). Diese Methode manipuliert rohe Token-Streams über String-Einschübe (`InsertBefore`, `Replace`, `InsertAfter`).

Trotz umfangreicher Heuristiken und Sicherheits-Guards (`SqlTokenSecurityOptions`, Denylists) birgt dieses heuristische Token-Rewriting latente Risiken durch **Lexer- und Dialekt-Differentials** (`SQ-01`, `SQ-02`, `SQ-05`):
- **Lexer-Diskrepanzen:** Bezeichner-Quoting (z. B. `[...]` in T-SQL vs. Array-Indizierung in ANSI/Trino).
- **String- & Escape-Differentials:** Divergierende Semantiken von Backslashes, Dollar-Quoting (`$$`) und `E'...'`-Literalen.
- **Token-Slicing & Kommentarinjektionen:** Versehentliches Auskommentieren von angehängten Sicherheitsfiltern durch unbalancierte Kommentare (`--`, `/* */`) im Eingabestrom.

**Ziel:** Vollständiger Übergang zu einer echten, deterministischen **Compiler-Pipeline**:
```
Raw SQL Query ──► ANTLR4 ParseTree ──► Typisierte AST-IR ──► Security & RLS Visitor ──► Dialect Code Generator ──► Ziel-SQL
```
Dadurch wird syntaktische Immunität gegen Token-Differentials und Injection-Bypässe *konstruktiv* garantiert (Security by Design).

---

## 2. Architektonisches Design (C# Solution Architect)

### 2.1 Leitplanken & Anti-Overengineering (KISS, YAGNI)
- **Keine tiefen Vererbungshierarchien:** Einsatz moderner C# 12 Records mit Pattern Matching (`switch`-Expressions).
- **Unveränderlichkeit (Immutability):** Alle AST-Knoten sind unveränderliche Records (`init`-only / Primary Constructors). Transformationen erzeugen saubere funktionale Kopien (`with { ... }`).
- **Zero-External-Dependencies:** Die AST-Definitionen und Generatoren verbleiben schlank in `src/TrinoSqlEngine/Ast/` ohne neue Third-Party-Bibliotheken.
- **Allokationsoptimierung:** Code-Generatoren nutzen `StringBuilder`-Pools oder vorgepufferte Kapazitäten, um Garbage-Collection-Druck unter Last zu minimieren.

### 2.2 Ziel-Komponentenstruktur

```
src/TrinoSqlEngine/
├── Ast/
│   ├── Nodes/
│   │   ├── SqlStatement.cs            # Basis-Knoten (SelectStatement)
│   │   ├── TableReference.cs          # Tabellen-, Schema- & Alias-Definitionen
│   │   ├── JoinClause.cs              # INNER, LEFT, RIGHT, FULL, CROSS
│   │   ├── Expression.cs              # BinaryExpr, ColumnRef, Literal, Param, FuncCall
│   │   └── PaginationClause.cs        # Offset & Limit
│   ├── Builder/
│   │   └── SqlAstBuilder.cs           # ANTLR4 ParseTree -> Typisierter AST
│   ├── Visitors/
│   │   ├── ISqlAstVisitor.cs          # Visitor-Interface
│   │   └── AstSecurityVisitor.cs      # RLS, Mandantenfilter & Column-Masking Injektion
│   └── Generators/
│       ├── ISqlDialectGenerator.cs    # Generator-Schnittstelle
│       ├── SqlServerDialectGenerator.cs
│       ├── PostgreSqlDialectGenerator.cs
│       └── SqliteDialectGenerator.cs
```

---

## 3. Detaillierter Phasenplan

### Phase 1: Typisierte AST-Intermediate-Representation (IR)
*Aufwand: 1–2 Tage*
- Definition der Kernmodelle als C# 12 Records:
  - `SelectStatement(IReadOnlyList<SelectItem> Projection, FromClause? From, Expression? Where, GroupByClause? GroupBy, Expression? Having, OrderByClause? OrderBy, PaginationClause? Pagination)`
  - `SelectItem`: `ColumnSelectItem(Expression Expr, string? Alias)`, `StarSelectItem(string? TableQualifier)`
  - `TableSource`: `NamedTableSource(string? Schema, string TableName, string? Alias)`, `SubqueryTableSource(SelectStatement Subquery, string Alias)`
  - `Expression`:
    - `BinaryExpression(Expression Left, BinaryOperator Op, Expression Right)`
    - `ColumnReference(string? Qualifier, string ColumnName)`
    - `LiteralExpression(object? Value, LiteralType Type)`
    - `ParameterReference(string ParameterName, int? PositionalIndex)`
    - `FunctionCallExpression(string FunctionName, IReadOnlyList<Expression> Arguments)`
    - `InListExpression(Expression Left, IReadOnlyList<Expression> Items, bool IsNotIn)`
    - `InSubqueryExpression(Expression Left, SelectStatement Subquery, bool IsNotIn)`
- **Akzeptanzkriterium:** Alle typisierten Modelle sind unit-testbar, unveränderlich und serialisierbar.

---

### Phase 2: ParseTree → AST Builder (`SqlAstBuilder`)
*Aufwand: 2–3 Tage*
- Implementierung von `SqlAstBuilder : SqlBaseBaseVisitor<SqlStatement>` auf Basis der existierenden `SqlBase.g4`-Grammatik.
- **Fail-Closed Validierung im Builder:**
  - DDL (DROP, ALTER, CREATE) und DML-Schreiboperationen im Read-Only-Pfad werden sofort abgewiesen.
  - Abweisung nicht-unterstützter AST-Knoten (z. B. unzulässige Session-Properties, Inline-Funktionen `WITH FUNCTION`).
  - Schutz gegen verschachtelte Rekursionen (AST-Tiefenbegrenzung zur Vermeidung von StackOverflow-Angriffen).
- **Akzeptanzkriterium:** 100% der gültigen SQL-SELECT-Statements der bestehenden Testsuite lassen sich fehlerfrei in die AST-Struktur parsen.

---

### Phase 3: Security & RLS Visitor (`AstSecurityVisitor`)
*Aufwand: 2 Tage*
- Transformation des typisierten AST vor der Code-Generierung:
  1. **Tenant-Isolation & RLS-Injektion:**
     - Wenn keine `WHERE`-Klausel existiert: `Where = rlsExpression`.
     - Wenn `WHERE` bereits vorhanden: `Where = new BinaryExpression(existingWhere, BinaryOperator.And, rlsExpression)`.
     - Unterstützung für Subquery-Pushdown auf Sub-Selects und Joins.
  2. **Column Masking:**
     - Ersetzen maskierter `ColumnReference`-Knoten durch berechnete `FunctionCallExpression` (z. B. Hash, Masking oder Redaction-Konstanten).
  3. **Consent Filtering:**
     - Projektion unautorisierter Spalten wird entweder entfernt oder durch `LiteralExpression(null)` mit Alias ersetzt.
- **Akzeptanzkriterium:** RLS-Prädikate sind unveränderlicher Bestandteil des logischen Baums; ein syntaktisches Umgehen ist ausgeschlossen.

---

### Phase 4: Dialect Code Generators (`ISqlDialectGenerator`)
*Aufwand: 3 Tage*
- Bereitstellung dialektspezifischer Emitter:
  1. **`SqlServerDialectGenerator`:**
     - Bezeichner-Quoting: `[schema].[table]`, `[column]` (inkl. Escapen von `]` als `]]`).
     - Parameter-Syntax: `@p0, @p1, ...`.
     - Paginierung: `ORDER BY (SELECT NULL) OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY` (falls kein `ORDER BY` deklariert ist).
     - Boolesche Prädikate: Saubere Transformation von `true`/`false` in `1=1` bzw. `1=0`.
  2. **`PostgreSqlDialectGenerator`:**
     - Bezeichner-Quoting: `"schema"."table"`, `"column"` (inkl. Escapen von `"` als `""`).
     - Parameter-Syntax: `$1, $2, ...`.
     - Paginierung: `LIMIT $limit OFFSET $offset`.
  3. **`SqliteDialectGenerator`:**
     - Bezeichner-Quoting: `"table"`, `"column"`.
     - Parameter-Syntax: `?1, ?2, ...`.
     - Paginierung: `LIMIT ?1 OFFSET ?2`.
- **Akzeptanzkriterium:** Generiert syntaktisch valides, minimales und dialektkonformes SQL für jeden Ziel-RDBMS-Typ.

---

### Phase 5: Integration & A/B-Parallelbetrieb
*Aufwand: 1–2 Tage*
- Einbindung in `FastSqlEngine`:
  - Neue Methode `FastSqlEngine.GenerateGovernedSql(string query, RlsOptions options)`.
  - Feature-Flag `GatewayOptions:SqlRewriterEngine = "AstCompiler" | "LegacyTokenStream"`, um risikoarme Regressionstests und A/B-Vergleiche im Staging-Betrieb zu erlauben.
- Anbindung an `GovernedSqlExecutionService` in `Autheris.Application`.

---

### Phase 6: Verifikation & Differential Testing
*Aufwand: 2 Tage*
- **Differential-Tests:** Ausführung identischer Abfragen über den alten `RlsListener` und den neuen AST-Generator; Verifikation identischer Ergebnismengen auf Testcontainers-Datenbanken (MSSQL, PostgreSQL, SQLite).
- **Security Penetration Tests:** Ausführung der Angriffsvektoren aus Runde 4/5 gegen den neuen Generator.

---

## 4. Security-Review (C# Application Security Expert)

### 4.1 STRIDE-Bedrohungsanalyse

| Bedrohung (STRIDE) | Risiko im Token-Rewriter | Abmilderung im AST Target Dialect Generator |
| :--- | :--- | :--- |
| **Spoofing / Bypass** | Token-Slicing-Lücken hebeln RLS-Prädikate aus | **Konstruktive Immunität:** RLS ist ein logischer `BinaryExpression.And`-Knoten im Root- und Subquery-Baum. |
| **Tampering** | Eingebettete Kommentare (`--`, `/* */`) manipulieren nachfolgende Filter | **Keine Token-Wiederverwendung:** Der Generator erzeugt Tokens synthetisch; Kommentare aus dem Quell-SQL werden gar nicht in den AST übernommen. |
| **Elevation of Privilege** | Unbeabsichtigte Ausführung von Admin-Befehlen | **Fail-Closed:** Der `SqlAstBuilder` unterstützt ausschließlich validierte DML-Knoten; DDL wirft sofort eine `SecurityException`. |
| **Information Disclosure** | Fehlerhafte Paginierung exponiert fremde Mandantendaten | **Strikte Paginierungs-Compiler:** `FETCH NEXT` / `LIMIT` werden formal validiert und deterministisch generiert. |
| **Denial of Service** | Extrem tief verschachtelte Subqueries überlasten den Parser | **Depth-Guard:** Konfigurierbare maximale Rekursionstiefe (`MaxAstDepth = 64`). |

---

### 4.2 Sicherheits-Leitplanken & Invarianten für die Implementierung

1. **Bezeichner-Sanitisierung & Escaping:**
   - Selbst wenn Bezeichner im AST typisiert sind, **müssen** die Generatoren Dialekt-spezifische Delimiter escapen:
     - T-SQL: `[` und `]` -> `[ColumnName]` mit `]` ersetzt durch `]]`.
     - PostgreSQL: `"` -> `"ColumnName"` mit `"` ersetzt durch `""`.
   - Null-Bytes (`\0`) und Steuerzeichen in Identifiers führen zum sofortigen Parsing-Abbruch.

2. **Parameterisierung vor Literalen:**
   - Dynamische Mandanten-IDs (`tenant_id`), Benutzer-SIDs und Filterwerte dürfen **niemals** als rohe String-Literale in den generierten SQL-String geschrieben werden.
   - Sie müssen ausnahmslos als typisierte Parameter-Referenzen (`@p0`, `$1`, `?1`) im AST geführt und übergeben werden.

3. **Kommentar-Bereinigung:**
   - Der `SqlAstBuilder` ignoriert alle ANTLR-Hidden-Channels (Kommentare). Der generierte SQL-Code enthält keine Benutzerkommentare.

4. **Fail-Closed bei unbekannten Knoten:**
   - Jeder AST-Visitor und Code-Generator muss einen expliziten Fallback `default: throw new NotSupportedException(...)` besitzen. Ein "Stillschweigendes Durchreichen" unbekannter Knoten ist verboten.

---

## 5. Meilensteine & Definition of Done

- [ ] **M1:** Vollständige AST-Knotenstruktur in `src/TrinoSqlEngine/Ast/` implementiert.
- [ ] **M2:** `SqlAstBuilder` parst Standard-SELECTs und wirft Exceptions bei verbotenem SQL.
- [ ] **M3:** `AstSecurityVisitor` wendet RLS-Filter und Column Masking deterministisch an.
- [ ] **M4:** `SqlServerDialectGenerator`, `PostgreSqlDialectGenerator` und `SqliteDialectGenerator` emittieren korrektes SQL.
- [ ] **M5:** Alle Unit-Tests und Security-Attack-Vector-Tests laufen erfolgreich durch.
- [ ] **M6:** Integrationstest mit echtem MSSQL-Container (Testcontainers) bestätigt erfolgreiche End-to-End-Abfrageausführung.
