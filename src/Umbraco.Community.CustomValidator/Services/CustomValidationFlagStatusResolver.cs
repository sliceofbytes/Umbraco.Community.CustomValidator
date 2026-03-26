using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Extensions;
using Umbraco.Community.CustomValidator.Validation;
using Umbraco.Extensions;

namespace Umbraco.Community.CustomValidator.Services;

/// <summary>
/// Resolves document validation error status for entity flags.
/// </summary>
public sealed class CustomValidationFlagStatusResolver(
    CustomValidationStatusCache statusCache,
    CustomValidationService validationService,
    IUmbracoContextAccessor umbracoContextAccessor,
    IOptions<CustomValidatorOptions> options,
    ILogger<CustomValidationFlagStatusResolver> logger)
{
    public async Task<bool> HasErrorsAsync(Guid documentId, string? culture, CancellationToken cancellationToken = default)
    {
        var mode = options.Value.EntityFlagMode;

        if (mode is ValidationFlagMode.None)
        {
            return false;
        }

        var status = statusCache.GetStatus(documentId, culture);

        if (mode is ValidationFlagMode.Lazy)
        {
            return status is ValidationStatus.HasErrors;
        }

        switch (status)
        {
            case ValidationStatus.HasErrors:
                return true;
            case ValidationStatus.Valid:
                return false;
            case ValidationStatus.Unknown:
            default:
                try
                {
                    var umbracoContext = umbracoContextAccessor.GetRequiredUmbracoContext();
                    var content = umbracoContext.Content.GetById(preview: true, documentId);

                    if (content == null)
                        return false;

                    var treatWarningsAsErrors = options.Value.TreatWarningsAsErrors;
                    var hasErrors = await HasValidationErrorsAsync(content, culture, treatWarningsAsErrors, cancellationToken);
                    statusCache.SetStatus(documentId, hasErrors, culture);

                    return hasErrors;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to resolve eager validation flags for document {DocumentId} (culture: {Culture}), falling back to cached status",
                        documentId,
                        culture ?? "invariant");

                    return false;
                }
        }
    }

    private async Task<bool> HasValidationErrorsAsync(
        IPublishedContent content,
        string? culture,
        bool treatWarningsAsErrors,
        CancellationToken cancellationToken)
    {
        var response = await validationService.ExecuteValidationAsync(content, culture, cancellationToken);
        return response.HasValidationErrors(treatWarningsAsErrors);
    }
}
