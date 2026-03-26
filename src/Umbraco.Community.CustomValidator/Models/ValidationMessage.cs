using System.Diagnostics.CodeAnalysis;
using Umbraco.Community.CustomValidator.Enums;

namespace Umbraco.Community.CustomValidator.Models;

[ExcludeFromCodeCoverage]
public sealed record ValidationMessage
{
    public required string Message { get; set; }

    public required ValidationSeverity Severity { get; set; }
}