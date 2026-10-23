namespace Autheris.Application.SchemaRegistry.Validation;

using FluentValidation;
using HotChocolate.Language;

public sealed class SchemaRegistrationRequestValidator : AbstractValidator<SchemaRegistrationRequest>
{
    public SchemaRegistrationRequestValidator()
    {
        RuleFor(x => x.ServiceName)
            .NotEmpty().WithMessage("ServiceName is required.")
            .Matches("^[a-zA-Z0-9_\\-]+$").WithMessage("ServiceName may only contain alphanumeric characters, underscores, and hyphens.")
            .MaximumLength(100).WithMessage("ServiceName cannot exceed 100 characters.");

        RuleFor(x => x.Sdl)
            .NotEmpty().WithMessage("Sdl (Schema Definition Language) cannot be empty.")
            .Custom((sdl, context) =>
            {
                if (string.IsNullOrWhiteSpace(sdl))
                    return;

                try
                {
                    var document = Utf8GraphQLParser.Parse(sdl);

                    // MISC-16: duplicates and dangling extensions are rejected at registration (first version included).
                    var (_, errors) = SchemaDocumentNormalizer.Normalize(document);
                    foreach (var error in errors)
                    {
                        context.AddFailure(nameof(SchemaRegistrationRequest.Sdl), $"Invalid schema: {error}");
                    }
                }
                catch (SyntaxException ex)
                {
                    context.AddFailure(nameof(SchemaRegistrationRequest.Sdl), $"Syntax error in GraphQL SDL at line {ex.Line}, col {ex.Column}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    context.AddFailure(nameof(SchemaRegistrationRequest.Sdl), $"Invalid GraphQL SDL: {ex.Message}");
                }
            });
    }
}
