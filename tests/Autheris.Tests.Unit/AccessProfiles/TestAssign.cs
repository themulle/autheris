namespace Autheris.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;

/// <summary>O-5d: typed user assignments for tests that used the removed plain <c>AssignedSubjects</c> list.</summary>
internal static class TestAssign
{
    public static List<AccessProfileAssignment> Users(params string[] subjects) => Users((IEnumerable<string>)subjects);

    public static List<AccessProfileAssignment> Users(IEnumerable<string> subjects) =>
        subjects.Select(s => new AccessProfileAssignment
        {
            Subject = s,
            SubjectType = AccessProfileSubjectType.User,
            AssignedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedBy = "admin1",
            ApprovedBy = "admin2",
            ApprovedAt = DateTimeOffset.UtcNow,
            Justification = "test fixture",
        }).ToList();
}
