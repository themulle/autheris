# P9: Native C# Ingress/Egress Pipeline & Dual-Mode Extensibility

**Status:** [Done] (100% GA – Core Foundation)  
**Components:** [`IGatewayMiddleware.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Interfaces/IGatewayMiddleware.cs), [`GatewayExecutionService.cs`](file:///root/lis-git/autheris/src/Autheris.Application/Services/GatewayExecutionService.cs)

---

## 1. Overview & Problem Statement

API gateways that rely on external out-of-process gRPC co-processes or Lua scripting (such as Kong or Tyk) introduce significant latency overhead (often 2–5 ms per call). P9 provides a dual-mode extensibility framework: native in-process C# DLL/NuGet middlewares for high-performance sub-millisecond execution (<0.1 ms overhead, zero IPC), alongside an optional decoupled gRPC coprocess interface for polyglot microservice teams.

---

## 2. Business Value

- **Peak High-Throughput Performance**: Sub-millisecond pipeline latency for critical enterprise hot paths.
- **Familiar .NET Developer Ecosystem**: Write enterprise plugins, custom authenticators, and transformation interceptors using standard C#, DI, and NuGet packages.
- **Polyglot Flexibility**: Non-.NET teams can still implement gRPC interceptors in Go, Python, or Rust when desired.

---

## 3. Architecture & Capabilities

- In-process pipeline hooks: `OnRequestAsync`, `OnExecutionPlanCreatedAsync`, `OnResultAsync`.
- Full dependency injection support allowing plugins to utilize gateway caching, logging, and metrics.
- Isolated plugin loading with `AssemblyLoadContext` preventing assembly dependency hell.

---

## 4. Usage Example

```csharp
// High-performance in-process C# middleware example
public class ComplianceHeaderMiddleware : IGatewayMiddleware
{
    public async Task InvokeAsync(GatewayContext context, Func<Task> next)
    {
        // Execute pre-request checks
        context.HttpContext.Response.Headers.Append("X-Compliance-Verified", "GDPR-OK");
        
        await next();
        
        // Execute post-execution audit tagging
    }
}
```

---

## 5. Configuration Example

```json
{
  "Gateway": {
    "Extensibility": {
      "Mode": "InProcess",
      "EnableGrpcSidecar": false,
      "SidecarEndpoint": "http://127.0.0.1:50051",
      "TimeoutMs": 100
    }
  }
}
```
