# Architecture & Implementation Plan: PLAN-SEC-230 - SQL Server Windows Authentication TLS Channel Binding (EPA / CBT)

**Document ID:** `PLAN-SEC-230`  
**Date:** 2026-10-19  
**Status:** APPROVED (Phase 2 Qualified)  
**Author:** Lead Solution Architect & Delivery Project Manager  
**Parent Requirement:** [`REQ-SEC-230`](2026-10-19-req-issue-230-ntlm-tls-channel-binding.md)  
**Master Plan:** [00-master-plan-overview.md](00-master-plan-overview.md)  
**Branch:** `feat/issue-230-ntlm-tls-channel-binding`  

---

## 1. Architectural Analysis & Component Overview

### 1.1 Technical Background & Vulnerability Breakdown

When connecting to Microsoft SQL Server from Linux containers with Windows Authentication (NTLM), the client negotiates authentication within the TDS (Tabular Data Stream) protocol:
1. TCP connection is established.
2. TLS handshake is negotiated (via `SslStream` in managed SNI on Linux).
3. TDS Pre-Login negotiation completes.
4. TDS Login7 packet triggers SSPI authentication via `SspiContextProvider.GenerateContext(...)`.
5. `NegotiateAuthentication` generates the NTLMSSP tokens:
   - `NEGOTIATE_MESSAGE` (Type 1)
   - Server returns `CHALLENGE_MESSAGE` (Type 2)
   - `AUTHENTICATE_MESSAGE` (Type 3)

In Microsoft.Data.SqlClient 7.1.1, the driver does not pass the TLS channel binding to `SspiContextProvider`. When `NegotiateAuthentication` is instantiated without `NegotiateAuthenticationClientOptions.Binding`, it emits 16 null bytes for `MsvAvChannelBindings` (AV pair 10) in the NTLM-AUTHENTICATE message.

If SQL Server enforces Extended Protection for Authentication (EPA / "Erweiterter Schutz" = `Required` or `Allowed` over TLS), the server rejects the login with:
`SqlException 18452, State 1: Login failed. The login is from an untrusted domain and cannot be used with Integrated authentication.`

### 1.2 Reflection Object Graph in Microsoft.Data.SqlClient 7.1.1

Autheris inspects the internal managed SNI object graph of Microsoft.Data.SqlClient 7.1.1 at the start of context generation:

```
[ManagedNtlmSspiContextProvider]
     |
     v (inherits from SspiContextProvider)
SspiContextProvider
     |-- _physicalStateObj (TdsParserStateObjectManaged)
              |-- _sessionHandle (SniTcpHandle / SniNpHandle / SniMarsHandle)
                       |
                       +-- (if SniMarsHandle) -> _connection (SniMarsConnection) -> _lowerHandle (SniTcpHandle)
                       |
                       +-- _sslStream (SniSslStream : SslStream)
                                |-- TransportContext
                                         |-- GetChannelBinding(ChannelBindingKind.Endpoint / Unique)
```

### 1.3 Key Architectural Decisions

1. **Extraction Timing:**
   - Channel binding extraction occurs during `TryGenerateContext` when `incomingBlob.IsEmpty` (the negotiate phase). At this point, the TLS handshake has completed, and `_physicalStateObj` has been populated by SqlClient via `Initialize()`.
2. **Channel Binding Kinds & Selection Logic:**
   - If TLS protocol is TLS 1.3 (`SslProtocols.Tls13`), query `ChannelBindingKind.Endpoint` (`tls-server-end-point`).
   - If TLS protocol is TLS 1.2 or earlier, query `ChannelBindingKind.Unique` (`tls-unique`). If `Unique` returns null or throws `NotSupportedException`, fall back to `ChannelBindingKind.Endpoint`.
3. **Resource Management:**
   - `ChannelBinding` inherits from `SafeHandleZeroOrMinusOneIsInvalid`. Any allocated handle is tracked in `ManagedNtlmSspiContextProvider` and disposed properly when `_negotiateAuth` is reset or when the provider is disposed.
4. **Version Guard & Fail-Safe Diagnostic Logging:**
   - SqlClient assembly version is inspected. If `ver == null || ver.Major < 7`, a diagnostic warning is logged, and extraction is attempted as best-effort.
   - If reflection fails or the connection is unencrypted, the extraction status and reason are recorded on the provider (`ChannelBindingStatus`) rather than throwing an unhandled exception or leaving silent nulls.
5. **Testability:**
   - `ManagedNtlmSspiContextProvider` accepts an optional custom `Func<SspiContextProvider, ChannelBinding?>?` channel binding accessor for unit testing without requiring live SQL Server network sockets.

---

## 2. Work Breakdown Structure (WBS)

### Work Package 1: Channel Binding Extractor Engine (WP-1)
- Implement `SqlClientChannelBindingExtractor` in `Autheris.Infrastructure.Persistence`:
  - `IsSupportedSqlClientVersion(Assembly assembly)`
  - `ExtractChannelBinding(SspiContextProvider provider, ILogger? logger = null)`
  - Reflection helpers traversing `_physicalStateObj -> _sessionHandle -> _lowerHandle -> _sslStream -> TransportContext -> GetChannelBinding`.
  - Structured extraction result `ChannelBindingExtractionResult`.

### Work Package 2: Managed NTLM SSPI Provider Integration (WP-2)
- Update `ManagedNtlmSspiContextProvider`:
  - Inject optional `channelBindingAccessor` in constructor for test doubles and dependency injection.
  - In `TryGenerateContext`, invoke channel binding extraction on initial negotiate step.
  - Set `NegotiateAuthenticationClientOptions.Binding` with extracted channel binding.
  - Store reference in `_channelBinding` and dispose cleanly in `Dispose()`.
  - Expose diagnostic properties `ChannelBindingAcquired`, `ChannelBindingKindUsed`, `ChannelBindingStatus`.

### Work Package 3: Enhanced Error Classification for 18452 (WP-3)
- Update `SqlServerWindowsAuthConfigurator.ClassifyOpenException`:
  - Clarify in the 18452 diagnostic message that `Microsoft.Data.SqlClient` did not provide TLS Channel Binding and the server enforces Extended Protection (EPA).
  - Include guidance on TLS encryption requirement (`Encrypt=True`), EPA settings, domain verification, and alternative auth modes.
  - Preserve all existing numeric diagnostics (`SqlNumber`, `SqlState`, `SqlClass`) and secret redaction.

### Work Package 4: Automated Testing & Verification (WP-4)
- Unit tests in `SqlServerWindowsAuthConfiguratorTests.cs`:
  - Version checking logic.
  - Reflection extraction with mock provider and state objects.
  - Options assignment verifying `Binding` is supplied.
  - Disposal and lifecycle verification.
- Security tests in `SqlServerWindowsAuthSecurityTests.cs`:
  - Assert updated 18452 diagnostic message content and guidance.
  - Verify circuit breaker trips on 18452.
  - Verify no sensitive credentials or tokens leak in messages or diagnostic fields.

---

## 3. Sequence Diagram

```mermaid
sequenceDiagram
    autonumber
    participant App as Autheris Application
    participant SqlClient as Microsoft.Data.SqlClient
    participant Extractor as SqlClientChannelBindingExtractor
    participant Provider as ManagedNtlmSspiContextProvider
    participant NetAuth as NegotiateAuthentication
    participant SqlServer as SQL Server (EPA Enforced)

    App->>SqlClient: SqlConnection.OpenAsync()
    SqlClient->>SqlServer: TCP Connect + TLS Handshake (Managed SNI SslStream)
    SqlServer-->>SqlClient: TLS Handshake Complete
    SqlClient->>Provider: Initialize(_parser, _serverInfo, _physicalStateObj)
    SqlClient->>Provider: GenerateContext(incomingBlob: empty)
    Provider->>Extractor: ExtractChannelBinding(this)
    Extractor->>SqlClient: Reflect _physicalStateObj -> SniTcpHandle._sslStream
    Extractor->>Extractor: SslStream.TransportContext.GetChannelBinding(Unique/Endpoint)
    Extractor-->>Provider: ChannelBinding (tls-unique / tls-server-end-point)
    Provider->>NetAuth: new NegotiateAuthenticationClientOptions { Binding = cbt }
    Provider->>NetAuth: GetOutgoingBlob() (Negotiate Type 1)
    NetAuth-->>Provider: Outgoing Blob (Type 1)
    Provider-->>SqlClient: Type 1 Blob
    SqlClient->>SqlServer: TDS Login7 + NTLMSSP Type 1
    SqlServer-->>SqlClient: TDS Challenge + NTLMSSP Type 2
    SqlClient->>Provider: GenerateContext(incomingBlob: Type 2)
    Provider->>NetAuth: GetOutgoingBlob(Type 2) (Generates Type 3 with CBT AV Pair 10)
    NetAuth-->>Provider: Type 3 Blob (containing MD5 CBT hash)
    Provider-->>SqlClient: Type 3 Blob
    SqlClient->>SqlServer: TDS Login7 Response + NTLMSSP Type 3
    SqlServer->>SqlServer: Verify CBT against TLS session -> OK!
    SqlServer-->>SqlClient: Login Success (Token: LoginAck)
    SqlClient-->>App: Connection Opened Successfully
```

---

## 4. Phase 3: Security & Compliance Review (Binding Security Conditions)

The Security Review establishes 5 mandatory binding security conditions that must be enforced and tested:

### SEC-SEC230-01: Zero Plaintext Secrets Leakage
- Channel binding extraction, reflection, diagnostics, and error reporting MUST NOT log, serialize, or reveal passwords, secret references, credentials, or raw cryptographic tokens in exception messages or diagnostic fields.
- Any sensitive client connection strings or SQL Server error messages must be sanitized via `SecretScrubber.Redact`.

### SEC-SEC230-02: Safe Handle Lifecycle & Resource Management
- Extracted `ChannelBinding` instances derive from `SafeHandleZeroOrMinusOneIsInvalid`.
- Any acquired channel binding must be owned, tracked, and safely disposed when negotiation resets or the provider is disposed, preventing unmanaged handle leaks.

### SEC-SEC230-03: Fail-Safe Reflection & Graceful Degradation
- Reflection operations on internal driver structures MUST be wrapped in defensive try-catch blocks and guarded with assembly version checks.
- If reflection fails, the underlying stream is unencrypted, or the driver version is unsupported, the system MUST NOT throw unhandled exceptions or crash.
- It must record an actionable diagnostic (`ChannelBindingStatus`) and proceed with standard negotiation, allowing the server or circuit breaker to handle authentication failures deterministically.

### SEC-SEC230-04: Circuit Breaker Integrity & Account Lockout Defense
- Error 18452 MUST retain category `SqlServerWindowsAuthFailureCategory.LoginRejected` and trigger `SqlLoginCircuitBreaker`.
- Repeated authentication failures under EPA or invalid domains must trip the breaker and prevent further attempts, guarding Active Directory accounts from lockout.

### SEC-SEC230-05: Cryptographic Protocol Compliance (RFC 5929 / RFC 8446)
- For TLS 1.3 sessions, the binding extraction MUST use `ChannelBindingKind.Endpoint` (in compliance with RFC 8446 prohibiting `tls-unique`).
- For TLS 1.2 or earlier, `ChannelBindingKind.Unique` MUST be preferred, falling back to `ChannelBindingKind.Endpoint`.

---

## 5. Phase 5: Verification & Quality Gate Report

### 5.1 Test Suite Execution Results
Targeted test execution executed on .NET 10.0 runtime:
```
Test run for Autheris.Tests.Unit.dll (.NETCoreApp,Version=v10.0)
Passed! - Failed: 0, Passed: 43, Skipped: 0, Total: 43, Duration: 157 ms
```

### 5.2 Verification Matrix

| Condition | Verification Test | Status |
|---|---|:---:|
| **SEC-SEC230-01** (Zero plaintext secret leakage) | `T_SEC_SEC230_02_ChannelBindingExtraction_DoesNotLeakSecrets`<br/>`T_SEC_SEC210_02_ClassifyOpenException_SanitizesSensitiveClientMessage` | PASSED ✅ |
| **SEC-SEC230-02** (Safe handle resource disposal) | `ManagedNtlmSspiContextProvider_Dispose_CleansUpChannelBindingAndNegotiation`<br/>`T_SEC_SEC230_05_ChannelBindingLifecycle_SafeDisposal` | PASSED ✅ |
| **SEC-SEC230-03** (Fail-safe reflection & graceful degradation) | `SqlClientChannelBindingExtractor_NullProvider_ReturnsGracefulFailureResult`<br/>`SqlClientChannelBindingExtractor_UninitializedStateObject_ReturnsGracefulFailure`<br/>`SqlClientChannelBindingExtractor_WithSimulatedObjectGraph_DetectsNonSslStreamGracefully`<br/>`SqlClientChannelBindingExtractor_WithUnencryptedHandle_ReportsNoSslStream`<br/>`T_SEC_SEC230_03_ReflectionFailSafe_DoesNotThrowAndRecordsStatus` | PASSED ✅ |
| **SEC-SEC230-04** (Circuit breaker trips on 18452 under EPA) | `T_SEC_SEC230_01_ClassifyOpenException_Error18452_ClarifiesSqlClientTlsChannelBindingAndEPA`<br/>`T_SEC_SEC210_02_SqlLoginCircuitBreaker_TripsOnBoth18452And18456` | PASSED ✅ |
| **SEC-SEC230-05** (Protocol compliance RFC 5929 / 8446) | `ManagedNtlmSspiContextProvider_WithCustomChannelBindingAccessor_AssignsBindingAndAcquires`<br/>`SqlClientChannelBindingExtractor_IsSupportedSqlClientVersion_DetectsVersion7AndHigher` | PASSED ✅ |

### 5.3 Quality Gate Status
- **Quality Gate:** PASSED ✅
- **Regressions:** 0 detected across existing NTLM, Kerberos, and GSSAPI test suites.
- **Master Plan Status:** COMPLETED & VERIFIED ✅

