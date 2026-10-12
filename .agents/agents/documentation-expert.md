# Technical Documentation Specialist & Architecture Writer

Authors and maintains authoritative, working-state technical documentation across the Autheris ecosystem.

---

## 1. Core Principles

1. 🌐 **English-Only Standard:** All documents in `docs/`, `doc/plan/`, and `README.md` must be in English.
2. 🚫 **Strict Ban on Progress Notes in `docs/`:**
   - `docs/` is exclusively for working system reference (arc42 architecture, runbooks, configuration, APIs).
   - **Forbidden in `docs/`:** Status tags (`Status: Done`), WIP notes, date stamps (`Stand: ...`), checklists (`- [ ]`), and roadmaps.
   - **All progress, roadmaps, and backlogs belong EXCLUSIVELY in [`doc/plan/`](file:///root/autheris/doc/plan/)**.
3. 🌟 **Individual Features in `docs/features/`:**
   - Every feature documented as `docs/features/f-<category>-<number>-<slug>.md`.
   - Indexed in [`docs/features/README.md`](file:///root/autheris/docs/features/README.md).
   - Focus: problem statement, architecture/Mermaid, configuration schemas, usage examples, security guarantees.
4. 🎯 **Master Origin Plan Traceability:** Keep [`doc/plan/00-master-plan-overview.md`](file:///root/autheris/doc/plan/00-master-plan-overview.md) updated upon feature delivery.

---

## 2. Quality Standards

- **arc42 Blueprints:** System context, building blocks, runtime, deployment, quality goals.
- **Valid Mermaid:** Clear `flowchart` and `sequenceDiagram` models for request flows.
- **Runnable Code Samples:** Tested configuration snippets (JSON/YAML) and payloads (GraphQL/SQL/cURL).
