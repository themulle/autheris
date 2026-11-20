namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Immutable;
using System.Data.Common;
using Autheris.Application.Sql.Internal;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;

public sealed partial class GovernedSqlExecutor
{
    private static (DbDataReader Reader, ImmutableArray<bool> OutputColumnMasks) WrapMetricViewGroupSizeReader(
        DbDataReader rawReader,
        GovernedRewrite rewrite,
        ImmutableArray<bool> maskArray,
        TargetSqlDialect dialect)
    {
        if (rewrite.MetricViewMinGroupSize <= 1 || string.IsNullOrEmpty(rewrite.MetricViewHiddenCountAlias))
        {
            return (rawReader, maskArray);
        }

        if (rawReader.FieldCount <= 0)
        {
            return (rawReader, maskArray);
        }

        int hiddenOrdinal = -1;
        for (int i = 0; i < rawReader.FieldCount; i++)
        {
            if (string.Equals(rawReader.GetName(i), rewrite.MetricViewHiddenCountAlias, StringComparison.OrdinalIgnoreCase))
            {
                hiddenOrdinal = i;
                break;
            }
        }

        if (hiddenOrdinal < 0 || maskArray.IsDefault || maskArray.Length != rawReader.FieldCount)
        {
            throw new GovernedSqlException(GovernedSqlErrorCodes.CompilerInvariant, dialect);
        }

        var filteredMasks = ImmutableArray.CreateBuilder<bool>(maskArray.Length - 1);
        for (int i = 0; i < maskArray.Length; i++)
        {
            if (i != hiddenOrdinal)
            {
                filteredMasks.Add(maskArray[i]);
            }
        }

        var filteredReader = new MetricViewGroupSizeDataReader(rawReader, hiddenOrdinal, rewrite.MetricViewMinGroupSize);
        return (filteredReader, filteredMasks.MoveToImmutable());
    }
}
