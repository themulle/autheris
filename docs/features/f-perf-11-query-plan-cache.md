# F-PERF-11: Query Plan Cache & Kestrel Tuning

> [!IMPORTANT]
> The query plan cache part of this feature is superseded. The gateway has no application-level plan cache for SQL or GraphQL: the former compiled-SQL plan cache of the Application layer and its `WebSql:PlanCache` setting were removed. Compiled SQL is cached only inside the SQL engine, as value-free templates, described in [F-DIALECT-02, section 2.3](f-dialect-02-ast-sql-compiler.md#23-value-free-template-cache).

## Engine template cache (summary)

`CompiledSqlTemplateCache` (`src/TrinoSqlEngine/Ast/Emit/CompiledSqlTemplate.cs`) caches the verified output of `ISqlEngine.Compile`:

- **Key.** SHA-256 of the canonical serialization of the whole `CompileRequest` plus the compiler version, the capability table version, the engine query-length limit and the SQL text (`CompileCacheKey.Material`). A hit also compares the full key material, so a hash collision cannot return another statement's template. Row filters, masks and the catalog are validated per table on a hit (shape and catalog fingerprints) instead of being part of the key.
- **No values.** A template holds the SQL text and slot descriptors. Tenant, policy and mask values are never stored; they are rebound from the current request on every hit, so tenants with the same policy shape share a template without sharing values.
- **Integrity.** Only output that passed the coverage proof and the emitted-text check is inserted.
- **Capacity.** 512 entries, first-in-first-out eviction; not configurable.

Policy expressions have their own parse cache inside `PolicyExpressionParser` (`PolicyParseCacheOptions`).

## Kestrel tuning

Kestrel limits are set in `src/Autheris.Api/Program.cs` from `Gateway:Hosting` (`HostingLimitsOptions`):

| Setting | Value |
|---|---|
| `Gateway:Hosting:MaxRequestBodySizeBytes` | Default 2 MiB (1 KiB to 1 GiB); endpoints with large payloads raise it per endpoint |
| `Gateway:Hosting:MaxConcurrentConnections` | Default `0` = no limit |
| `Gateway:Hosting:MaxConcurrentUpgradedConnections` | Default 1,000 |
| HTTP/2 streams per connection | 1,024 (fixed) |
| Keep-alive timeout | 2 minutes (fixed) |
| Request headers timeout | 10 seconds (fixed) |
| Server header, synchronous I/O | Disabled |
