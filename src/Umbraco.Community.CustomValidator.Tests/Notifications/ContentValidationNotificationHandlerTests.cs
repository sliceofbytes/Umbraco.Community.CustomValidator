using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Interfaces;
using Umbraco.Community.CustomValidator.Models;
using Umbraco.Community.CustomValidator.Notifications;
using Umbraco.Community.CustomValidator.Services;
using Umbraco.Community.CustomValidator.Validation;

namespace Umbraco.Community.CustomValidator.Tests.Notifications;

[TestFixture]
public sealed class ContentValidationNotificationHandlerTests
{
    private Mock<IUmbracoContextAccessor> _umbracoContextAccessorMock = null!;
    private CustomValidationCacheService _validationCacheService = null!;
    private CustomValidationStatusCache _statusCache = null!;
    private CustomValidationService _validationService = null!;
    private Mock<IOptions<CustomValidatorOptions>> _optionsMock = null!;
    private Mock<ILogger<ContentValidationNotificationHandler>> _loggerMock = null!;
    private ContentValidationNotificationHandler _sut = null!;

    private ServiceProvider _serviceProvider = null!;
    private CustomValidatorOptions _options = null!;

    [SetUp]
    public void Setup()
    {
        _options = new CustomValidatorOptions
        {
            CacheExpirationMinutes = 30,
            TreatWarningsAsErrors = false
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddMemoryCache();

        _serviceProvider = services.BuildServiceProvider();

        var hybridCache = _serviceProvider.GetRequiredService<HybridCache>();

        _optionsMock = new Mock<IOptions<CustomValidatorOptions>>();
        _optionsMock.Setup(x => x.Value).Returns(_options);

        var logger = new Mock<ILogger<ValidatorLookup>>();

        var lookup = new ValidatorLookup(new List<ValidatorMetadata>(), logger.Object);

        // Create registry with empty metadata (no validators)
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var validatorRegistry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            _serviceProvider.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        _validationCacheService = new CustomValidationCacheService(
            hybridCache,
            _optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationCacheService>>());

        var variationContextMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();
        languageServiceMock.Setup(x => x.GetDefaultIsoCodeAsync())
            .ReturnsAsync("en-GB");

        _statusCache = new CustomValidationStatusCache(
            _serviceProvider.GetRequiredService<IMemoryCache>(),
            _optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        _validationService = new CustomValidationService(
            validatorRegistry,
            _validationCacheService,
            _statusCache,
            _optionsMock.Object,
            variationContextMock.Object,
            languageServiceMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationService>>());

        _umbracoContextAccessorMock = new Mock<IUmbracoContextAccessor>();
        _loggerMock = new Mock<ILogger<ContentValidationNotificationHandler>>();

        _sut = new ContentValidationNotificationHandler(
            _umbracoContextAccessorMock.Object,
            _validationCacheService,
            _statusCache,
            _validationService,
            _optionsMock.Object,
            _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
    }

    #region ContentSavingNotification Tests

    [Test]
    public async Task HandleAsync_ContentSaving_ClearsCacheForDocument()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var entity = CreateMockContent(documentId, "Test Page");

        // Pre-populate cache
        await _validationCacheService.GetOrSetAsync(
            documentId,
            null, (ct) => ValueTask.FromResult(CreateValidationResponse(documentId)),
            CancellationToken.None);

        var notification = new ContentSavingNotification(entity, new EventMessages());

        // Act
        await _sut.HandleAsync(notification, CancellationToken.None);

        // Assert - Verify cache was cleared
        var factoryCalled = false;
        await _validationCacheService.GetOrSetAsync(
            documentId,
            null,
            async (ct) =>
            {
                factoryCalled = true;
                return CreateValidationResponse(documentId);
            },
            CancellationToken.None);

        Assert.That(factoryCalled, Is.True, "Cache should be cleared, factory should be called");
    }

    #endregion

    #region ContentPublishingNotification Tests - No Validators

    [Test]
    public async Task HandleAsync_ContentPublishing_NoValidators_DoesNotCancelPublish()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var entity = CreateMockVariantContent(documentId, "Test Page", "en-US");
        var content = CreateMockPublishedContent(documentId);

        SetupUmbracoContext(content);

        var notification = new ContentPublishingNotification(entity, new EventMessages());

        // Act
        await _sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        Assert.That(notification.Messages.GetAll(), Is.Empty);
    }

    [Test]
    public async Task HandleAsync_ContentPublishing_ContentNotFound_SkipsValidation()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var entity = CreateMockContent(documentId, "Test Page");

        SetupUmbracoContext(null); // Content not found

        var notification = new ContentPublishingNotification(entity, new EventMessages());

        // Act
        await _sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        Assert.That(notification.Messages.GetAll(), Is.Empty);
    }

    #endregion

    #region ContentPublishingNotification Tests - With Validators

    [Test]
    public async Task HandleAsync_ContentPublishing_WithErrors_CancelsPublish()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var entity = CreateMockVariantContent(documentId, "Test Page", "en-US");
        var content = CreateMockPublishedContent(documentId);

        var validationService = CreateValidationServiceWithErrorValidator();
        var sut = new ContentValidationNotificationHandler(
            _umbracoContextAccessorMock.Object,
            _validationCacheService,
            _statusCache,
            validationService,
            _optionsMock.Object,
            _loggerMock.Object);

        SetupUmbracoContext(content);

        var notification = new ContentPublishingNotification(entity, new EventMessages());

        // Act
        await sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        var errorMessages = notification.Messages.GetAll().ToList();
        Assert.That(errorMessages, Has.Count.EqualTo(1));
        Assert.That(errorMessages.First().MessageType, Is.EqualTo(EventMessageType.Error));
        Assert.That(errorMessages.First().Category, Does.Contain("Custom Validation Failed"));
    }

    [Test]
    public async Task HandleAsync_ContentPublishing_WithWarningsOnly_DoesNotCancelPublish()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var entity = CreateMockVariantContent(documentId, "Test Page", "en-US");
        var content = CreateMockPublishedContent(documentId);

        var validationService = CreateValidationServiceWithWarningValidator();
        var sut = new ContentValidationNotificationHandler(
            _umbracoContextAccessorMock.Object,
            _validationCacheService,
            _statusCache,
            validationService,
            _optionsMock.Object,
            _loggerMock.Object);

        SetupUmbracoContext(content);

        var notification = new ContentPublishingNotification(entity, new EventMessages());

        // Act
        await sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        var messages = notification.Messages.GetAll().ToList();
        Assert.That(messages.All(m => m.MessageType != EventMessageType.Error), Is.True);
    }

    [Test]
    public async Task HandleAsync_ContentPublishing_EarlyExitOnFirstError()
    {
        // Arrange
        var doc1 = Guid.NewGuid();
        var doc2 = Guid.NewGuid();
        var entity1 = CreateMockVariantContent(doc1, "Doc 1", "en-US");
        var entity2 = CreateMockVariantContent(doc2, "Doc 2", "en-US");
        var content1 = CreateMockPublishedContent(doc1);
        var content2 = CreateMockPublishedContent(doc2);

        var doc2Validated = false;
        var validationService = CreateValidationServiceWithConditionalError(doc1, () => doc2Validated = true);

        var sut = new ContentValidationNotificationHandler(
            _umbracoContextAccessorMock.Object,
            _validationCacheService,
            _statusCache,
            validationService,
            _optionsMock.Object,
            _loggerMock.Object);

        SetupUmbracoContextForMultipleDocuments(new[] { (doc1, content1), (doc2, content2) });

        var notification = new ContentPublishingNotification(
            new[] { entity1, entity2 },
            new EventMessages());

        // Act
        await sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        Assert.That(doc2Validated, Is.False, "Should exit after first error");

        var messages = notification.Messages.GetAll().ToList();
        Assert.That(messages, Has.Count.EqualTo(1));
    }

    #endregion

    #region TreatWarningsAsErrors Tests

    [Test]
    public async Task HandleAsync_TreatWarningsAsErrors_True_CancelsOnWarnings()
    {
        // Arrange
        _options.TreatWarningsAsErrors = true;

        var documentId = Guid.NewGuid();
        var entity = CreateMockVariantContent(documentId, "Test Page", "en-US");
        var content = CreateMockPublishedContent(documentId);

        var validationService = CreateValidationServiceWithWarningValidator();
        var sut = new ContentValidationNotificationHandler(
            _umbracoContextAccessorMock.Object,
            _validationCacheService,
            _statusCache,
            validationService,
            _optionsMock.Object,
            _loggerMock.Object);

        SetupUmbracoContext(content);

        var notification = new ContentPublishingNotification(entity, new EventMessages());

        // Act
        await sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        var messages = notification.Messages.GetAll().ToList();
        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(messages.First().MessageType, Is.EqualTo(EventMessageType.Error));
    }

    [Test]
    public async Task HandleAsync_TreatWarningsAsErrors_False_AllowsPublishWithWarnings()
    {
        // Arrange
        _options.TreatWarningsAsErrors = false;

        var documentId = Guid.NewGuid();
        var entity = CreateMockVariantContent(documentId, "Test Page", "en-US");
        var content = CreateMockPublishedContent(documentId);

        var validationService = CreateValidationServiceWithWarningValidator();
        var sut = new ContentValidationNotificationHandler(
            _umbracoContextAccessorMock.Object,
            _validationCacheService,
            _statusCache,
            validationService,
            _optionsMock.Object,
            _loggerMock.Object);

        SetupUmbracoContext(content);

        var notification = new ContentPublishingNotification(entity, new EventMessages());

        // Act
        await sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        var messages = notification.Messages.GetAll().ToList();
        Assert.That(messages.All(m => m.MessageType != EventMessageType.Error), Is.True);
    }

    #endregion

    #region Exception Handling Tests

    [Test]
    public async Task HandleAsync_ContentPublishing_ExceptionThrown_CancelsPublish()
    {
        // Arrange
        var context = It.IsAny<IUmbracoContext>();
        _umbracoContextAccessorMock.Setup(x => x.TryGetUmbracoContext(out context))
            .Throws(new InvalidOperationException("Test exception"));

        var entity = CreateMockContent(Guid.NewGuid(), "Test");
        var notification = new ContentPublishingNotification(entity, new EventMessages());

        // Act
        await _sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        var messages = notification.Messages.GetAll().ToList();
        Assert.That(messages, Has.Count.EqualTo(1));
        Assert.That(messages.First().MessageType, Is.EqualTo(EventMessageType.Error));
        Assert.That(messages.First().Category, Does.Contain("Custom Validation Failed"));
    }

    [Test]
    public async Task HandleAsync_ContentPublishing_ExceptionThrown_LogsError()
    {
        // Arrange
        var exception = new InvalidOperationException("Test exception");
        var context = It.IsAny<IUmbracoContext>();
        _umbracoContextAccessorMock.Setup(x => x.TryGetUmbracoContext(out context))
            .Throws(exception);

        var entity = CreateMockContent(Guid.NewGuid(), "Test");
        var notification = new ContentPublishingNotification(entity, new EventMessages());

        // Act
        await _sut.HandleAsync(notification, CancellationToken.None);

        // Assert
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unexpected error")),
                It.Is<Exception>(ex => ex == exception),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Helper Methods

    private void SetupUmbracoContext(IPublishedContent? content)
    {
        var umbracoContextMock = new Mock<IUmbracoContext>();
        var contentCacheMock = new Mock<IPublishedContentCache>();

        contentCacheMock.Setup(x => x.GetById(true, It.IsAny<Guid>())).Returns(content);
        contentCacheMock.Setup(x => x.GetById(It.IsAny<bool>(), It.IsAny<Guid>())).Returns(content);

        umbracoContextMock.Setup(x => x.Content).Returns(contentCacheMock.Object);

        var context = umbracoContextMock.Object;
        _umbracoContextAccessorMock.Setup(x => x.TryGetUmbracoContext(out context))
            .Returns(true);
    }

    private void SetupUmbracoContextForMultipleDocuments(IEnumerable<(Guid key, IPublishedContent content)> documents)
    {
        var umbracoContextMock = new Mock<IUmbracoContext>();
        var contentCacheMock = new Mock<IPublishedContentCache>();

        foreach (var (key, content) in documents)
        {
            contentCacheMock.Setup(x => x.GetById(true, key)).Returns(content);
        }

        umbracoContextMock.Setup(x => x.Content).Returns(contentCacheMock.Object);

        var context = umbracoContextMock.Object;
        _umbracoContextAccessorMock.Setup(x => x.TryGetUmbracoContext(out context))
            .Returns(true);
    }

    private static IPublishedContent CreateMockPublishedContent(Guid key)
    {
        var contentTypeMock = new Mock<IPublishedContentType>();
        contentTypeMock.Setup(x => x.Alias).Returns("testPage");
        contentTypeMock.Setup(x => x.ItemType).Returns(PublishedItemType.Content);

        var contentMock = new Mock<IPublishedContent>();
        contentMock.Setup(x => x.Id).Returns(1);
        contentMock.Setup(x => x.Key).Returns(key);
        contentMock.Setup(x => x.Name).Returns("Test Page");
        contentMock.Setup(x => x.ContentType).Returns(contentTypeMock.Object);

        return contentMock.Object;
    }

    private static IContent CreateMockContent(Guid key, string name)
    {
        var contentMock = new Mock<IContent>();
        contentMock.Setup(x => x.Key).Returns(key);
        contentMock.Setup(x => x.Id).Returns(1);
        contentMock.Setup(x => x.Name).Returns(name);
        contentMock.Setup(x => x.AvailableCultures).Returns(Enumerable.Empty<string>());

        return contentMock.Object;
    }

    private static IContent CreateMockVariantContent(Guid key, string name, params string[] cultures)
    {
        var contentMock = new Mock<IContent>();
        contentMock.Setup(x => x.Key).Returns(key);
        contentMock.Setup(x => x.Id).Returns(1);
        contentMock.Setup(x => x.Name).Returns(name);
        contentMock.Setup(x => x.AvailableCultures).Returns(cultures);

        var cultureCollection = new ContentCultureInfosCollection();

        foreach (var culture in cultures)
        {
            var mockCultureInfo = new Mock<ContentCultureInfos>(culture);
            mockCultureInfo.Setup(s => s.IsDirty()).Returns(true);
            cultureCollection.Add(mockCultureInfo.Object);
        }

        contentMock.Setup(x => x.PublishCultureInfos).Returns(cultureCollection);

        return contentMock.Object;
    }

    private CustomValidationService CreateValidationServiceWithErrorValidator()
    {
        var metadata = new List<ValidatorMetadata>
        {
            new() { ValidatorType = typeof(ErrorValidator), ContentType = typeof(IPublishedContent) }
        };

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup(metadata, logger.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddMemoryCache();
        services.AddSingleton<ErrorValidator>();

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var validatorRegistry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            sp.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        var cacheService = new CustomValidationCacheService(
            sp.GetRequiredService<HybridCache>(),
            _optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationCacheService>>());

        var variationContextMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();

        var statusCacheError = new CustomValidationStatusCache(
            sp.GetRequiredService<IMemoryCache>(),
            _optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        return new CustomValidationService(
            validatorRegistry,
            cacheService,
            statusCacheError,
            _optionsMock.Object,
            variationContextMock.Object,
            languageServiceMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationService>>());
    }

    private CustomValidationService CreateValidationServiceWithWarningValidator()
    {
        var metadata = new List<ValidatorMetadata>
        {
            new() { ValidatorType = typeof(WarningValidator), ContentType = typeof(IPublishedContent) }
        };

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup(metadata, logger.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddMemoryCache();
        services.AddSingleton<WarningValidator>();

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var validatorRegistry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            sp.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        var cacheService = new CustomValidationCacheService(
            sp.GetRequiredService<HybridCache>(),
            _optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationCacheService>>());

        var variationContextMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();

        var statusCacheWarning = new CustomValidationStatusCache(
            sp.GetRequiredService<IMemoryCache>(),
            _optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        return new CustomValidationService(
            validatorRegistry,
            cacheService,
            statusCacheWarning,
            _optionsMock.Object,
            variationContextMock.Object,
            languageServiceMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationService>>());
    }

    private CustomValidationService CreateValidationServiceWithConditionalError(Guid errorDocId, Action onDoc2Validate)
    {
        var metadata = new List<ValidatorMetadata>
        {
            new() { ValidatorType = typeof(ConditionalErrorValidator), ContentType = typeof(IPublishedContent) }
        };

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup(metadata, logger.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddMemoryCache();

        var validator = new ConditionalErrorValidator(errorDocId, onDoc2Validate);
        services.AddSingleton(validator);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var validatorRegistry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            sp.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        var cacheService = new CustomValidationCacheService(
            sp.GetRequiredService<HybridCache>(),
            _optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationCacheService>>());

        var variationContextMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();

        var statusCacheConditional = new CustomValidationStatusCache(
            sp.GetRequiredService<IMemoryCache>(),
            _optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        return new CustomValidationService(
            validatorRegistry,
            cacheService,
            statusCacheConditional,
            _optionsMock.Object,
            variationContextMock.Object,
            languageServiceMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationService>>());
    }

    private static ValidationResponse CreateValidationResponse(Guid documentId, params ValidationMessage[] messages)
    {
        return new ValidationResponse
        {
            ContentId = documentId,
            HasValidator = true,
            Messages = messages.ToList()
        };
    }

    #endregion

    #region Test Validators

    private class ErrorValidator : IDocumentValidator
    {
        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content)
        {
            return Task.FromResult<IEnumerable<ValidationMessage>>(new List<ValidationMessage>
            {
                new() { Message = "Validation error", Severity = ValidationSeverity.Error }
            });
        }
    }

    private class WarningValidator : IDocumentValidator
    {
        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content)
        {
            return Task.FromResult<IEnumerable<ValidationMessage>>(new List<ValidationMessage>
            {
                new() { Message = "Validation warning", Severity = ValidationSeverity.Warning }
            });
        }
    }

    private class ConditionalErrorValidator : IDocumentValidator
    {
        private readonly Guid _errorDocId;
        private readonly Action _onDoc2Validate;

        public ConditionalErrorValidator(Guid errorDocId, Action onDoc2Validate)
        {
            _errorDocId = errorDocId;
            _onDoc2Validate = onDoc2Validate;
        }

        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content)
        {
            if (content.Key == _errorDocId)
            {
                return Task.FromResult<IEnumerable<ValidationMessage>>(new List<ValidationMessage>
                {
                    new() { Message = "Error", Severity = ValidationSeverity.Error }
                });
            }

            _onDoc2Validate();
            return Task.FromResult<IEnumerable<ValidationMessage>>(new List<ValidationMessage>());
        }
    }

    #endregion
}