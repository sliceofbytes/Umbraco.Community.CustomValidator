using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.CustomValidator.Enums;

namespace Umbraco.Community.CustomValidator.Services;

public sealed class CustomValidationStatusCache(
    IMemoryCache cache,
    IOptions<CustomValidatorOptions> options,
    ILogger<CustomValidationStatusCache> logger)
{
    private readonly ConcurrentDictionary<string, bool> _statusTracker = new();

    private const string CacheKeyPrefix = "customValidationStatus";

    /// <summary>
    /// Gets validation status for a single document.
    /// </summary>
    public ValidationStatus GetStatus(Guid documentId, string? culture = null)
    {
        var cacheKey = GetCacheKey(documentId, culture);

        if (cache.TryGetValue<ValidationStatus>(cacheKey, out var status))
        {
            logger.LogTrace("Status cache HIT for {DocumentId}: {Status}", documentId, status);
            return status;
        }

        logger.LogTrace("Status cache MISS for {DocumentId}", documentId);
        return ValidationStatus.Unknown;
    }

    /// <summary>
    /// Sets validation status for a document.
    /// </summary>
    public void SetStatus(Guid documentId, ValidationStatus status, string? culture = null)
    {
        var cacheKey = GetCacheKey(documentId, culture);
        var cacheExpirationMinutes = options.Value.CacheExpirationMinutes;

        if (cacheExpirationMinutes <= 0)
        {
            cache.Remove(cacheKey);
            _statusTracker.TryRemove(cacheKey, out _);

            logger.LogDebug(
                "Status cache disabled (CacheExpirationMinutes <= 0), skipping status cache set for {DocumentId}",
                documentId);

            return;
        }

        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(cacheExpirationMinutes))
            .SetPriority(CacheItemPriority.Low)
            .RegisterPostEvictionCallback((key, value, reason, state) =>
            {
                if (key is string cacheKeyString)
                {
                    _statusTracker.TryRemove(cacheKeyString, out _);
                }

                logger.LogTrace("Status cache evicted for {DocumentId}, reason: {Reason}",
                    documentId, reason);
            });

        cache.Set(cacheKey, status, cacheOptions);
        _statusTracker.TryAdd(cacheKey, true);

        logger.LogDebug("Validation status set for {DocumentId}: {Status}", documentId, status);
    }

    /// <summary>
    /// Sets status based on whether document has errors.
    /// </summary>
    public void SetStatus(Guid documentId, bool hasErrors, string? culture = null)
    {
        SetStatus(documentId, hasErrors ? ValidationStatus.HasErrors : ValidationStatus.Valid, culture);
    }

    /// <summary>
    /// Clears validation status for a document.
    /// </summary>
    public void ClearStatus(Guid documentId, string? culture = null)
    {
        var cacheKey = GetCacheKey(documentId, culture);
        cache.Remove(cacheKey);
        _statusTracker.TryRemove(cacheKey, out _);

        logger.LogDebug("Cleared validation status for {DocumentId}", documentId);
    }

    /// <summary>
    /// Clears validation status for all cultures for a document.
    /// </summary>
    public void ClearForDocument(Guid documentId)
    {
        var documentPrefix = GetDocumentPrefix(documentId);
        var keysToRemove = _statusTracker.Keys
            .Where(key => key.StartsWith(documentPrefix, StringComparison.Ordinal))
            .ToList();

        foreach (var key in keysToRemove)
        {
            cache.Remove(key);
            _statusTracker.TryRemove(key, out _);
        }

        logger.LogDebug("Cleared validation status for {DocumentId} across {Count} culture entries", documentId, keysToRemove.Count);
    }

    /// <summary>
    /// Clears validation status for all documents.
    /// </summary>
    public void ClearAll()
    {
        var allKeys = _statusTracker.Keys.ToList();

        foreach (var cacheKey in allKeys)
        {
            cache.Remove(cacheKey);
        }

        _statusTracker.Clear();

        logger.LogInformation("Cleared all validation status cache ({Count} entries)", allKeys.Count);
    }

    private static string GetCacheKey(Guid documentId, string? culture)
    {
        var normalizedCulture = string.IsNullOrWhiteSpace(culture) ? "invariant" : culture;
        return $"{GetDocumentPrefix(documentId)}_{normalizedCulture}";
    }

    private static string GetDocumentPrefix(Guid documentId) =>
        $"{CacheKeyPrefix}_{documentId}";
}