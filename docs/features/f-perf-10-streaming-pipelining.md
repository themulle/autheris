# F-PERF-10: Split-Engine & Zero-LOH Streaming Result Pipelining\n\n**Status:** [Done] (100% GA – Wave 3)  \n**Komponenten:** [`CrossDomainStreamingPipeliner.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Pushdown/CrossDomainStreamingPipeliner.cs)\n\n---\n\n## 1. Übersicht & Problemstellung
Große Ergebnismengen belasten bei konventioneller Serialisierung den Large Object Heap (LOH) und führen zu GC-Pausen.

## 2. Architektur & Umsetzung
- Streamt zeilenweise Result-Batches über `IAsyncEnumerable<T>` und `System.IO.Pipelines`.
- Zero-Copy Allokation unter Vermeidung des LOH.

## 3. Business Value
- Stabile Sub-Millisekunden-Latenz selbst bei Millionen von Ergebniszeilen.\n