using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Web;
using Umbraco.Community.CustomValidator.Extensions;
using Umbraco.Community.CustomValidator.Services;
using Umbraco.Community.CustomValidator.Validation;
using Umbraco.Extensions;

namespace Umbraco.Community.CustomValidator.Notifications;


public sealed class ContentValidationNotificationHandler(
    IUmbracoContextAccessor umbracoContextAccessor,
    CustomValidationCacheService cacheService,
    CustomValidationStatusCache statusCache,
    CustomValidationService validationService,
    IOptions<CustomValidatorOptions> options,
    ILogger<ContentValidationNotificationHandler> logger)
    :   INotificationAsyncHandler<ContentSavingNotification>,
        INotificationAsyncHandler<ContentPublishingNotification>
{

    /// <summary>
    /// Clears the validation cache for affected documents.
    /// </summary>
    public async Task HandleAsync(ContentSavingNotification notification, CancellationToken cancellationToken)
    {
        foreach (var entity in notification.SavedEntities)
        {
            await cacheService.ClearForDocumentAsync(entity.Key, cancellationToken);
            statusCache.ClearForDocument(entity.Key);
        }
    }

    /// <summary>
    /// Validates publishing entities and cancels the publish operation if validation errors are found.
    /// </summary>
    public async Task HandleAsync(ContentPublishingNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            var umbracoContext = umbracoContextAccessor.GetRequiredUmbracoContext();

            foreach (var entity in notification.PublishedEntities)
            {

                var content = umbracoContext.Content.GetById(preview: true, entity.Key);

                if (content == null)
                {
                    continue;
                }

                var publishingCultures = entity.AvailableCultures
                    .Where(culture => notification.IsPublishingCulture(entity, culture))
                    .ToList();

                (bool hasErrors, string errorMessage) = await ValidateDocumentAsync(content, publishingCultures, cancellationToken);

                if (!hasErrors)
                    continue;

                notification.CancelOperation(new EventMessage(
                    "Custom Validation Failed",
                    errorMessage,
                    EventMessageType.Error
                ));

                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Custom Validator: Unexpected error during publish validation");

            notification.CancelOperation(new EventMessage(
                "Custom Validation Failed",
                "Cannot publish: an unexpected error occurred. Please check the logs.",
                EventMessageType.Error
            ));
        }
    }

    private async Task<(bool HasErrors, string ErrorMessage)> ValidateDocumentAsync(
        IPublishedContent content,
        List<string> cultures,
        CancellationToken cancellationToken)
    {
        var treatWarningsAsErrors = options.Value.TreatWarningsAsErrors;

        if (cultures.Count > 0)
        {
            // Variant content - validate each culture
            foreach (var culture in cultures)
            {
                var response = await validationService.ExecuteValidationAsync(content, culture, cancellationToken);

                if (!response.HasValidationErrors(treatWarningsAsErrors))
                    continue;

                var cultureErrors = response.CountErrors(treatWarningsAsErrors);
                return (true, $"Cannot publish '{content.Name}' (culture: {culture}): {cultureErrors} validation error(s) found.");
            }
        }
        else
        {
            // Invariant content
            var response = await validationService.ExecuteValidationAsync(content, null, cancellationToken);

            if (!response.HasValidationErrors(treatWarningsAsErrors))
                return (false, string.Empty);

            int errorCount = response.CountErrors(treatWarningsAsErrors);

            return (true, $"Cannot publish '{content.Name}': {errorCount} validation error(s) found.");
        }

        return (false, string.Empty);
    }
}
