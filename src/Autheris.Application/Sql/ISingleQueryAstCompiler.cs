namespace Autheris.Application.Sql;

using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface ISingleQueryAstCompiler
{
    bool SupportsDialect(DatabaseDialect dialect);

    string CompileHierarchicalQuery(
        SqlAstNode rootNode,
        DatabaseDialect dialect,
        IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates = null);
}
