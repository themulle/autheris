# F-AI-04: Pre-Flight Query Simulator & Safety Limits\n\n**Status:** [Done] (100% GA – Wave 1)  \n**Komponenten:** [`PreFlightQuerySimulator.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Mcp/Services/PreFlightQuerySimulator.cs), Tool `simulate_query`\n\n---\n\n## 1. Übersicht & Problemstellung
Agenten können durch unbedachte Monster-Abfragen oder Endlosschleifen Datenbanken überlasten und enorme Kosten verursachen.

## 2. Architektur & Umsetzung
- AST-basierte Vorab-Simulation ohne DB-Ausführung (`simulate_query`).
- Prüfung von Hard-Safety-Limits: Maximale Komplexität, Token-Budget, geschätzte Scan-Bytes und Rekursionstiefe.
- Warnung und Ablehnung vor dem eigentlichen Datenbank-Zugriff.

## 3. Business Value
- FinOps-Schutz und Überlastungsschutz für Backend-Datenbanken.\n