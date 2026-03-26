using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Interfaces;
using Umbraco.Community.CustomValidator.Models;
using Umbraco.Community.CustomValidator.Services;
using Umbraco.Community.CustomValidator.Validation;

namespace Umbraco.Community.CustomValidator.Tests.Services;

[TestFixture]
public sealed class CustomValidationFlagStatusResolverTests
{
    private MemoryCache _memoryCache = null!;
    private CustomValidationStatusCache _statusCache = null!;
    private Mock<IUmbracoContextAccessor> _umbracoContextAccessorMock = null!;
    private Mock<IOptions<CustomValidatorOptions>> _optionsMock = null!;
    private CustomValidatorOptions _options = null!;
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void Setup()
    {
        _options = new CustomValidatorOptions
        {
            CacheExpirationMinutes = 30,
            TreatWarningsAsErrors = false,
            EntityFlagMode = ValidationFlagMode.Lazy
        };

        _optionsMock = new Mock<IOptions<CustomValidatorOptions>>();
        _optionsMock.Setup(x => x.Value).Returns(_options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        _serviceProvider = services.BuildServiceProvider();

        _memoryCache = new MemoryCache(new MemoryCacheOptions());

        _statusCache = new CustomValidationStatusCache(
            _memoryCache,
            _optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        _umbracoContextAccessorMock = new Mock<IUmbracoContextAccessor>();
    }

    [TearDown]
    public void TearDown()
    {
        _memoryCache.Dispose();
        _serviceProvider.Dispose();
    }

    #region None Mode Tests

    [Test]
    public async Task HasErrorsAsync_ModeNone_ReturnsFalse_Regardless()
    {
        _options.EntityFlagMode = ValidationFlagMode.None;
        _statusCache.SetStatus(Guid.NewGuid(), ValidationStatus.HasErrors);
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.HasErrors);

        var sut = CreateResolver(CreateValidationServiceWithErrorValidator());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasErrorsAsync_ModeNone_DoesNotAccessUmbracoContext()
    {
        _options.EntityFlagMode = ValidationFlagMode.None;
        var sut = CreateResolver(CreateValidationService());

        await sut.HasErrorsAsync(Guid.NewGuid(), null);

        _umbracoContextAccessorMock.Verify(
            x => x.TryGetUmbracoContext(out It.Ref<IUmbracoContext?>.IsAny),
            Times.Never);
    }

    #endregion

    #region Lazy Mode Tests

    [Test]
    public async Task HasErrorsAsync_ModeLazy_StatusHasErrors_ReturnsTrue()
    {
        _options.EntityFlagMode = ValidationFlagMode.Lazy;
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.HasErrors);

        var sut = CreateResolver(CreateValidationService());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasErrorsAsync_ModeLazy_StatusValid_ReturnsFalse()
    {
        _options.EntityFlagMode = ValidationFlagMode.Lazy;
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.Valid);

        var sut = CreateResolver(CreateValidationService());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasErrorsAsync_ModeLazy_StatusUnknown_ReturnsFalse()
    {
        _options.EntityFlagMode = ValidationFlagMode.Lazy;
        var documentId = Guid.NewGuid();
        // No status set — Unknown

        var sut = CreateResolver(CreateValidationService());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasErrorsAsync_ModeLazy_DoesNotRunValidation()
    {
        _options.EntityFlagMode = ValidationFlagMode.Lazy;

        var sut = CreateResolver(CreateValidationService());

        await sut.HasErrorsAsync(Guid.NewGuid(), null);

        _umbracoContextAccessorMock.Verify(
            x => x.TryGetUmbracoContext(out It.Ref<IUmbracoContext?>.IsAny),
            Times.Never);
    }

    [Test]
    public async Task HasErrorsAsync_ModeLazy_WithCulture_ReadsCorrectCultureEntry()
    {
        _options.EntityFlagMode = ValidationFlagMode.Lazy;
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.HasErrors, "en-US");
        _statusCache.SetStatus(documentId, ValidationStatus.Valid, "da-DK");

        var sut = CreateResolver(CreateValidationService());

        Assert.That(await sut.HasErrorsAsync(documentId, "en-US"), Is.True);
        Assert.That(await sut.HasErrorsAsync(documentId, "da-DK"), Is.False);
    }

    #endregion

    #region Eager Mode Tests

    [Test]
    public async Task HasErrorsAsync_ModeEager_StatusHasErrors_ReturnsTrueWithoutValidating()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.HasErrors);

        var sut = CreateResolver(CreateValidationService());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.True);
        _umbracoContextAccessorMock.Verify(
            x => x.TryGetUmbracoContext(out It.Ref<IUmbracoContext?>.IsAny),
            Times.Never);
    }

    [Test]
    public async Task HasErrorsAsync_ModeEager_StatusValid_ReturnsFalseWithoutValidating()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        var documentId = Guid.NewGuid();
        _statusCache.SetStatus(documentId, ValidationStatus.Valid);

        var sut = CreateResolver(CreateValidationService());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.False);
        _umbracoContextAccessorMock.Verify(
            x => x.TryGetUmbracoContext(out It.Ref<IUmbracoContext?>.IsAny),
            Times.Never);
    }

    [Test]
    public async Task HasErrorsAsync_ModeEager_StatusUnknown_RunsValidation_ReturnsTrue_WhenHasErrors()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        var documentId = Guid.NewGuid();
        var content = CreateMockContent(documentId);

        SetupUmbracoContext(content);

        var sut = CreateResolver(CreateValidationServiceWithErrorValidator());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasErrorsAsync_ModeEager_StatusUnknown_RunsValidation_ReturnsFalse_WhenNoErrors()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        var documentId = Guid.NewGuid();
        var content = CreateMockContent(documentId);

        SetupUmbracoContext(content);

        var sut = CreateResolver(CreateValidationServiceWithInfoValidator());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasErrorsAsync_ModeEager_StatusUnknown_SetsStatusCacheAfterValidation()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        var documentId = Guid.NewGuid();
        var content = CreateMockContent(documentId);

        SetupUmbracoContext(content);

        var sut = CreateResolver(CreateValidationServiceWithErrorValidator());

        await sut.HasErrorsAsync(documentId, null);

        // Status should now be cached — second call should not hit context
        _umbracoContextAccessorMock.Invocations.Clear();
        _options.EntityFlagMode = ValidationFlagMode.Lazy; // switch to lazy to isolate cache read
        var cachedResult = await sut.HasErrorsAsync(documentId, null);

        Assert.That(cachedResult, Is.True);
    }

    [Test]
    public async Task HasErrorsAsync_ModeEager_StatusUnknown_ContentNotFound_ReturnsFalse()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        var documentId = Guid.NewGuid();

        SetupUmbracoContext(null); // content not found

        var sut = CreateResolver(CreateValidationService());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasErrorsAsync_ModeEager_UmbracoContextThrows_ReturnsFalse()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        var documentId = Guid.NewGuid();
        // No status set — Unknown; context accessor throws

        var umbracoContext = (IUmbracoContext?)null;
        _umbracoContextAccessorMock
            .Setup(x => x.TryGetUmbracoContext(out umbracoContext))
            .Throws(new InvalidOperationException("Context unavailable"));

        var sut = CreateResolver(CreateValidationService());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task HasErrorsAsync_ModeEager_TreatWarningsAsErrors_True_WarningCausesTrue()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        _options.TreatWarningsAsErrors = true;
        var documentId = Guid.NewGuid();
        var content = CreateMockContent(documentId);

        SetupUmbracoContext(content);

        var sut = CreateResolver(CreateValidationServiceWithWarningValidator());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task HasErrorsAsync_ModeEager_TreatWarningsAsErrors_False_WarningCausesFalse()
    {
        _options.EntityFlagMode = ValidationFlagMode.Eager;
        _options.TreatWarningsAsErrors = false;
        var documentId = Guid.NewGuid();
        var content = CreateMockContent(documentId);

        SetupUmbracoContext(content);

        var sut = CreateResolver(CreateValidationServiceWithWarningValidator());

        var result = await sut.HasErrorsAsync(documentId, null);

        Assert.That(result, Is.False);
    }

    #endregion

    #region Helper Methods

    private CustomValidationFlagStatusResolver CreateResolver(CustomValidationService validationService)
    {
        return new CustomValidationFlagStatusResolver(
            _statusCache,
            validationService,
            _umbracoContextAccessorMock.Object,
            _optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationFlagStatusResolver>>());
    }

    private CustomValidationService CreateValidationService(params ValidatorMetadata[] metadata)
    {
        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup(metadata.ToList(), logger.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        foreach (var m in metadata)
            services.AddSingleton(m.ValidatorType);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var registry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            sp.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        var cacheService = new CustomValidationCacheService(
            sp.GetRequiredService<HybridCache>(),
            _optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationCacheService>>());

        var statusCache = new CustomValidationStatusCache(
            _memoryCache,
            _optionsMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        var variationContextMock = new Mock<IVariationContextAccessor>();
        var languageServiceMock = new Mock<ILanguageService>();
        languageServiceMock.Setup(x => x.GetDefaultIsoCodeAsync()).ReturnsAsync("en-US");

        return new CustomValidationService(
            registry,
            cacheService,
            statusCache,
            _optionsMock.Object,
            variationContextMock.Object,
            languageServiceMock.Object,
            sp.GetRequiredService<ILogger<CustomValidationService>>());
    }

    private CustomValidationService CreateValidationServiceWithErrorValidator() =>
        CreateValidationService(new ValidatorMetadata
        {
            ValidatorType = typeof(ErrorValidator),
            ContentType = typeof(IPublishedContent)
        });

    private CustomValidationService CreateValidationServiceWithWarningValidator() =>
        CreateValidationService(new ValidatorMetadata
        {
            ValidatorType = typeof(WarningValidator),
            ContentType = typeof(IPublishedContent)
        });

    private CustomValidationService CreateValidationServiceWithInfoValidator() =>
        CreateValidationService(new ValidatorMetadata
        {
            ValidatorType = typeof(InfoValidator),
            ContentType = typeof(IPublishedContent)
        });

    private void SetupUmbracoContext(IPublishedContent? content)
    {
        var contentCacheMock = new Mock<IPublishedContentCache>();
        contentCacheMock
            .Setup(x => x.GetById(It.IsAny<bool>(), It.IsAny<Guid>()))
            .Returns(content);

        var umbracoContextMock = new Mock<IUmbracoContext>();
        umbracoContextMock.Setup(x => x.Content).Returns(contentCacheMock.Object);

        var context = umbracoContextMock.Object;
        _umbracoContextAccessorMock
            .Setup(x => x.TryGetUmbracoContext(out context))
            .Returns(true);
    }

    private static IPublishedContent CreateMockContent(Guid key)
    {
        var contentTypeMock = new Mock<IPublishedContentType>();
        contentTypeMock.Setup(x => x.Alias).Returns("testPage");

        var contentMock = new Mock<IPublishedContent>();
        contentMock.Setup(x => x.Key).Returns(key);
        contentMock.Setup(x => x.ContentType).Returns(contentTypeMock.Object);

        return contentMock.Object;
    }

    #endregion

    #region Test Validators

    private class ErrorValidator : IDocumentValidator
    {
        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content) =>
            Task.FromResult<IEnumerable<ValidationMessage>>(
            [new ValidationMessage { Message = "Error", Severity = ValidationSeverity.Error }]);
    }

    private class WarningValidator : IDocumentValidator
    {
        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content) =>
            Task.FromResult<IEnumerable<ValidationMessage>>(
            [new ValidationMessage { Message = "Warning", Severity = ValidationSeverity.Warning }]);
    }

    private class InfoValidator : IDocumentValidator
    {
        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content) =>
            Task.FromResult<IEnumerable<ValidationMessage>>(
            [new ValidationMessage { Message = "Info", Severity = ValidationSeverity.Info }]);
    }

    #endregion
}
