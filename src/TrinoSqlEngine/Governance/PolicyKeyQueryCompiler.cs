namespace TrinoSqlEngine.Governance;

using System;
using System.Threading;

/// <summary>Issue #107 WP-B owns the implementation (PLAN-GOV-107 A-3, SEC-RF107-11). Contract stub only.</summary>
public sealed class PolicyKeyQueryCompiler : IPolicyKeyQueryCompiler
{
    public CompiledKeyQuery CompileKeyQuery(PolicyKeyQueryRequest request, CancellationToken ct)
        => throw new NotImplementedException("WP-B: PolicyKeyQueryCompiler");
}
