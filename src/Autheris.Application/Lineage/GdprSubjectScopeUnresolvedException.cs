namespace Autheris.Application.Lineage;

using System;

/// <summary>
/// MISC-11: the GDPR Art. 15 report cannot be scoped to tables that hold the subject's data (no table named and no subject-to-table
/// mapping exists). Endpoints answer 422 <see cref="ErrorCode"/> instead of a report that would imply a completeness it does not have.
/// </summary>
public sealed class GdprSubjectScopeUnresolvedException() : InvalidOperationException(
    "The GDPR report scope cannot be resolved: name the table that holds the data subject's data (GDPR_SUBJECT_SCOPE_UNRESOLVED).")
{
    public const string ErrorCode = "GDPR_SUBJECT_SCOPE_UNRESOLVED";
}
