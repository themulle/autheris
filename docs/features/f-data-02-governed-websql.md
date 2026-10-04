# F-DATA-02: Governed WebSQL Engine\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** [`FastSqlEngine.cs`](file:///root/lis-git/gql/gql_sqlparser/FastSqlEngine.cs), [`TrinoSqlEngine.csproj`](file:///root/lis-git/gql/gql_sqlparser/TrinoSqlEngine.csproj), [`SqlSecurityValidator.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Sql/SqlSecurityValidator.cs), [`GovernedSqlExecutionService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Sql/Services/GovernedSqlExecutionService.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Direkte Datenbankzugriffe via Port 1433/5432 umgehen Sicherheits-Gateways, während reine REST-APIs Analysten in ihrer Abfrageflexibilität einschränken.

## 2. Architektur & Umsetzung
- HTTP-basierte SQL-Ausführung (`POST /api/v1/sql`) nach Trino-Vorbild.
- ANTLR4-AST Linter: Erzwingt Read-Only, blockiert Multi-Statements und destruktive Systemfunktionen.
- Automatische Injektion von Casbin-RLS in den `WHERE`-Baum und zeilenweise PII-Maskierung.

## 3. Business Value
- Erlaubt sichere Ad-hoc-Analysen im Browser oder via Python ohne Öffnung interner Datenbankports.\n