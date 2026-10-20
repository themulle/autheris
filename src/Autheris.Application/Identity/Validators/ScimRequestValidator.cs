namespace Autheris.Application.Identity.Validators;

using System;
using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Identity;

public static class ScimRequestValidator
{
    public static void ValidateUserCreate(ScimUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(user.UserName))
        {
            throw new ValidationException("SCIM User requires 'userName'.");
        }
    }

    public static void ValidateGroupCreate(ScimGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(group.DisplayName))
        {
            throw new ValidationException("SCIM Group requires 'displayName'.");
        }
    }
}
