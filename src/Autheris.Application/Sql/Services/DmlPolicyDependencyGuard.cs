namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>A DML target must not be a table that a row policy of the caller reads: writing it would change that policy's outcome.</summary>
internal static class DmlPolicyDependencyGuard
{
    public static bool IsReadByRowPolicy(IEnumerable<IRowPolicy?> policies, TableIdentifier target, string dataSource)
    {
        var wanted = target.ToString();
        foreach (var policy in policies)
        {
            if (policy is null)
            {
                continue;
            }

            // A policy that is not a typed predicate cannot be analyzed: refuse (fail-closed).
            if (policy is not PolicyPredicateRowPolicy typed)
            {
                return true;
            }

            foreach (var name in PolicySubqueryTables.Names(typed.Predicate))
            {
                foreach (var candidate in PolicySubqueryTables.Candidates(name, dataSource))
                {
                    if (string.Equals(candidate.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
