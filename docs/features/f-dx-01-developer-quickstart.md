# F-DX-01: Zero-Config Developer Quickstart & Dev Portal Hub\n\n**Status:** [Done] (100% GA – Wave 2)  \n**Komponenten:** `Profile: Quickstart`, Root-Hub `GET /`, 1-Click Identity Switcher (Alice/Bob/Carol)\n\n---\n\n## 1. Übersicht & Problemstellung
Lange Einarbeitungszeiten und komplexe Setups hemmen die Entwickler-Adoption.

## 2. Architektur & Umsetzung
- Zero-Config Quickstart-Profil mit In-Memory SQLite Seed-Katalog.
- Interaktives Entwickler-Portal auf `GET /`.
- 1-Click Umschaltung zwischen Test-Identitäten zur Evaluierung von ABAC- und RLS-Regeln.

## 3. Business Value
- Reduziert Time-to-First-Query von 30 Minuten auf 30 Sekunden.\n