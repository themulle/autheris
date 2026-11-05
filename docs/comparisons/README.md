# Autheris Competitive Comparisons

This directory contains deep-dive competitive battle cards comparing **Autheris** against alternative data access gateways, GraphQL routers, and governance platforms.

---

## 📊 Comparison Matrix & Battle Cards

| Competitor | Category | Core Battle Card | Key Differentiation Summary |
|---|---|---|---|
| **Apollo GraphQL** | Schema Federation | [autheris-vs-apollo.md](autheris-vs-apollo.md) | Permissive BSL/Commercial vs. ELv2; **in-gateway Casbin ABAC & Row-Level Security** vs. delegating auth to subgraphs; automated catalog sync. |
| **Hasura Enterprise** | Instant GraphQL / DDN | [autheris-vs-hasura.md](autheris-vs-hasura.md) | Zero proprietary lock-in; decoupled data-owner governance; integrated 4-eyes approval workflow; lower TCO without high per-core licensing. |
| **WunderGraph / Cosmo** | Open-Source Federation | [autheris-vs-wundergraph-cosmo.md](autheris-vs-wundergraph-cosmo.md) | Enterprise compliance: HMAC-SHA256 WORM audit trail, Kerberos/AD support, GDPR Art. 15 disclosure APIs vs. pure BFF developer focus. |
| **Immuta & Privacera** | Data Security Platforms | [autheris-vs-immuta.md](autheris-vs-immuta.md) | Unified API layer bringing database-grade governance directly to GraphQL, REST, and OData without complex database plugins. |

---

## 🎯 Architecture Rules for Comparisons

1. **Anti-Bloat Strategy:** Keep the root [`README.md`](../../README.md) punchy and link to these battle cards for exhaustive technical deep-dives.
2. **Language:** All benchmark reports and battle cards must be written in **English**.
3. **Data-Driven:** Back all competitive claims with reproducible BenchmarkDotNet or test harness figures.
