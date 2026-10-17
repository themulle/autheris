namespace Autheris.Application.Sql.ListParameters;

using System;
using Autheris.Domain.Options;
using TrinoSqlEngine.Ast.Capabilities;

/// <summary>Limits of list-valued client parameters (REQ-SQL-128). The internal constants are not options (Phase 3 14.7).</summary>
public readonly record struct ListParameterLimits(int ConfiguredMaxValues, int MaxListParameters)
{
    /// <summary>Hard ceiling of the raw array length: <c>min(10 x configured, 100,000)</c> (SEC-128-09), checked before any element is read.</summary>
    public int RawCeiling => throw new NotImplementedException();

    public static ListParameterLimits From(WebSqlOptions options) => throw new NotImplementedException();

    /// <summary><c>min(configured, MaxBindParameters - 100, MaxInListItems ?? int.MaxValue)</c> (A-6).</summary>
    public static int EffectiveMax(int configured, DialectCapabilities capabilities) => throw new NotImplementedException();
}
