namespace TrinoSqlEngine.Governance;

using TrinoSqlEngine.Ast.Emit;

/// <summary>A value bound by the gateway (tenant, policy, mask argument). It never originates from request text.</summary>
public readonly record struct PolicyValue(object? Value, SqlParameterType Type);
