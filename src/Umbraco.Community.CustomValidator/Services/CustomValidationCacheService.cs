using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.CustomValidator.Models;

namespace Umbraco.Community.CustomValidator.Services;

/// <summary>
/// Handles caching of document validation results using HybridCache with tag-based invalidation.
/// </summary>
public sealed class CustomValidationCacheService(
    HybridCache cache,
    IOptions<CustomValidatorOptions> options,
    ILogger<CustomValidationCacheService> logger)
{
    private const string CacheKeyPrefix = "customValidation";

    /// <summary>
    /// Gets or sets a cached validation result.
    /// </summary>
    public async Task<ValidationResponse> GetOrSetAsync(
        Guid documentId, 
        string? culture, 
        Func<CancellationToken, ValueTask<ValidationResponse>> factory,
        CancellationToken cancellationToken = default)
    {
        // Skip caching if disabled
        if (options.Value.CacheExpirationMinutes <= 0)
        {
            logger.LogDebug("Caching disabled, executing validation directly for document {DocumentId}", documentId);
            return await factory(cancellationToken);
        }

        var cacheKey = GetCacheKey(documentId, culture);
        var tags = GetCacheTags(documentId, culture);

        var result = await cache.GetOrCreateAsync(
            cacheKey,
            factory,
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(options.Value.CacheExpirationMinutes),
                LocalCacheExpiration = TimeSpan.FromMinutes(options.Value.CacheExpirationMinutes),
                Flags = HybridCacheEntryFlags.DisableCompression
            },
            tags,
            cancellationToken);

        logger.LogDebug("Retrieved validation result for {CacheKey} (culture: {Culture})", 
            cacheKey, culture ?? "invariant");

        return result;
    }

    /// <summary>
    /// Clears cached validation result for a specific document and culture.
    /// </summary>
    public async Task ClearForDocumentCultureAsync(Guid documentId, string? culture, CancellationToken cancellationToken = default)
    {
        var tag = GetCultureTag(documentId, culture);
        await cache.RemoveByTagAsync(tag, cancellationToken);
        
        logger.LogDebug("Cleared cache by tag: {Tag} (culture: {Culture})", 
            tag, culture ?? "invariant");
    }

    /// <summary>
    /// Clears all cached validation results for a document (all cultures).
    /// </summary>
    public async Task ClearForDocumentAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var tag = GetDocumentTag(documentId);
        await cache.RemoveByTagAsync(tag, cancellationToken);
        
        logger.LogInformation("Cleared all validation cache for document {DocumentId}", documentId);
    }

    /// <summary>
    /// Clears all cached validation results.
    /// </summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await cache.RemoveByTagAsync(CacheKeyPrefix, cancellationToken);
        logger.LogInformation("Cleared all validation cache");
    }

    private static string GetCacheKey(Guid documentId, string? culture)
    {
        return $"{CacheKeyPrefix}_{documentId}_{culture ?? "invariant"}";
    }

    private static string[] GetCacheTags(Guid documentId, string? culture)
    {
        return
        [
            CacheKeyPrefix,                          // Global tag for all validation cache
            GetDocumentTag(documentId),             // Document-specific tag
            GetCultureTag(documentId, culture)      // Document + culture specific tag
        ];
    }

    private static string GetDocumentTag(Guid documentId) 
        => $"{CacheKeyPrefix}:doc:{documentId}";

    private static string GetCultureTag(Guid documentId, string? culture) 
        => $"{CacheKeyPrefix}:doc:{documentId}:culture:{culture ?? "invariant"}";
}