namespace Autheris.Application.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>One hop in a legacy correlated-subquery consent row filter being migrated.</summary>
public sealed record LegacySubqueryHop(
    TableIdentifier Table,
    string TableAlias,
    string LeftJoinColumn,
    string RightJoinColumn);

/// <summary>Legacy correlated-subquery consent filter payload read from pre-migration rows.</summary>
public sealed record LegacySubqueryConsentFilter(
    Guid Id,
    int FilterGroup,
    string ColumnName,
    TableIdentifier? DependentTable,
    string? DependentTableAlias,
    string? ForeignKeyColumn,
    string? PrimaryKeyColumn,
    string? SubqueryFilterPredicateJson,
    string? TargetTemporalColumn,
    string? DependentValidFromColumn,
    string? DependentValidToColumn,
    string? TargetTableAlias,
    IReadOnlyList<LegacySubqueryHop>? AdditionalHops = null);

/// <summary>Legacy consent record with subquery filters awaiting migration.</summary>
public sealed record LegacyConsentRowFilterInput(
    Guid ConsentId,
    TenantId TenantId,
    TableIdentifier TargetTable,
    bool IsRevoked,
    ConsentEffect Effect,
    IReadOnlyList<LegacySubqueryConsentFilter> SubqueryFilters);

/// <summary>Legacy access profile record with a free-SQL predicate to drop.</summary>
public sealed record LegacyAccessProfilePredicateInput(
    TenantId TenantId,
    string ProfileId,
    string? RowFilterPredicate);

/// <summary>A consent row-filter converted into an active generated row policy and a grant row-scope entry.</summary>
public sealed record ConvertedConsentRowFilter(
    Guid ConsentId,
    TenantId TenantId,
    VirtualFilter GeneratedPolicy,
    GrantRowScopeEntry RowScopeEntry);

/// <summary>A consent revoked by migration because its legacy subquery filter could not be represented as a row policy.</summary>
public sealed record MigrationRevokedConsent(
    Guid ConsentId,
    TenantId TenantId,
    TableIdentifier TargetTable,
    string RevokeReason);

/// <summary>Result of converting legacy subquery consent filters and dropping access-profile predicates.</summary>
public sealed record RowPolicyMigrationResult(
    IReadOnlyList<ConvertedConsentRowFilter> ConvertedFilters,
    IReadOnlyList<MigrationRevokedConsent> RevokedConsents,
    IReadOnlyList<(string TenantId, string ProfileId)> DroppedProfilePredicates);

/// <summary>
/// Converts expressible legacy correlated-subquery consent filters into active generated <see cref="VirtualFilter"/> definitions
/// plus <see cref="GrantRowScopeEntry"/> entries, revokes consents with unconvertible filters, and drops legacy free-SQL
/// predicates on access profiles.
/// </summary>
public static class RowPolicyMigrationConverter
{
    public const string UnconvertibleRevokeReason = "OD38_ROW_FILTER_NOT_CONVERTIBLE";
    public const string MigrationStepCommit = "0018_consent_row_scopes";

    public static RowPolicyMigrationResult Convert(
        IReadOnlyList<LegacyConsentRowFilterInput> consents,
        IReadOnlyList<LegacyAccessProfilePredicateInput> profiles)
    {
        ArgumentNullException.ThrowIfNull(consents);
        ArgumentNullException.ThrowIfNull(profiles);

        var converted = new List<ConvertedConsentRowFilter>();
        var revoked = new List<MigrationRevokedConsent>();

        foreach (var consent in consents)
        {
            if (consent.IsRevoked || consent.SubqueryFilters.Count == 0)
            {
                continue;
            }

            if (consent.Effect == ConsentEffect.Deny || consent.SubqueryFilters.Select(f => f.FilterGroup).Distinct().Count() > 1)
            {
                revoked.Add(new MigrationRevokedConsent(consent.ConsentId, consent.TenantId, consent.TargetTable, UnconvertibleRevokeReason));
                continue;
            }

            var perConsent = new List<ConvertedConsentRowFilter>();
            bool allConvertible = true;
            for (int i = 0; i < consent.SubqueryFilters.Count; i++)
            {
                if (!TryConvertFilter(consent, consent.SubqueryFilters[i], i + 1, out var entry))
                {
                    allConvertible = false;
                    break;
                }

                perConsent.Add(entry!);
            }

            if (allConvertible)
            {
                converted.AddRange(perConsent);
            }
            else
            {
                revoked.Add(new MigrationRevokedConsent(consent.ConsentId, consent.TenantId, consent.TargetTable, UnconvertibleRevokeReason));
            }
        }

        var droppedProfiles = profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.RowFilterPredicate))
            .Select(p => (p.TenantId.Value, p.ProfileId))
            .ToList();

        return new RowPolicyMigrationResult(converted, revoked, droppedProfiles);
    }

    private static bool TryConvertFilter(
        LegacyConsentRowFilterInput consent,
        LegacySubqueryConsentFilter filter,
        int ordinal,
        out ConvertedConsentRowFilter? converted)
    {
        converted = null;
        if (filter.DependentTable is not { } depTable)
        {
            return false;
        }

        // Temporal validity columns compare against a target temporal column and cannot map cleanly without target binding metadata.
        if (!string.IsNullOrWhiteSpace(filter.TargetTemporalColumn) ||
            !string.IsNullOrWhiteSpace(filter.DependentValidFromColumn) ||
            !string.IsNullOrWhiteSpace(filter.DependentValidToColumn))
        {
            return false;
        }

        string source = consent.TargetTable.Domain;
        if (string.IsNullOrWhiteSpace(source) ||
            !string.Equals(depTable.Domain, source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string targetAlias = string.IsNullOrWhiteSpace(filter.TargetTableAlias) ? "target" : filter.TargetTableAlias.Trim();
        string depAlias = string.IsNullOrWhiteSpace(filter.DependentTableAlias) ? "dep" : filter.DependentTableAlias.Trim();
        string targetFk = string.IsNullOrWhiteSpace(filter.ForeignKeyColumn) ? filter.ColumnName.Trim() : filter.ForeignKeyColumn.Trim();
        string depPk = string.IsNullOrWhiteSpace(filter.PrimaryKeyColumn) ? "id" : filter.PrimaryKeyColumn.Trim();

        if (string.Equals(depAlias, targetAlias, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(depAlias, "autheris_target", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var joins = new List<FilterJoin>();
        if (filter.AdditionalHops is { Count: > 0 } hops)
        {
            foreach (var hop in hops)
            {
                if (!string.Equals(hop.Table.Domain, source, StringComparison.OrdinalIgnoreCase) ||
                    ReferencesTargetAlias(hop.LeftJoinColumn, targetAlias) ||
                    ReferencesTargetAlias(hop.RightJoinColumn, targetAlias))
                {
                    return false;
                }

                joins.Add(new FilterJoin(
                    hop.Table,
                    hop.TableAlias.Trim(),
                    hop.LeftJoinColumn.Trim(),
                    hop.RightJoinColumn.Trim()));
            }
        }

        if (!TryParseWhere(filter.SubqueryFilterPredicateJson, depAlias, targetAlias, out var where))
        {
            return false;
        }

        string policyName = $"migrated_{consent.ConsentId:N}_{ordinal.ToString(CultureInfo.InvariantCulture)}";
        if (policyName.Length > 64)
        {
            return false;
        }

        var vf = new VirtualFilter
        {
            Name = policyName,
            TenantId = consent.TenantId,
            Source = source,
            Structured = new StructuredFilterDefinition
            {
                From = depTable,
                FromAlias = depAlias,
                Joins = joins,
                Where = where!
            },
            KeyColumns = [$"{depAlias}.{depPk}"],
            Status = FilterApprovalStatus.Active,
            EvaluationMode = RowPolicyEvaluationMode.Live,
            ManagedBy = new ManagedBy("Migration", MigrationStepCommit)
        };

        try
        {
            vf.Validate();
        }
        catch (ArgumentException)
        {
            return false;
        }

        Dictionary<string, string>? columnMap = string.Equals(depPk, targetFk, StringComparison.OrdinalIgnoreCase)
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [depPk] = targetFk };

        converted = new ConvertedConsentRowFilter(
            consent.ConsentId,
            consent.TenantId,
            vf,
            new GrantRowScopeEntry(
                vf.Name,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                columnMap,
                UncoveredPolicy.Deny));
        return true;
    }

    private static bool TryParseWhere(
        string? json,
        string defaultAlias,
        string targetAlias,
        out IReadOnlyList<FilterCondition>? where)
    {
        where = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            where = [];
            return true;
        }

        var conditions = new List<FilterCondition>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (ReferencesTargetAlias(prop.Name, targetAlias))
                    {
                        return false;
                    }

                    string qualified = Qualify(prop.Name.Trim(), defaultAlias);
                    if (prop.Value.ValueKind == JsonValueKind.Null)
                    {
                        conditions.Add(new FilterCondition(qualified, FilterConditionOperator.IsNull));
                    }
                    else if (TryExtractScalar(prop.Value, out string? val))
                    {
                        conditions.Add(new FilterCondition(qualified, FilterConditionOperator.Eq, val));
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    if (elem.ValueKind != JsonValueKind.Object ||
                        !elem.TryGetProperty("column", out var colElem) ||
                        colElem.ValueKind != JsonValueKind.String ||
                        !elem.TryGetProperty("value", out var valElem))
                    {
                        return false;
                    }

                    string col = colElem.GetString()!.Trim();
                    if (ReferencesTargetAlias(col, targetAlias))
                    {
                        return false;
                    }

                    string op = elem.TryGetProperty("op", out var opProp) && opProp.ValueKind == JsonValueKind.String
                        ? opProp.GetString()!.Trim().ToUpperInvariant()
                        : "EQ";

                    string qualified = Qualify(col, defaultAlias);
                    if (valElem.ValueKind == JsonValueKind.Null)
                    {
                        if (op is not ("EQ" or "NEQ"))
                        {
                            return false;
                        }

                        conditions.Add(new FilterCondition(
                            qualified,
                            op == "NEQ" ? FilterConditionOperator.IsNotNull : FilterConditionOperator.IsNull));
                    }
                    else
                    {
                        if (!TryExtractScalar(valElem, out string? scalar))
                        {
                            return false;
                        }

                        var mappedOp = op switch
                        {
                            "EQ" => FilterConditionOperator.Eq,
                            "NEQ" => FilterConditionOperator.NotEq,
                            _ => (FilterConditionOperator?)null
                        };
                        if (mappedOp is null)
                        {
                            return false;
                        }

                        conditions.Add(new FilterCondition(qualified, mappedOp.Value, scalar));
                    }
                }
            }
            else
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        where = conditions;
        return true;
    }

    private static bool TryExtractScalar(JsonElement element, out string? scalar)
    {
        scalar = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
        return scalar is not null;
    }

    private static string Qualify(string column, string defaultAlias) =>
        column.Contains('.', StringComparison.Ordinal) ? column : $"{defaultAlias}.{column}";

    private static bool ReferencesTargetAlias(string column, string targetAlias)
    {
        int dot = column.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return false;
        }

        string alias = column[..dot].Trim();
        return string.Equals(alias, targetAlias, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(alias, "autheris_target", StringComparison.OrdinalIgnoreCase);
    }
}
