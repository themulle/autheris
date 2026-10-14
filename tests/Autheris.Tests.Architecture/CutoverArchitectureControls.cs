using System.Data.Common;

namespace Autheris.Tests.Architecture;

// Fixtures of the controls in CutoverArchitectureTests. Top-level types on purpose: the call graph folds nested types into their
// top-level type, so nested fixtures could not tell the types apart.

internal interface IControlEngine
{
    object Compile(string sql);
}

internal sealed class ControlEngine : IControlEngine
{
    public object Compile(string sql) => sql;
}

/// <summary>Has a method named like the engine's but implements nothing: a virtual call to the engine must not be linked to it.</summary>
internal sealed class ControlUnrelatedCompiler
{
    public object Compile(string sql) => sql;
}

/// <summary>An entry type with a governed handler and a raw SQL handler (assigns CommandText and never compiles).</summary>
internal sealed class ControlEntry(IControlEngine engine)
{
    public object Governed(string sql) => engine.Compile(sql);

    public void Raw(DbCommand command, string sql)
    {
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public object ViaUnrelated(ControlUnrelatedCompiler other, string sql) => other.Compile(sql);
}

/// <summary>Creates a bind marker outside the policy predicate renderer: the control of the bind marker scan.</summary>
internal sealed class ControlBindMarkerFactory
{
    public TrinoSqlEngine.Ast.Nodes.NamedBindMarkerExpression Create(string name) => new(name);
}
