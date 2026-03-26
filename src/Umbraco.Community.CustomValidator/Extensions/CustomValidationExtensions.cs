namespace Umbraco.Community.CustomValidator.Extensions;

using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Models;

public static class CustomValidationExtensions
{
    extension(ValidationResponse response)
    {
        public bool HasValidationErrors(bool treatWarningsAsErrors)
        {
            return response is { HasValidator: true, Messages: not null } &&
                   response.Messages.Any(m => IsError(m.Severity, treatWarningsAsErrors));
        }

        public int CountErrors(bool treatWarningsAsErrors)
        {
            return response.Messages?.Count(m => IsError(m.Severity, treatWarningsAsErrors)) ?? 0;
        }
    }

    private static bool IsError(ValidationSeverity severity, bool treatWarningsAsErrors = false)
    {
        return treatWarningsAsErrors
            ? severity == ValidationSeverity.Error || severity == ValidationSeverity.Warning
            : severity == ValidationSeverity.Error;
    }
}
