namespace Umbraco.Community.CustomValidator;

using Umbraco.Community.CustomValidator.Enums;

public sealed class CustomValidatorOptions
{
    public bool TreatWarningsAsErrors { get; set; } = false;

    public int CacheExpirationMinutes { get; set; } = 30;

    public ValidationFlagMode EntityFlagMode { get; set; } = ValidationFlagMode.Lazy;
}
