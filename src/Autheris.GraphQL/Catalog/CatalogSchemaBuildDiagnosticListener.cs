using HotChocolate.Execution;
using HotChocolate.Execution.Instrumentation;

namespace Autheris.GraphQL.Catalog;

/// <summary>
/// Issue #786: Hot Chocolate raises <c>ExecutorCreated</c> when a schema build ends, which is where the "build finished" line
/// with the duration is written. The module logs counts and durations only; no catalog names.
/// </summary>
public sealed class CatalogSchemaBuildDiagnosticListener(CatalogGraphQlTypeModule module) : ExecutionDiagnosticEventListener
{
    public override void ExecutorCreated(string name, IRequestExecutor executor) => module.ReportExecutorCreated();
}
