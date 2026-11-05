# P9: Native C# Ingress/Egress Pipeline

**Components:** `IGatewayMiddleware.cs`, [`GatewayExecutionService.cs`](../../src/Autheris.Application/Services/GatewayExecutionService.cs)

---

## 1. Overview & Problem Statement

API gateways that rely on external out-of-process coprocesses or Lua scripting (such as Kong or Tyk) introduce significant latency overhead. P9 provides native in-process C# DLL/NuGet middlewares for high-performance execution (zero IPC).

---

## 2. Business Value

- **Peak High-Throughput Performance**: Sub-millisecond pipeline latency for critical enterprise hot paths.
- **Familiar .NET Developer Ecosystem**: Write enterprise plugins, custom authenticators, and transformation interceptors using standard C#, DI, and NuGet packages.

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
