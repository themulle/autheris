# Product Requirements Document (PRD): Generic C# DLL Plugin & Event/Webhook Callback Subsystem

**Document ID:** `REQ-GENERIC-PLUGIN-EVENTS-2026-10-13`  
**Author:** Enterprise Product Manager & Solution Architect  
**Status:** PROPOSED 📝  
**Date:** 2026-10-13  
**Target Release:** v1.1.8  

---

## 1. Executive Summary & Business Rationale

Autheris currently supports HTTP data source plugins (`IHttpDataSourcePlugin`) for querying custom HTTP APIs, as well as several provider-specific webhooks (e.g. OpenMetadata, dbt Cloud). However, enterprise customers operating in air-gapped, on-premise, or hybrid infrastructures require:

1. **Parity Between Out-of-Process Webhooks and In-Process C# DLL Plugins:**  
   Every event, state change, and action that can be queried or notified via REST API or outbound webhook must also be subscribable and actionable via a native C# DLL plugin running inside the gateway process.
2. **Unified, Generic Event Backbone:**  
   Instead of bespoke point-to-point webhook notifications, the gateway needs an event bus that standardizes domain events (Governance, Catalog, Policy, Security, Query Execution, Documentation) into CloudEvents v1.0 envelopes.
3. **Dual Dispatch Architecture:**  
   When a domain event occurs, the gateway dispatches it simultaneously to:
   - **In-Process C# DLL Plugins:** Sandboxed, zero-latency notification via `IPluginEventListener<TEvent>` and action resolution via `IPluginActionHandler`.
   - **Out-of-Process Webhook Subscribers:** Secure HMAC-SHA256 signed HTTP POST deliveries with replay protection and retry policies.

---

## 2. User Personas & Use Cases

| Persona | Problem Statement | Solution with This Feature |
|---|---|---|
| **Enterprise Platform Engineer** | Needs to integrate Autheris with internal C# compliance DLLs without setting up external HTTP webhook listener servers. | Drops a compiled `.dll` into `plugins/` that implements `IPluginEventListener<AccessPlanAppliedEvent>` to automatically provision Active Directory permissions. |
| **SecOps / SIEM Integrator** | Wants real-time notification of prompt injection attacks and Casbin ABAC policy denials. | Configures an outbound webhook subscription to push `SecurityAlertEvent` and `PolicyDeniedEvent` to Splunk/Datadog with HMAC signatures. |
| **Data Steward** | Wants notification whenever a dataset state changes (active/inactive) or when new documentation is deposited. | Subscribes via plugin or webhook to `DatasetStateChangedEvent` and `DocumentDepositedEvent`. |

---

## 3. Scope of Events (The Gateway Event Catalog)

| Event Category | Concrete Event Type | Trigger Point | Payload Details |
|---|---|---|---|
| **Governance** | `governance.access_plan.created` | `AccessPlanningService.CreatePlanAsync` | Dataset ID, Plan ID, Requester, Grant count |
| **Governance** | `governance.access_plan.applied` | `AccessPlanningService.ApplyPlanAsync` | Dataset ID, Plan ID, Approver, Applied grants |
| **Governance** | `governance.consent.decided` | `IConsentRepository.DecideConsentAsync` | Consent ID, Subject, Dataset, Decision (Approved/Denied) |
| **Catalog** | `catalog.dataset.state_changed` | `AccessPlanningService.SetDatasetStateAsync` | Dataset ID, OldState, NewState, Reason |
| **Catalog** | `catalog.schema.drift_detected` | Schema validation / drift checker | Table ID, Detected drift changes |
| **Security** | `security.policy.denied` | `AiDataGuardrailService` / Casbin | Subject, Action, Resource, Reason |
| **Security** | `security.alert.prompt_injection` | `AiDataGuardrailService` prompt guardrail | Tool name, Detected attack type, Snippet |
| **Documentation** | `docs.document.deposited` | `IUniversalDocStore.DepositDocumentAsync` | App ID, Category, Doc ID, Title |

---

## 4. Functional Requirements

### FR-01: In-Process Event Bus (`IGatewayEventBus`)
- The gateway shall provide an asynchronous, high-throughput in-process event bus (`IGatewayEventBus`).
- Publishing an event shall be non-blocking and isolated: an unhandled exception in one subscriber must never disrupt the publishing service or other subscribers.

### FR-02: C# DLL Plugin Event Subscriptions (`IPluginEventListener<TEvent>`)
- The existing `PluginManager` and `PluginAssemblyLoadContext` shall scan loaded assemblies for `IPluginEventListener<TEvent>` and `IPluginActionHandler`.
- When an event is published on the event bus, matching plugin event listeners must be invoked asynchronously.
- Plugins are validated against `Plugins:TrustedPluginHashes` for SHA-256 integrity.

### FR-03: Generic Outbound Webhook Dispatcher (`IWebhookDispatcherService`)
- Subscriptions can be configured statically in `appsettings.json` under `Gateway:Webhooks:Subscriptions` or dynamically.
- Webhooks must be delivered as standard CloudEvents v1.0 JSON payloads.
- Each webhook request must carry:
  - `X-Autheris-Signature-256`: Hex-encoded HMAC-SHA256 signature of the payload using the subscription's secret key.
  - `X-Autheris-Timestamp`: Unix timestamp to prevent replay attacks.
- Configurable retry policy with exponential backoff.

### FR-04: Extensible In-Process Plugin Action Handlers (`IPluginActionHandler`)
- Plugins can register custom named action handlers that can be invoked internally or via dispatchers, allowing plugins to serve as custom resolvers.

---

## 5. Non-Functional & Security Requirements

1. **Fail-Safe & Isolation:** Plugin failures must never bring down the gateway process. All plugin invocations must be wrapped in try/catch with structured diagnostic logging.
2. **Zero-Trust & Integrity:** All C# plugin DLLs must pass SHA-256 integrity validation (`PluginTrustList`) before loading.
3. **High Performance .NET 10:** Zero allocations in hot path event checking, async I/O consistency, and cancellation token propagation throughout.
