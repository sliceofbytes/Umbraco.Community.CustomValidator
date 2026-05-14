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
    IUmbracoContextFactory umbracoContextFactory,
    CustomValidationCacheService cacheService,
    CustomValidationStatusCache statusCache,
    CustomValidationService validationService,
    IOptions<CustomValidatorOptions> options,
    ILogger<ContentValidationNotificationHandler> logger)
    : INotificationAsyncHandler<ContentSavingNotification>,
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
    /// <remarks>
    /// Uses <see cref="IUmbracoContextFactory.EnsureUmbracoContext"/> rather than relying on
    /// <see cref="IUmbracoContextAccessor"/> directly, so the handler works in any execution
    /// context — HTTP requests, background jobs, messaging, migrations,
    /// integration tests, etc. <c>EnsureUmbracoContext</c> reuses an existing context if one
    /// is already present or creates one if not, so there is no behavioral change in the common case.
    /// </remarks>
    public async Task HandleAsync(ContentPublishingNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            using var contextReference = umbracoContextFactory.EnsureUmbracoContext();
            var umbracoContext = contextReference.UmbracoContext;

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
        catch (OperationCanceledException)
        {
            // Cooperative cancellation — let it propagate rather than masking it as a validation failure.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Custom Validator: Unexpected error during publish validation for {EntityCount} entit(y/ies). First entity key: {FirstEntityKey}. Cancelling publish operation.",
                notification.PublishedEntities.Count(),
                notification.PublishedEntities.FirstOrDefault()?.Key);

            notification.CancelOperation(new EventMessage(
                "Custom Validation Failed",
                $"Cannot publish: an unexpected error occurred ({ex.GetType().Name}). Please check the logs.",
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
                var response = await validationService.ExecuteValidationAsync(
                    content,
                    culture,
                    cancellationToken);

                if (!response.HasValidationErrors(treatWarningsAsErrors))
                    continue;

                return (
                    true,
                    BuildErrorMessage(
                        content.Name,
                        culture,
                        response,
                        treatWarningsAsErrors));
            }
        }
        else
        {
            // Invariant content
            var response = await validationService.ExecuteValidationAsync(
                content,
                null,
                cancellationToken);

            if (!response.HasValidationErrors(treatWarningsAsErrors))
                return (false, string.Empty);

            return (
                true,
                BuildErrorMessage(
                    content.Name,
                    null,
                    response,
                    treatWarningsAsErrors));
        }

        return (false, string.Empty);
    }

    private static string BuildErrorMessage(string? contentName,
        string? culture,
        Models.ValidationResponse response,
        bool treatWarningsAsErrors)
    {
        var errorTexts = response.Messages?
            .Where(m => treatWarningsAsErrors
                ? m.Severity is Enums.ValidationSeverity.Error
                    or Enums.ValidationSeverity.Warning
                : m.Severity == Enums.ValidationSeverity.Error)
            .Select(m => m.Message?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        var header = string.IsNullOrWhiteSpace(culture)
            ? $"Cannot publish '{contentName}'"
            : $"Cannot publish '{contentName}' ({culture})";

        if (errorTexts.Count == 0)
        {
            // Defensive fallback.
            var count = response.CountErrors(treatWarningsAsErrors);

            return count > 0
                ? $"{header}: {count} validation error(s) found."
                : $"{header}: validation error(s) found.";
        }

        var countSuffix = errorTexts.Count > 1
            ? $" ({errorTexts.Count} errors)"
            : string.Empty;

        return $"{header}{countSuffix}: {string.Join("; ", errorTexts)}";
    }
}