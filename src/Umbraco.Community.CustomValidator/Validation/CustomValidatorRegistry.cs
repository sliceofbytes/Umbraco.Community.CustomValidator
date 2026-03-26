using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Interfaces;
using Umbraco.Community.CustomValidator.Models;

namespace Umbraco.Community.CustomValidator.Validation;

public sealed class CustomValidatorRegistry
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ValidatorLookup _validators;
    private readonly ILogger<CustomValidatorRegistry> _logger;

    public CustomValidatorRegistry(
        IServiceScopeFactory serviceScopeFactory,
        ValidatorLookup validators,
        ILogger<CustomValidatorRegistry> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _validators = validators ?? throw new ArgumentNullException(nameof(validators));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content)
    {
        var validatorTypes = _validators.GetValidatorsFor(content.GetType());
        var messages = new List<ValidationMessage>();

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var scopedProvider = scope.ServiceProvider;

        foreach (var validatorType in validatorTypes)
        {
            try
            {
                if (scopedProvider.GetRequiredService(validatorType) is not IDocumentValidator validator)
                {
                    _logger.LogWarning("Could not resolve validator {ValidatorType}", validatorType.Name);
                    continue;
                }

                messages.AddRange(await validator.ValidateAsync(content));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing validator {ValidatorType} for document {DocumentId}",
                    validatorType.Name, content.Id);

                messages.Add(new ValidationMessage
                {
                    Message = "An error occurred while running validation. Please check the logs.",
                    Severity = ValidationSeverity.Error
                });
            }
        }

        return messages;
    }

    public bool HasValidator<T>(T publishedContent) where T : class, IPublishedContent
    {
        var validatorTypes = _validators.GetValidatorsFor(publishedContent.GetType());
        return validatorTypes.Count > 0;
    }
}