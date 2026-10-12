# Product Requirements Document (PRD): Universal Multi-App Document Store & Headless Ingestion Gateway

**Document ID:** `REQ-UNIVERSAL-DOC-STORE-14`  
**Date:** October 12, 2026  
**Status:** APPROVED 🎯  
**Track:** AI Documentation & Governance Ecosystem  
**Target Feature:** `F-AI-16`  
**Origin Plan Link:** [`00-master-plan-overview.md`](00-master-plan-overview.md)  
**Concept Document:** [`2026-10-10-konzept-universelle-dokumentenablage-mcp.md`](2026-10-10-konzept-universelle-dokumentenablage-mcp.md)

---

## 1. Executive Summary & Problem Qualification

Enterprise microservices and domain components maintain documentation in silos (e.g. Git markdown repositories, generated OpenAPI specifications, incident runbooks in wikis, and structured error catalogs).
When AI agents (Claude, Antigravity, Cursor, automated incident triage bots) need to resolve production incidents or execute governed schema queries in 100% offline, air-gapped enterprise environments, they cannot consult external search engines or cross-service portals.

**F-AI-16** delivers an enterprise-grade, multi-application "Doc-Lake" foundation:
1. **Shared Volume Foundation:** High-performance, filesystem-backed repository with standard layout (`/mnt/shared-docs/{appId}/`).
2. **Headless REST Ingestion API:** Enables CI/CD pipelines (GitHub Actions, GitLab CI, Jenkins) to publish documentation bundles (ZIP) or individual markdown/spec assets automatically on deployment.
3. **Agent-as-a-Documenter (MCP Deposit):** Equips AI agents with `deposit_documentation` and `list_applications` MCP tools to document troubleshooting findings, runbooks, and error remediations directly into the doc-lake.
4. **Real-time Reactive Ingestion:** Integrates FileSystemWatcher event detection with sub-100ms in-memory BM25 indexing into `IDocSearchService`.

---

## 2. Benchmark & Architectural Fit

- **OWASP AppSec Standard:** Strict defense against **Zip Slip** (arbitrary file overwrite via directory traversal in archive extraction) and path traversal in `appId`/`category`/`docId`.
- **Zero-Network Air-Gapped Operation:** No reliance on cloud object storage (S3/GCS); 100% compliant with Kubernetes `ReadWriteMany` PVC, NFS shares, or local container volume mounts.
- **Fail-Closed Governance:** Invalid manifests or unverified archive structures are rejected without corrupting existing application docs.

---

## 3. Detailed Functional Requirements

### FR-01: Application Manifest (`manifest.yaml`) & Model
- Every registered application has a root `manifest.yaml` specifying `app_id`, `name`, `version`, `domain`, `owner`, `governance_tier`, `entry_points`, and `tags`.
- Ingestion enforces manifest presence or generates a compliant fallback manifest from upload metadata.

### FR-02: Headless REST Ingestion API
- `POST /api/v1/docs/apps/{appId}/bundle`: Accepts multipart/form-data ZIP bundle. Safely extracts files into the application directory with Zip Slip protection. Triggers automatic chunking and indexing.
- `PUT /api/v1/docs/apps/{appId}/documents/{category}/{docId}`: Directly deposits or updates a markdown or YAML specification file.
- `GET /api/v1/docs/apps`: Lists all registered applications with summary metadata.
- `GET /api/v1/docs/apps/{appId}/manifest`: Returns parsed manifest.

### FR-03: MCP Deposit & Multi-App Discovery Tools
- `list_applications`: MCP tool listing all available apps, domains, and tags.
- `deposit_documentation`: MCP tool for AI agents to write runbooks, architecture notes, or error resolutions into the shared volume.

### FR-04: Secure Storage Provider & Event Watcher
- `IUniversalDocStore` abstraction managing filesystem read/write operations, directory sanitization, and path validation.
- FileSystemWatcher monitor triggering atomic re-indexing upon file modification.

---

## 4. Acceptance Criteria

1. **Zip Slip Defense:** Any ZIP entry with relative path elements (`../`) or absolute paths is rejected with `INVALID_ARCHIVE_PATH`.
2. **Path Sanitization:** `appId`, `category`, and `docId` must conform to `^[a-zA-Z0-9_\-\.]+$` and cannot contain path traversal sequences.
3. **MCP Tool Invocations:** AI agents can discover applications via `list_applications` and deposit new runbooks via `deposit_documentation`, immediately making them searchable in `search_docs`.
4. **Clean Build:** `Autheris.sln` compiles with 0 warnings and 0 errors; all unit tests pass.
