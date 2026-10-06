# Implementierungsplan: AST Target Dialect Generator (SQL-Pipeline)

**Referenz:** [ADR-017: Multi-Node State Synchronisation, AST Dialect Generator und RBAC-Konsolidierung](file:///root/autheris/docs/adr/ADR-017-distributed-state-ast-generator-and-rbac.md) (Abschnitt 2)  
**Branch:** `feat/ast-target-dialect-generator`  
**Datum:** 2026-10-06  
**Autoren:** C# Solution Architect, C# Application Security Expert & C# Performance Engineer  
**Status:** In Review / Genehmigt für Umsetzung (Enterprise & Hardening Edition)  

---

## 1. Executive Summary & Problemaufriss

Die Absicherung von Multi-Tenant-Datenbankzugriffen im Autheris-Gateway erfolgte historisch über den ANTLR-basierten [`RlsListener`](file:///root/autheris/src/TrinoSqlEngine/RlsListener.cs) unter Verwendung des `TokenStreamRewriter`. Diese Architektur manipuliert den Quelltext durch relative String- und Token-Einschübe (`InsertBefore`, `Replace`, `InsertAfter`) direkt im Trino-Token-Strom.

Obwohl diese Pipeline durch iterative Sicherheitsreviews (Runde 4 & 5, SEC-01 bis SEC-24) sowie strikte Token-Level-Guards ([`SqlTokenSecurityOptions`](file:///root/autheris/src/TrinoSqlEngine/FastSqlEngine.cs#L35-L101)) gehärtet wurde, stößt das heuristische Token-Rewriting an fundamentale Grenzen:

1. **Lexer- & Dialekt-Differentials (`SQ-01`, `SQ-02`, `SQ-05`, `SQL-1`):**
   - **Quoting-Semantik:** T-SQL und SQLite interpretieren eckige Klammern `[...]` als Bezeichner-Quotes, während die ANSI/Trino-Grammatik diese als Array-Konstruktoren bzw. Indizierungs-Operatoren liest. Ein scheinbar harmloser String oder Array-Ausdruck kann in der Zieldatenbank als Bezeichner ausgewertet werden.
   - **String- & Escape-Differentials:** Divergierende Interpretationen von Backslashes (`\`), Hex-Literalen (`0x...`), Dollar-Quoting (`$$...$$` in PostgreSQL) und `E'...'`-Strings.
   - **Kommentar-Injektionen:** Versehentliches oder böswilliges Auskommentieren von angehängten Sicherheitsfiltern durch unbalancierte Kommentare (`--`, `/* ... */`) oder Zeilenumbrüche im Eingabestrom.
2. **Syntaktische Fragilität bei komplexen Abfragen:**
   - Verschachtelte Subqueries, Set-Operationen (`UNION`, `INTERSECT`, `EXCEPT`), Recursive CTEs (`WITH RECURSIVE`) und Multi-Table-Joins erfordern komplexe Positionierungs- und Alias-Berechnungen.
   - Das manuelle Kapseln von Tabellenreferenzen in Subqueries (`(SELECT * FROM table WHERE rls) AS alias`) führt bei bestimmten Dialekten (z. B. SQL Server ohne deklarierte Projektionsaliase) zu syntaktischen Fehlern oder Optimierer-Problemen.
3. **Mangelnde strukturelle Typsicherheit:**
   - Modifikationen finden auf Textebene statt. Ein struktureller Beweis, dass Row-Level-Security (RLS), Column-Masking und Consent-Filter *in jedem erdenklichen AST-Zweig* lückenlos greifen, lässt sich bei reinem String-Rewriting mathematisch nicht garantieren.

### Das Compiler-Paradigma (Security by Design)

Zur vollständigen und konstruktiven Behebung dieser Risikoklasse wird die SQL-Verarbeitung auf eine vollwertige **Compiler-Pipeline** umgestellt:

```
┌─────────────────┐     ┌──────────────────────┐     ┌──────────────────────┐
│  Raw SQL Query  │ ──► │ ANTLR4 Parse Tree    │ ──► │ Typisierte AST-IR    │
│  (Trino/ANSI)   │     │ (SqlBase.g4)         │     │ (C# 12 Records)      │
└─────────────────┘     └──────────────────────┘     └──────────┬───────────┘
                                                                │
                                                                ▼
┌─────────────────┐     ┌──────────────────────┐     ┌──────────────────────┐
│ Dialekt-SQL     │ ◄── │ Dialect Code Emitter │ ◄── │ Security & Governance│
│ (T-SQL, PG, ...)│     │ (Zero-Allocation)    │     │ Visitor (RLS/Masking)│
└─────────────────┘     └──────────────────────┘     └──────────────────────┘
```

**Kernvorteile:**
- **Syntaktische Immunität:** Der Dialect Code Emitter serialisiert ausschließlich typisierte Knoten. Kommentare, Zeilenumbrüche oder rohe Fragmente des Quell-Strings erreichen die Zieldatenbank niemals.
- **Dialekt-Determinismus:** Identifiers werden nach den exakten Quoting- und Escaping-Regeln der Zieldatenbank formatiert (z. B. `[name]` mit `]]`-Escaping für MSSQL, `"name"` mit `""`-Escaping für PostgreSQL). Parameter werden nativ getypt (`@p0`, `$1`, `?1`).
- **Verifizierbare Sicherheitsinvarianten:** Der Security-Visitor manipuliert den logischen Operatorbaum. RLS-Prädikate werden als unverletzliche `BinaryExpression(And)`-Knoten im Root- und Subquery-Scope verankert.

---

## 2. Modul- und Komponentenarchitektur

### 2.1 Verzeichnis- und Klassenstruktur in `src/TrinoSqlEngine/`

```
src/TrinoSqlEngine/
├── Analysis/
│   ├── ISqlQueryAnalyzer.cs           # Existierende Schnittstelle
│   ├── SqlQueryAnalyzer.cs            # Existierender Metadaten-Analyzer
│   └── SqlParameterExtractor.cs       # Extrahierung von @param / {{param}} / ?
├── Ast/
│   ├── Nodes/
│   │   ├── SqlNode.cs                 # Abstrakter Basis-Record aller Knoten
│   │   ├── SqlStatement.cs            # SelectStatement, InsertStatement, UpdateStatement, DeleteStatement
│   │   ├── SqlIdentifier.cs           # Bezeichner, Quoted/Unquoted, Delimited Strings
│   │   ├── SqlQualifiedName.cs        # Mehrteilige Namen: Catalog.Schema.Table.Column
│   │   ├── Projections.cs             # SelectItem, ColumnSelectItem, WildcardSelectItem
│   │   ├── TableSources.cs            # NamedTableSource, SubqueryTableSource, JoinedTableSource, ValuesSource
│   │   ├── Expressions.cs             # Binary, Unary, ColumnRef, Literal, ParameterRef, FuncCall, Case, Cast
│   │   ├── SetOperations.cs           # SetOperationQuery (Union, Intersect, Except mit Distinct/All)
│   │   ├── CommonTableExpressions.cs  # WithClause, CommonTableExpression
│   │   └── Clauses.cs                 # WhereClause, GroupByClause, HavingClause, OrderByClause, PaginationClause
│   ├── Builder/
│   │   ├── SqlAstBuilder.cs           # ANTLR4 ParseTreeVisitor -> AST-IR
│   │   ├── AstBuilderOptions.cs       # Tiefenbegrenzung, DDL-Guards, Validation
│   │   └── AstBuildException.cs       # Spezifische Exception bei ParseTree-IR-Mappingfehlern
│   ├── Visitors/
│   │   ├── ISqlAstVisitor.cs          # Generischer Visitor (Visitor-Pattern)
│   │   ├── SqlAstRewriter.cs          # Basis-Rewriter für funktionale Baumkopien
│   │   ├── AstSecurityVisitor.cs      # RLS-, Maskierungs- und Consent-Injektion
│   │   └── AstValidationVisitor.cs    # Statische Sicherheits- und Konformitätsvalidierung
│   ├── Generators/
│   │   ├── ISqlDialectGenerator.cs    # Emitter-Interface (GenerateSql, TargetDialect)
│   │   ├── SqlDialectGeneratorBase.cs # Gemeinsame Basisfunktionalität (Expressions, Precedence)
│   │   ├── SqlServerDialectGenerator.cs   # T-SQL (@p0, [...], OFFSET FETCH, 1=1)
│   │   ├── PostgreSqlDialectGenerator.cs  # PostgreSQL ($1, "...", LIMIT OFFSET, native booleans)
│   │   ├── SqliteDialectGenerator.cs      # SQLite (?1, "...", LIMIT OFFSET)
│   │   └── AnalyticalDialectGenerators.cs # DuckDB & Snowflake (Lakehouse-Profile)
│   └── Buffer/
│       ├── ValueStringBuilder.cs      # Zero-Allocation Ref Struct Buffer
│       └── SqlEmitterContext.cs       # Zustandskapselung für Emitter (Indent, Parens, Params)
├── FastSqlEngine.cs                   # Fassade: Parse, RewriteRls, GenerateGovernedSql
├── FastSqlEngine.Rewrite.cs           # Legacy-Rewriter-Hilfsmethoden (Deprecation Path)
├── IRlsPolicyProvider.cs              # Policy-Schnittstellen (RLS, Masking, Columns)
├── RlsListener.cs                     # Legacy TokenStream-Rewriter (A/B-Vergleich)
└── SqlBase.g4                         # ANTLR4-Grammatik
```

### 2.2 Integration in die Gateway-Pipeline

```
[HTTP Request / WebSQL / GraphQL]
                 │
                 ▼
 ┌───────────────────────────────┐
 │ GovernedSqlExecutionService   │
 └───────────────┬───────────────┘
                 │
                 ▼
 ┌───────────────────────────────┐
 │ FastSqlEngine                 │
 │ .GenerateGovernedSql(...)     │
 └───────────────┬───────────────┘
                 │
        ┌────────┴────────┐
        ▼                 ▼
[AstCompiler Pfad]    [LegacyTokenStream Pfad (Fallback)]
  SqlAstBuilder         RlsListener
  AstSecurityVisitor    TokenStreamRewriter
  DialectGenerator
        │                 │
        └────────┬────────┘
                 ▼
 ┌───────────────────────────────┐
 │ Ziel-SQL + Typisierte Params  │
 └───────────────┬───────────────┘
                 │
                 ▼
 ┌───────────────────────────────┐
 │ Dapper / ADO.NET Data Source  │
 │ (MSSQL / Postgres / SQLite)   │
 └───────────────────────────────┘
```

---

## 3. Typisiertes AST-Knotenmodell (C# 12 / .NET 10)

Alle AST-Knoten sind unveränderliche (`immutable`) C# 12 Records mit Primary Constructors. Transformationen erzeugen über funktionale Kopien (`with { ... }`) neue, thread-sichere Instanzen ohne Nebeneffekte.

### 3.1 Identifiers, Names & Basis-Knoten

```csharp
namespace TrinoSqlEngine.Ast.Nodes;

using System;
using System.Collections.Generic;

/// <summary>
/// Abstrakter Basisknoten aller AST-Elemente.
/// </summary>
public abstract record SqlNode;

/// <summary>
/// Repräsentiert einen einzelnen Bezeichner (Spalten-, Tabellen- oder Aliasname).
/// </summary>
public sealed record SqlIdentifier(string Value, bool IsQuoted = false) : SqlNode
{
    public override string ToString() => IsQuoted ? $"\"{Value}\"" : Value;
}

/// <summary>
/// Mehrteiliger qualifizierter Name (z. B. catalog.schema.table oder schema.table.column).
/// </summary>
public sealed record SqlQualifiedName(IReadOnlyList<SqlIdentifier> Parts) : SqlNode
{
    public SqlQualifiedName(params string[] parts) 
        : this(parts.Select(p => new SqlIdentifier(p)).ToList()) { }

    public string SimpleName => Parts[^1].Value;
    public bool IsSimple => Parts.Count == 1;

    public override string ToString() => string.Join(".", Parts.Select(p => p.ToString()));
}
```

### 3.2 Statements & Query-Struktur

```csharp
/// <summary>
/// Wurzelknoten eines SQL-Statements.
/// </summary>
public abstract record SqlStatement : SqlNode;

/// <summary>
/// SELECT-Abfrage mit optionalen CTEs, Set-Operationen und Paginierung.
/// </summary>
public sealed record SelectStatement(
    WithClause? With,
    QueryBody Body,
    OrderByClause? OrderBy,
    PaginationClause? Pagination) : SqlStatement;

/// <summary>
/// Abstrakter Körper einer Leseabfrage: Entweder eine atomare QuerySpecification oder eine Set-Operation.
/// </summary>
public abstract record QueryBody : SqlNode;

/// <summary>
/// Standard-SELECT-Spezifikation (Projection, FROM, WHERE, GROUP BY, HAVING).
/// </summary>
public sealed record QuerySpecification(
    bool Distinct,
    IReadOnlyList<SelectItem> Projections,
    TableSource? From,
    Expression? Where,
    GroupByClause? GroupBy,
    Expression? Having) : QueryBody;

/// <summary>
/// Set-Operation zwischen zwei Abfragen (UNION, INTERSECT, EXCEPT).
/// </summary>
public sealed record SetOperationQuery(
    QueryBody Left,
    SetOperator Operator,
    bool Distinct,
    QueryBody Right) : QueryBody;

public enum SetOperator
{
    Union,
    Intersect,
    Except
}
```

### 3.3 Common Table Expressions (CTE) & Projektionen

```csharp
public sealed record WithClause(
    bool IsRecursive,
    IReadOnlyList<CommonTableExpression> Ctes) : SqlNode;

public sealed record CommonTableExpression(
    SqlIdentifier Name,
    IReadOnlyList<SqlIdentifier>? ColumnAliases,
    SelectStatement Query) : SqlNode;

public abstract record SelectItem : SqlNode;

public sealed record ColumnSelectItem(
    Expression Expression,
    SqlIdentifier? Alias) : SelectItem;

public sealed record WildcardSelectItem(
    SqlQualifiedName? Qualifier) : SelectItem;
```

### 3.4 Table Sources & Joins

```csharp
public abstract record TableSource : SqlNode;

public sealed record NamedTableSource(
    SqlQualifiedName Name,
    SqlIdentifier? Alias) : TableSource;

public sealed record SubqueryTableSource(
    SelectStatement Subquery,
    SqlIdentifier Alias) : TableSource;

public sealed record JoinedTableSource(
    TableSource Left,
    JoinType Type,
    TableSource Right,
    JoinCondition? Condition) : TableSource;

public enum JoinType
{
    Inner,
    LeftOuter,
    RightOuter,
    FullOuter,
    Cross
}

public abstract record JoinCondition : SqlNode;
public sealed record OnJoinCondition(Expression Predicate) : JoinCondition;
public sealed record UsingJoinCondition(IReadOnlyList<SqlIdentifier> Columns) : JoinCondition;
```

### 3.5 Expressions, Operatoren & Literale

```csharp
public abstract record Expression : SqlNode;

public sealed record ColumnReference(SqlQualifiedName Name) : Expression;

public sealed record ParameterReference(
    string Name,
    int? PositionalIndex = null) : Expression;

public sealed record LiteralExpression(
    object? Value,
    LiteralType Type) : Expression;

public enum LiteralType
{
    Null,
    Boolean,
    Integer,
    Decimal,
    String,
    Binary
}

public sealed record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right) : Expression;

public enum BinaryOperator
{
    // Logisch
    And,
    Or,
    // Vergleich
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    // Arithmetisch & String
    Add,
    Subtract,
    Multiply,
    Divide,
    Modulo,
    Concat,
    // Muster
    Like,
    NotLike
}

public sealed record UnaryExpression(
    UnaryOperator Operator,
    Expression Operand) : Expression;

public enum UnaryOperator
{
    Not,
    Negate,
    IsNull,
    IsNotNull
}

public sealed record InListExpression(
    Expression Operand,
    IReadOnlyList<Expression> Items,
    bool IsNotIn) : Expression;

public sealed record InSubqueryExpression(
    Expression Operand,
    SelectStatement Subquery,
    bool IsNotIn) : Expression;

public sealed record ExistsExpression(
    SelectStatement Subquery) : Expression;

public sealed record BetweenExpression(
    Expression Operand,
    Expression Lower,
    Expression Upper,
    bool IsNotBetween) : Expression;

public sealed record CaseExpression(
    Expression? Operand,
    IReadOnlyList<WhenClause> WhenClauses,
    Expression? ElseResult) : Expression;

public sealed record WhenClause(
    Expression Condition,
    Expression Result) : SqlNode;

public sealed record FunctionCallExpression(
    SqlQualifiedName Name,
    IReadOnlyList<Expression> Arguments,
    bool Distinct = false,
    WindowSpecification? Window = null) : Expression;

public sealed record WindowSpecification(
    IReadOnlyList<Expression>? PartitionBy,
    OrderByClause? OrderBy) : SqlNode;

public sealed record CastExpression(
    Expression Operand,
    string TargetType,
    bool IsTryCast = false) : Expression;
```

### 3.6 DML-Knoten (INSERT, UPDATE, DELETE)

```csharp
public sealed record DeleteStatement(
    NamedTableSource TargetTable,
    Expression? Where) : SqlStatement;

public sealed record UpdateStatement(
    NamedTableSource TargetTable,
    IReadOnlyList<UpdateAssignment> Assignments,
    Expression? Where) : SqlStatement;

public sealed record UpdateAssignment(
    SqlIdentifier Column,
    Expression Value) : SqlNode;

public sealed record InsertStatement(
    NamedTableSource TargetTable,
    IReadOnlyList<SqlIdentifier>? Columns,
    SelectStatement SourceQuery) : SqlStatement;
```

### 3.7 Sortierung & Paginierung

```csharp
public sealed record OrderByClause(IReadOnlyList<OrderByElement> Elements) : SqlNode;

public sealed record OrderByElement(
    Expression Expression,
    SortDirection Direction = SortDirection.Ascending,
    NullOrdering NullOrder = NullOrdering.Default) : SqlNode;

public enum SortDirection { Ascending, Descending }
public enum NullOrdering { Default, First, Last }

public sealed record PaginationClause(
    Expression? Offset,
    Expression? Limit) : SqlNode;
```

---

## 4. Grammar Mapping Spezifikation (`SqlBase.g4` ➔ AST-IR)

Die Transformation des ANTLR4-ParseTrees in die AST-IR erfolgt im `SqlAstBuilder`, der von `SqlBaseBaseVisitor<SqlNode>` erbt.

### 4.1 Regelzuordnungsmatrix

| `SqlBase.g4` ParseTree Rule | AST-Knotenklasse | Sicherheitsvalidierung / Fail-Closed Guard |
| :--- | :--- | :--- |
| `singleStatement` | `SqlStatement` | Nur `statementDefault`, `insertInto`, `update`, `delete` erlaubt. Alle DDL- und Admin-Regeln werfen `SecurityException`. |
| `statementDefault` ➔ `rootQueryWithSession` | `SelectStatement` | Prüft `WITH SESSION`: Session-Properties werfen Exception, falls nicht explizit in `AllowedSessionProperties`. |
| `rootQuery` | `SelectStatement` | Prüft `WITH FUNCTION`: Inline-Funktionen werden standardmäßig abgewiesen (`AllowInlineFunctionDefinitions == false`). |
| `query` | `SelectStatement.With` | Parst `with`-Klausel; baut `WithClause(Recursive, List<CTE>)`. |
| `queryNoWith` | `SelectStatement` | Extrahiert `queryTerm`, `orderBy`, `offset`, `limit` / `fetch`. |
| `queryTerm` (SetOperation) | `SetOperationQuery` | Rekursive Transformation von `UNION`, `INTERSECT`, `EXCEPT` mit `DISTINCT` / `ALL`. |
| `querySpecification` | `QuerySpecification` | Parst `SELECT [DISTINCT]`, Projektionen, `FROM`, `WHERE`, `GROUP BY`, `HAVING`. |
| `selectSingle` | `ColumnSelectItem` | Parst Expression und optionalen Alias `AS identifier`. |
| `selectAll` | `WildcardSelectItem` | Parst `*` oder `table.*`. |
| `joinRelation` | `JoinedTableSource` | Linkassoziative Auflösung von `CROSS JOIN`, `INNER JOIN`, `LEFT/RIGHT/FULL OUTER JOIN` mit `ON` oder `USING`. |
| `tableName` | `NamedTableSource` | Parst `qualifiedName`. Prüft `queryPeriod` (Time-Travel: `FOR TIMESTAMP AS OF` wird bei `RejectTimeTravelQueries` abgewiesen). |
| `subqueryRelation` | `SubqueryTableSource` | Rekursiver Parse der Subquery; Alias ist syntaktisch zwingend. |
| `tableFunctionInvocation` | Abweisung / Fehler | `TABLE(fn(...))` wird sofort abgewiesen, es sei denn `AllowedTableFunctions` enthält die Funktion (`SEC H-14`). |
| `booleanExpression` (`and`, `or`, `logicalNot`) | `BinaryExpression` / `UnaryExpression` | Wahrung der Operator-Präzedenz (`NOT` vor `AND` vor `OR`). |
| `predicate` (`comparison`, `between`, `inList`, `nullPredicate`) | `BinaryExpression`, `BetweenExpression`, `InListExpression`, `UnaryExpression` | Typisierte Normalisierung aller Prädikatformen. |
| `literals` | `LiteralExpression` | Typisierte Konvertierung (`INTEGER`, `DECIMAL`, `STRING`, `BOOLEAN`, `NULL`). String-Sanitizing. |
| `parameter` (`?`, `@param`) | `ParameterReference` | Abgleich mit deklarierten Abfrageparametern. |
| `functionCall` | `FunctionCallExpression` | Validierung gegen `SqlFunctionPolicy` (Denylist und dialektspezifische Allowlist). |
| `methodCall` / `staticMethodCall` | Abweisung | Sofortige `SecurityException` (`SEC P-01`). |

### 4.2 Depth-Guard & Rekursionsschutz (Anti-DoS)

Zur Abwehr von Denial-of-Service-Angriffen über extrem tief verschachtelte Ausdrücke (`SELECT (((...)))` oder rekursive Subqueries) implementiert `SqlAstBuilder` einen strikten Depth-Guard:

```csharp
public sealed class SqlAstBuilder : SqlBaseBaseVisitor<SqlNode>
{
    private const int MaxAllowedAstDepth = 64;
    private int _currentDepth = 0;

    private IDisposable EnterScope()
    {
        if (++_currentDepth > MaxAllowedAstDepth)
        {
            throw new SecurityException(
                $"SQL query exceeded the maximum allowable AST nesting depth of {MaxAllowedAstDepth}.");
        }
        return new ScopeDisposable(() => _currentDepth--);
    }

    private readonly struct ScopeDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
```

---

## 5. Security & Governance Visitor Transformationen

Die Absicherung von Abfragen erfolgt im `AstSecurityVisitor`, der auf der Basisklasse `SqlAstRewriter` aufbaut. Jede Transformation erzeugt unveränderliche Kopien des AST mit injizierten Sicherheits- und Maskierungs-Prädikaten.

### 5.1 RLS-Injektionsstrategie: Vergleich und Festlegung

Im bisherigen `RlsListener` wurde RLS über Subquery-Kapselung realisiert (`FROM orders` ➔ `FROM (SELECT * FROM orders WHERE tenant_id = 42) AS orders`). Für den AST-Generator definieren wir zwei komplementäre Strategien:

1. **Subquery-Encapsulation (Standard für Multi-Tenant Data-Sharing):**
   - Jede physische Tabelle `T` wird durch einen `SubqueryTableSource`-Knoten ersetzt:
     ```sql
     (SELECT {expanded_columns} FROM T WHERE {rls_filter}) AS T
     ```
   - **Vorteil:** Volle Kapselung. Existierende Outer-Joins, `ON`-Kriterien und komplexe `WHERE`-Klauseln der Originalabfrage müssen nicht semantisch umgebaut werden. Nullable-Spalten von Outer-Joins können den RLS-Filter nicht versehentlich aushebeln.
   - **Kompilier-Invariante:** Bei `TargetSqlDialect.SqlServer` wird für die Subquery zwingend ein Bezeichner-Alias generiert (z. B. `[orders]`), da MSSQL unbenannte Subqueries im FROM-Pfad verbietet.

2. **Direct Predicate Conjunction (Für einfache Single-Table Abfragen & DML):**
   - Für `UPDATE` und `DELETE` ist Subquery-Kapselung auf dem Zieltisch syntaktisch unzulässig. Hier wird das RLS-Prädikat direkt konjunktiv an die `WHERE`-Klausel gebunden:
     ```csharp
     Where = existingWhere != null 
         ? new BinaryExpression(existingWhere, BinaryOperator.And, rlsFilter)
         : rlsFilter;
     ```

### 5.2 Lexikalische CTE-Scope-Verwaltung (`WITH`-Klauseln)

Ein kritischer Angriffsvektor gegen RLS ist das Shadowing physischer Tabellennamen durch CTEs (z. B. `WITH orders AS (SELECT * FROM public_orders) SELECT * FROM orders`).

**Regeln für den `AstSecurityVisitor`:**
1. **Scope-Stack:** Der Visitor führt einen `Stack<HashSet<string>> _cteScopeStack`.
2. **Lexikalisches Eintritts-/Austritts-Timing:**
   - Beim Betreten eines `SelectStatement` mit `WithClause` wird ein neuer Scope auf den Stack gelegt.
   - Der Name einer CTE (`cte.Name.Value`) wird **erst nach dem Besuch des CTE-Körpers** in den aktuellen Scope eingetragen (Exit-Semantik). Dadurch wird verhindert, dass eine CTE sich selbst referenziert (sofern nicht `RECURSIVE`) oder RLS innerhalb ihrer eigenen Definition umgeht.
3. **Physische vs. Virtuelle Tabellenprüfung:**
   - Eine `NamedTableSource` wird nur dann als CTE interpretiert, wenn ihr Bezeichner **unqualifiziert** (Single-Part) ist und im aktuellen CTE-Scope existiert.
   - Qualifizierte Bezeichner (z. B. `schema.orders`) können *niemals* CTEs sein und unterliegen ausnahmslos der RLS-Prüfung (`SEC C-02`).

```csharp
public override SqlNode VisitNamedTableSource(NamedTableSource node)
{
    string tableName = node.Name.ToString();
    string simpleName = node.Name.SimpleName;

    // CTE-Prüfung: Nur unqualifizierte Bezeichner im CTE-Scope werden ignoriert
    if (node.Name.IsSimple && IsCteInScope(simpleName))
    {
        return node; // Keine RLS auf CTE-Referenz anwenden
    }

    if (!_options.PolicyProvider.ShouldApplyPolicy(tableName))
    {
        return ApplyColumnMaskingOnly(node);
    }

    // Erzeuge RLS-Subquery-Knoten
    return CreateSecuredSubqueryTableSource(node);
}
```

### 5.3 Set-Operationen & Verschachtelte Subqueries

Set-Operationen (`UNION`, `INTERSECT`, `EXCEPT`) bilden im AST einen binären Baum (`SetOperationQuery`).

- **Sicherheits-Invariante:** Der Visitor traversiert zwingend in `Left` und `Right`. RLS- und Maskierungsregeln werden auf jeden Ast unabhängig und vollständig angewendet.
- **Top-Level vs. Branch-Paginierung:**
  - `PaginationClause` am `SelectStatement` gilt für das Gesamtergebnis der Set-Operation.
  - Wenn `EnforcedMaxRows > 0` konfiguriert ist, wird das Root-Limit auf die äußerste `PaginationClause` geklemmt. Innere Abfragen behalten ihre expliziten Limits, sofern sie kleiner als `EnforcedMaxRows` sind.

### 5.4 Column Masking & Consent Resolution (`SQ-07`)

1. **Projektions-Expansion bei `WildcardSelectItem` (`*`):**
   - Wenn eine Tabelle geschützte (zu maskierende) Spalten besitzt und im Query ein `SELECT *` vorkommt, fragt der Visitor das Schema über `TableColumnsProvider` ab.
   - Der `WildcardSelectItem` wird in eine Liste von `ColumnSelectItem`-Knoten expandiert:
     - Unmaskierte Spalten: `new ColumnSelectItem(new ColumnReference(colName), null)`
     - Maskierte Spalten: `new ColumnSelectItem(maskExpressionAst, new SqlIdentifier(colName))`
   - **Fail-Closed:** Ist kein `TableColumnsProvider` registriert, die Tabelle enthält aber maskierte Spalten, wird die Abfrage mit einer `SecurityException` abgewiesen.
2. **Consent-Resolution bei Schreiboperationen (`SQ-07`):**
   - Tabellen mit dynamischen Consent-Filtern (`TablesWithConsentRowFilter`) dürfen nicht Ziel von ungeprüften `INSERT`-Statements sein (`RejectConsentFilteredInsert == true`).
   - Der Visitor prüft `InsertStatement.TargetTable` und bricht bei Übereinstimmung sofort ab.

### 5.5 DML-Absicherung: `WITH CHECK OPTION` & Unfiltered DML Guard

1. **`RejectUnfilteredDml`:**
   - Vor der Transformation prüft der Visitor bei `UpdateStatement` und `DeleteStatement`, ob eine syntaktische `WHERE`-Klausel existiert.
   - Tautologien (z. B. `1 = 1`, `'a' = 'a'`, `col = col`, `NOT (1 = 0)`) werden durch statische Expression-Evaluation erkannt und mit `UnfilteredDmlException` verworfen.
2. **`EnforceWithCheckOption` auf `INSERT` und `UPDATE`:**
   - **UPDATE:** Eine Änderung der Mandantenspalte (`TenantColumnName`) wird strikt verboten (`DisallowTenantColumnModificationInUpdate`).
   - **INSERT:** Der Visitor verifiziert, dass die Mandantenspalte explizit in den Zielspalten deklariert ist.
   - Alle Zeilen eines `VALUES`-Blocks sowie alle Zweige eines `INSERT INTO ... SELECT` müssen für die Mandantenspalte exakt mit `ExpectedTenantValue` übereinstimmen (`SEC M-23`). Nicht-Literale (z. B. Unterabfragen, Berechnungen oder Fremdspalten) an der Mandantenposition werden fail-closed abgewiesen.

---

## 6. Dialekt-Emitter: Nuancen & Codegenerierung

Die Generatorklassen implementieren `ISqlDialectGenerator` und emittieren syntaktisch einwandfreien SQL-Code für das jeweilige Ziel-RDBMS.

```csharp
public interface ISqlDialectGenerator
{
    TargetSqlDialect TargetDialect { get; }
    void GenerateSql(SqlStatement statement, ref ValueStringBuilder builder);
    string GenerateSql(SqlStatement statement);
}
```

### 6.1 T-SQL (`SqlServerDialectGenerator`)

- **Bezeichner-Quoting:** Eckige Klammern `[...]`. Schließende Klammern im Bezeichner werden verdoppelt:
  ```csharp
  builder.Append('[');
  builder.Append(identifier.Replace("]", "]]"));
  builder.Append(']');
  ```
- **Parameter-Syntax:** `@p0, @p1, @p2`.
- **Boolesche Literale & Prädikate:** SQL Server besitzt keinen nativen `BOOLEAN`-Datentyp.
  - Literale im SELECT-Pfad: `1` für `true`, `0` für `false`.
  - Prädikate im WHERE-Pfad: `(1 = 1)` für `true`, `(1 = 0)` für `false`.
- **Paginierung:**
  - T-SQL erfordert für `OFFSET ... ROWS FETCH NEXT ... ROWS ONLY` zwingend eine `ORDER BY`-Klausel.
  - Besitzt das AST-Statement keine `OrderByClause`, emittiert der Generator synthetisch:
    ```sql
    ORDER BY (SELECT NULL) OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY
    ```
- **Subquery-Aliase:** Jede abgeleitete Tabelle im FROM-Pfad erhält zwingend einen Bezeichner-Alias (`AS [alias]`).

### 6.2 PostgreSQL (`PostgreSqlDialectGenerator`)

- **Bezeichner-Quoting:** Doppelte Anführungszeichen `"..."`. Anführungszeichen im Bezeichner werden verdoppelt:
  ```csharp
  builder.Append('"');
  builder.Append(identifier.Replace("\"", "\"\""));
  builder.Append('"');
  ```
- **Case-Folding:** Unquotierte Bezeichner werden in Kleinschreibung normalisiert (`SqlIdentifierHelper.FoldIdentifier`).
- **Parameter-Syntax:** `$1, $2, $3`.
- **Boolesche Werte:** Native Schlüsselwörter `TRUE` und `FALSE`.
- **Paginierung:** `LIMIT $limit OFFSET $offset`.
- **Strings:** Standard-konform (`standard_conforming_strings = on`). Kein Backslash-Escaping; einfache Quotes werden als `''` escaped.

### 6.3 SQLite (`SqliteDialectGenerator`)

- **Bezeichner-Quoting:** Doppelte Anführungszeichen `"..."` (ANSI-konform, vermeidet SQLite-Klammerambiguitäten).
- **Parameter-Syntax:** `?1, ?2, ?3`.
- **Boolesche Werte:** Integer-Konstanten `1` und `0`.
- **Paginierung:** `LIMIT ?limit OFFSET ?offset`.

### 6.4 Analytical- & Lakehouse-Dialekte (DuckDB & Snowflake)

- **DuckDB:**
  - Orientiert an PostgreSQL (`"..."`-Quoting, native Booleans, `$1`-Parameter).
  - Optimiert für In-Process-Vektorisierung; vollständige Unterstützung von `STRUCT`- und `LIST`-Zugriffen.
- **Snowflake:**
  - Case-Folding von unquotierten Bezeichnern zu Großbuchstaben (`UPPERCASE`).
  - Doppelquotes für case-sensitive Bezeichner.
  - Parameterformat: `:1, :2` oder `?`.

### 6.5 Dialekt-Vergleichsmatrix

| Eigenschaft / Dialekt | T-SQL (MSSQL) | PostgreSQL | SQLite | DuckDB | Snowflake |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Identifier Quoting** | `[Name]` (`]]`) | `"Name"` (`""`) | `"Name"` (`""`) | `"Name"` (`""`) | `"Name"` (`""`) |
| **Unquoted Case Folding** | Case-Insensitive | Lowercase | Case-Insensitive | Lowercase | Uppercase |
| **Parameter-Marker** | `@p0, @p1` | `$1, $2` | `?1, ?2` | `$1, $2` | `:1, :2` oder `?` |
| **Boolean Literale** | `1` / `0` (`1=1`) | `TRUE` / `FALSE` | `1` / `0` | `TRUE` / `FALSE` | `TRUE` / `FALSE` |
| **Paginierungs-Syntax** | `OFFSET x FETCH y` | `LIMIT y OFFSET x` | `LIMIT y OFFSET x`| `LIMIT y OFFSET x` | `LIMIT y OFFSET x` |
| **Synthetisches OrderBy** | `ORDER BY (SELECT NULL)` | Nicht erforderlich | Nicht erforderlich | Nicht erforderlich | Nicht erforderlich |
| **String-Konkatenation** | `+` oder `CONCAT()` | `\|\|` | `\|\|` | `\|\|` | `\|\|` |

---

## 7. Zero-Allocation Design & Performance Engineering

Gemäß den Richtlinien des `csharp-performance-engineer` darf der Übergang von Token-Manipulation zu einem AST-Compiler zu keiner signifikanten Erhöhung des GC-Drucks (LOH-Allokationen, Gen0/1 Collections) führen.

### 7.1 `ValueStringBuilder` (Ref Struct)

Zur Codegenerierung wird ein stack-basierter, nicht-allozierender Puffer eingesetzt:

```csharp
namespace TrinoSqlEngine.Ast.Buffer;

using System;
using System.Buffers;

/// <summary>
/// Hochperformanter, Zero-Allocation String-Buffer für die Dialekt-Codegenerierung.
/// Arbeitet primär auf Stack-Speicher (Span<char>) und weicht erst bei großen Abfragen
/// auf den ArrayPool<char>.Shared aus.
/// </summary>
public ref struct ValueStringBuilder
{
    private char[]? _arrayToReturnToPool;
    private Span<char> _chars;
    private int _pos;

    public ValueStringBuilder(Span<char> initialBuffer)
    {
        _arrayToReturnToPool = null;
        _chars = initialBuffer;
        _pos = 0;
    }

    public int Length => _pos;

    public void Append(char c)
    {
        int pos = _pos;
        if ((uint)pos < (uint)_chars.Length)
        {
            _chars[pos] = c;
            _pos = pos + 1;
        }
        else
        {
            GrowAndAppend(c);
        }
    }

    public void Append(string? s)
    {
        if (s == null) return;
        int pos = _pos;
        if (s.Length == 1 && (uint)pos < (uint)_chars.Length)
        {
            _chars[pos] = s[0];
            _pos = pos + 1;
            return;
        }
        AppendSlow(s);
    }

    public void AppendSpan(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return;
        if (_pos > _chars.Length - span.Length)
        {
            Grow(span.Length);
        }
        span.CopyTo(_chars.Slice(_pos));
        _pos += span.Length;
    }

    private void Grow(int requiredAdditionalCapacity)
    {
        int newCapacity = Math.Max(_chars.Length * 2, _chars.Length + requiredAdditionalCapacity);
        char[] poolArray = ArrayPool<char>.Shared.Rent(newCapacity);
        _chars.Slice(0, _pos).CopyTo(poolArray);

        char[]? toReturn = _arrayToReturnToPool;
        _chars = _arrayToReturnToPool = poolArray;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }

    public override string ToString()
    {
        string result = new string(_chars.Slice(0, _pos));
        Dispose();
        return result;
    }

    public void Dispose()
    {
        char[]? toReturn = _arrayToReturnToPool;
        this = default;
        if (toReturn != null)
        {
            ArrayPool<char>.Shared.Return(toReturn);
        }
    }
}
```

### 7.2 Pooling & Allokations-Budgets

| Metrik | Altes TokenStreamRewriter | Neues AST Dialect Compiler Ziel |
| :--- | :--- | :--- |
| **Allokation (Standard Query < 500 Zeichen)** | ca. 4,8 KB pro Abfrage | **< 2,2 KB pro Abfrage** (AST-Records im GC Gen0) |
| **LOH Allokationen (> 85.000 Bytes)** | 0 Bytes | **0 Bytes** (strikte Garantie durch ArrayPool) |
| **P95 Latenz (Parse + Transform + Emit)** | 18 µs | **< 28 µs** |
| **Thread-Sicherheit** | Eingeschränkt durch Rewriter-Zustand | **Vollständig zustandslos & nebenläufigkeitssicher** |

---

## 8. Sicherheits-Invarianten & STRIDE-Matrix

### 8.1 STRIDE-Bedrohungs- und Abwehranalyse

| Bedrohung (STRIDE) | Angriffsszenario | Verwundbarkeit im Token-Rewriter | Garantie im AST Dialect Generator |
| :--- | :--- | :--- | :--- |
| **Spoofing** | Angreifer maskiert Tenant-IDs über hexadezimale Literale oder Unicode-Konstrukte | Lexer-spezifische Literale können Heuristiken umgehen | **Striktes Typsystem:** Parameter und Literale sind stark typisierte `LiteralExpression`-Knoten; Re-Serialisierung erzwingt kanonische Form. |
| **Tampering** | Kommentarinjektion (`--`, `/* */`) schneidet injiziertes `WHERE tenant_id = 42` ab | Nachfolgender String wird auskommentiert (`SQL-1`) | **Synthetische Codegenerierung:** Kommentare aus dem Quell-SQL werden nicht in den AST übernommen. Generierter Code ist kommentarfrei. |
| **Repudiation** | Verstecken von DML-Operationen in verschachtelten WITH-Klauseln | Heuristische Statement-Typprüfung übersieht Schreibknoten | **Fail-Closed Statement-Klassifikation:** `SqlAstBuilder` erlaubt ausschließlich typisierte `SelectStatement`, `InsertStatement`, `UpdateStatement`, `DeleteStatement`. |
| **Information Disclosure** | Auslesen maskierter Spalten über Aggregationen oder WHERE-Klauseln in DML (`SEC H-15`) | Token-Rewriter prüft nur Projektionen | **Vollständige Baumtraversierung:** `EnsureNoMaskedColumnReferences` verbietet maskierte Spalten in SET- und WHERE-Ausdrücken ausnahmslos. |
| **Denial of Service** | StackOverflow durch rekursiv geschachtelte Ausdrücke (`((((...))))`) | Parser stürzt unkontrolliert ab | **AST Depth-Guard:** `MaxAllowedAstDepth = 64` bricht pathologische Abfragen deterministisch ab. |
| **Elevation of Privilege** | CTE-Shadowing hebelt Mandantenprüfung physischer Tabellen aus (`SEC-CTE`) | Falsche Zuordnung im Token-Stream | **Lexikalischer Scope-Stack:** Identifiers werden strikt getrennt nach physischen Tabellen und CTE-Symbolen aufgelöst. |

### 8.2 Formale Sicherheits-Invarianten (I1 bis I10)

1. **[I1] Kommentar-Freiheit:** Kein Byte eines Benutzerkommentars gelangt jemals in den generierten Ziel-SQL-Code.
2. **[I2] Deterministische Bezeichner:** Jeder Bezeichner wird mit den exakten Delimitern des Zieldialekts umschlossen; Schließzeichen im Bezeichner werden ausnahmslos maskiert.
3. **[I3] Unverletzliche RLS-Konjunktion:** RLS-Filter werden als logischer `BinaryOperator.And`-Knoten unlösbar im AST verankert.
4. **[I4] Lückenlose CTE-Isolation:** Ein CTE-Name kann eine physische Tabelle innerhalb seines eigenen Definitions-Körpers niemals maskieren oder von RLS befreien.
5. **[I5] Typisierte Parameter:** Dynamische Mandanten- und Filterwerte werden als getypte Parameter (`@p0`, `$1`) übergeben – niemals als Inline-Strings.
6. **[I6] Fail-Closed bei unbekannten Knoten:** Jeder nicht explizit unterstützte AST-Knoten wirft eine `NotSupportedException`. Ein stillschweigendes Durchreichen ist verboten.
7. **[I7] Maskierungs-Unverletzlichkeit:** Bei Wildcard-Projektionen (`SELECT *`) auf Tabellen mit maskierten Spalten wird die Projektion zwingend expandiert oder fail-closed abgewiesen.
8. **[I8] DML-Tautologie-Verbot:** UPDATE- oder DELETE-Statements ohne WHERE oder mit tautologischer WHERE-Bedingung werden vor jeder RLS-Injektion abgewiesen.
9. **[I9] WITH CHECK OPTION Konsistenz:** INSERT- und UPDATE-Operationen validieren den Mandantenwert zwingend gegen die deklarierte Mandanten-Identität.
10. **[I10] Beschränkte Baumtiefe:** Abfragen mit einer AST-Schachtelungstiefe > 64 werden vor Beginn der Transformation abgewiesen.

---

## 9. Phasenplan & Migrationsstrategie

Die Einführung erfolgt in sechs aufeinander aufbauenden, rückwärtskompatiblen Phasen.

```
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  Phase 1     │ ──► │  Phase 2     │ ──► │  Phase 3     │
│  AST-IR &    │     │  AST Builder │     │  Security    │
│  Knoten      │     │  & Guards    │     │  Visitor     │
└──────────────┘     └──────────────┘     └──────────────┘
                                                 │
                                                 ▼
┌──────────────┐     ┌──────────────┐     ┌──────────────┐
│  Phase 6     │ ◄── │  Phase 5     │ ◄── │  Phase 4     │
│  Deprecation │     │  A/B Testing │     │  Dialect     │
│  Legacy      │     │  & Shadowing │     │  Generators  │
└──────────────┘     └──────────────┘     └──────────────┘
```

### Phase 1: Typisiertes AST-Modell & Basis-Infrastruktur
*Dauer: 1–2 Tage*
- Anlegen des Namespaces `TrinoSqlEngine.Ast.Nodes`.
- Implementierung aller C# 12 Record-Hierarchien (Statements, QueryBody, Expressions, Identifiers, TableSources).
- Implementierung des `ValueStringBuilder` und `SqlEmitterContext` im Namespace `TrinoSqlEngine.Ast.Buffer`.
- **Akzeptanzkriterium:** 100% Unit-Test-Abdeckung der AST-Records; Verifikation der Immutability und Serialisierbarkeit.

### Phase 2: ANTLR4 ParseTree ➔ AST Builder (`SqlAstBuilder`)
*Dauer: 2–3 Tage*
- Implementierung von `SqlAstBuilder` als `SqlBaseBaseVisitor<SqlNode>`.
- Einbindung des `MaxAllowedAstDepth = 64` Rekursionsschutzes.
- Fail-Closed Abweisung aller DDL- und administrativen Befehle.
- Unterstützung aller Standard-DML- und Query-Konstrukte (`SELECT`, `INSERT`, `UPDATE`, `DELETE`, `JOIN`, `UNION`).
- **Akzeptanzkriterium:** Alle 980 bestehenden SQL-Parsing-Tests aus `TrinoSqlEngine.Tests` erzeugen erfolgreich einen typisierten AST.

### Phase 3: Security & Governance Visitor (`AstSecurityVisitor`)
*Dauer: 2–3 Tage*
- Implementierung von `ISqlAstVisitor<TResult>` und `SqlAstRewriter`.
- Implementierung des `AstSecurityVisitor`:
  - RLS-Injektion (Subquery-Kapselung und direkte Conjunction).
  - Lexikalische CTE-Scope-Verwaltung mit Stack.
  - Spaltenmaskierung mit automatischer Wildcard-Expansion.
  - Consent-Validierung (`RejectConsentFilteredInsert`).
  - DML-Schutz (`RejectUnfilteredDml`, `EnforceWithCheckOption`, `SEC H-15`).
- **Akzeptanzkriterium:** Alle Sicherheitsprüfungen aus `SecurityRemediationTests.cs` und `SecurityReview20261002SqTests.cs` greifen identisch oder strikter auf dem AST.

### Phase 4: Dialekt-Code-Generatoren (`ISqlDialectGenerator`)
*Dauer: 3 Tage*
- Implementierung von `SqlServerDialectGenerator`:
  - T-SQL Quoting `[...]`, Parameter `@p0`, `OFFSET FETCH`, synthetisches `ORDER BY (SELECT NULL)`.
- Implementierung von `PostgreSqlDialectGenerator`:
  - Quoting `"..."`, Parameter `$1`, `LIMIT OFFSET`, native Booleans.
- Implementierung von `SqliteDialectGenerator`:
  - Quoting `"..."`, Parameter `?1`, `LIMIT OFFSET`.
- Implementierung von `DuckDbDialectGenerator` und `SnowflakeDialectGenerator`.
- **Akzeptanzkriterium:** Generiertes SQL ist syntaktisch valide auf den jeweiligen Ziel-RDBMS.

### Phase 5: A/B-Parallelbetrieb & Shadow Execution
*Dauer: 2–3 Tage*
- Integration in `FastSqlEngine`:
  ```csharp
  public string GenerateGovernedSql(
      ReadOnlyMemory<char> sql, 
      RlsOptions? options = null, 
      CancellationToken cancellationToken = default);
  ```
- Ergänzung der Konfigurationsoption `GatewayOptions:SqlRewriterEngine`:
  - `"LegacyTokenStream"` (Standard im Übergang)
  - `"AstCompiler"` (Neuer Standard nach Freigabe)
  - `"ShadowDualRun"` (Führt beide Engines aus, vergleicht Ergebnisse und loggt Diskrepanzen)
- Differential-Testing: Automatisierter Abgleich beider Engines gegen Live-Datenbanken (Testcontainers: MSSQL, PostgreSQL, SQLite).
- **Akzeptanzkriterium:** 0 semantische Abweichungen in den Ergebnismengen bei 10.000 generierten synthetischen Testabfragen.

### Phase 6: Decommissioning des Legacy Token Rewriters
*Dauer: 1–2 Tage*
- Umstellung des Standardwerts von `SqlRewriterEngine` auf `"AstCompiler"`.
- Markierung von `RlsListener` als `[Obsolete("Superseded by AstSecurityVisitor and Dialect Generators")]`.
- Bereinigung redundanter Lexer-Denylists, die durch den AST konstruktiv überflüssig wurden (z. B. `RejectBracketLexerDifferentials`).
- **Akzeptanzkriterium:** Codebasis bereinigt; alle Testsuites laufen ausschließlich über den AST Target Dialect Generator.

---

## 10. Verifikationskonzept & Test-Matrix

### 10.1 Test-Suiten

```
tests/TrinoSqlEngine.Tests/
├── Ast/
│   ├── SqlAstBuilderTests.cs               # ParseTree -> AST Mapping
│   ├── AstDepthGuardTests.cs               # Anti-DoS Tiefenbegrenzung
│   ├── AstSecurityVisitorRlsTests.cs       # RLS Injection & Scoping
│   ├── AstSecurityVisitorCteTests.cs       # CTE Shadowing & Recursion
│   ├── AstSecurityVisitorMaskingTests.cs   # Wildcard Expansion & Masking
│   ├── AstSecurityVisitorDmlTests.cs       # WithCheckOption & Unfiltered DML
│   └── DialectGenerators/
│       ├── SqlServerDialectTests.cs        # T-SQL Quoting, Booleans, OFFSET
│       ├── PostgreSqlDialectTests.cs       # PG Quoting, $1 Params, LIMIT
│       ├── SqliteDialectTests.cs           # SQLite Quoting, ?1 Params
│       └── AnalyticalDialectTests.cs       # DuckDB / Snowflake Tests
├── Differential/
│   ├── LegacyVsAstDifferentialTests.cs     # Dual-Run Vergleich alter vs. neuer Generator
│   └── TestcontainersE2eTests.cs           # Echte MSSQL-, PG- und SQLite-Container
└── Benchmarks/
    └── AstGeneratorBenchmarks.cs           # BenchmarkDotNet Allokations- und Latenztests
```

### 10.2 Differential Fuzzing mit Testcontainers

Ein automatisierter Fuzzer generiert zufällige, syntaktisch gültige SQL-Queries (inkl. Joins, Group By, Subqueries, Literalen und CTEs) und führt diese über beide Engines aus:
```csharp
[Theory]
[MemberData(nameof(GenerateRandomSqlStatements), 500)]
public async Task Differential_Execution_Yields_Identical_Results(string rawSql)
{
    var legacySql = _legacyEngine.RewriteRls(rawSql.AsMemory(), _options);
    var astSql = _astEngine.GenerateGovernedSql(rawSql.AsMemory(), _options);

    var legacyResult = await ExecuteOnDatabaseAsync(legacySql);
    var astResult = await ExecuteOnDatabaseAsync(astSql);

    Assert.Equal(legacyResult.RowCount, astResult.RowCount);
    Assert.Equal(legacyResult.DataSha256, astResult.DataSha256);
}
```

---

## 11. Meilensteine & Definition of Done

- [ ] **M1 (AST-IR):** Alle typisierten Record-Knoten in `TrinoSqlEngine.Ast.Nodes` implementiert und unit-getestet.
- [ ] **M2 (Builder & Guards):** `SqlAstBuilder` implementiert; Depth-Guard (`MaxAllowedAstDepth = 64`) aktiv; Fail-Closed bei DDL.
- [ ] **M3 (Security Visitor):** RLS-Injektion, CTE-Scoping, Column-Masking, Consent-Checks und DML-Guardrails vollständig implementiert.
- [ ] **M4 (Dialect Generators):** MSSQL-, PostgreSQL- und SQLite-Emitter vollständig implementiert und mit Golden-Master-Snapshots getestet.
- [ ] **M5 (Zero-Allocation Budget):** BenchmarkDotNet bestätigt < 2,5 KB Gen0-Allokation und 0 Bytes LOH pro Abfrage.
- [ ] **M6 (Differential Suite):** 100% Übereinstimmung mit `RlsListener` auf allen 980 bestehenden Testfällen.
- [ ] **M7 (Container E2E):** E2E-Verifikation auf echten Docker-Containern via Testcontainers (MSSQL Server, PostgreSQL 16, SQLite 3).
- [ ] **M8 (Production Readiness):** Feature-Flag `SqlRewriterEngine` in `GovernedSqlExecutionService` integriert und Staging Dark-Launch erfolgreich durchgeführt.
