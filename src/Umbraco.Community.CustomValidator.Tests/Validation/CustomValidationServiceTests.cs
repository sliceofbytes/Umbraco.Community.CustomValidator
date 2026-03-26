using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.CustomValidator.Enums;
using Umbraco.Community.CustomValidator.Interfaces;
using Umbraco.Community.CustomValidator.Models;
using Umbraco.Community.CustomValidator.Services;
using Umbraco.Community.CustomValidator.Validation;

namespace Umbraco.Community.CustomValidator.Tests.Validation;

[TestFixture]
public sealed class CustomValidationServiceTests
{
    private CustomValidatorRegistry _validatorRegistry = null!;
    private CustomValidationCacheService _cacheService = null!;
    private CustomValidationStatusCache _statusCache = null!;
    private Mock<IVariationContextAccessor> _variationContextAccessorMock = null!;
    private Mock<ILanguageService> _languageServiceMock = null!;
    private Mock<ILogger<CustomValidationService>> _loggerMock = null!;
    private Mock<IOptions<CustomValidatorOptions>> _optionsMock = null!;
    private CustomValidationService _sut = null!;

    private ServiceProvider _serviceProvider = null!;
    private HybridCache _hybridCache = null!;

    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddMemoryCache();

        _serviceProvider = services.BuildServiceProvider();
        _hybridCache = _serviceProvider.GetRequiredService<HybridCache>();

        var options = new CustomValidatorOptions
        {
            CacheExpirationMinutes = 30,
            TreatWarningsAsErrors = false
        };

        _optionsMock = new Mock<IOptions<CustomValidatorOptions>>();
        _optionsMock.Setup(x => x.Value).Returns(options);

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup([], logger.Object);

        // Create registry with empty metadata (no validators by default)
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _validatorRegistry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            _serviceProvider.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        _cacheService = new CustomValidationCacheService(
            _hybridCache,
            _optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationCacheService>>());

        _variationContextAccessorMock = new Mock<IVariationContextAccessor>();
        _languageServiceMock = new Mock<ILanguageService>();
        _loggerMock = new Mock<ILogger<CustomValidationService>>();

        _statusCache = new CustomValidationStatusCache(
            _serviceProvider.GetRequiredService<IMemoryCache>(),
            _optionsMock.Object,
            _serviceProvider.GetRequiredService<ILogger<CustomValidationStatusCache>>());

        _sut = new CustomValidationService(
            _validatorRegistry,
            _cacheService,
            _statusCache,
            _optionsMock.Object,
            _variationContextAccessorMock.Object,
            _languageServiceMock.Object,
            _loggerMock.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
    }

    #region ExecuteValidationAsync - No Validator Tests

    [Test]
    public async Task ExecuteValidationAsync_WithNoValidator_ReturnsNoValidatorResponse()
    {
        // Arrange
        var content = CreateMockContent();

        // Act
        var result = await _sut.ExecuteValidationAsync(content, null);

        // Assert
        Assert.That(result.HasValidator, Is.False);
        Assert.That(result.Messages, Is.Empty);
    }

    #endregion

    #region ExecuteValidationAsync - With Validator Tests

    [Test]
    public async Task ExecuteValidationAsync_WithValidator_ReturnsValidationResult()
    {
        // Arrange
        var content = CreateMockContent();
        var sut = CreateServiceWithValidator();

        // Act
        var result = await sut.ExecuteValidationAsync(content, "en-US");

        // Assert
        Assert.That(result.HasValidator, Is.True);
        Assert.That(result.Messages?.Count(), Is.EqualTo(1));
        Assert.That(result.Messages.First().Message, Is.EqualTo("Test validation"));
    }

    [Test]
    public async Task ExecuteValidationAsync_CalledTwice_UsesCacheOnSecondCall()
    {
        // Arrange
        var content = CreateMockContent();
        var callCount = 0;
        var sut = CreateServiceWithCountingValidator(() => callCount++);

        // Act
        await sut.ExecuteValidationAsync(content, "en-US");
        await sut.ExecuteValidationAsync(content, "en-US");

        // Assert
        Assert.That(callCount, Is.EqualTo(1), "Validator should only be called once due to caching");
    }

    #endregion

    #region Culture Handling Tests

    [Test]
    public async Task ExecuteValidationAsync_WithCulture_SetsVariationContext()
    {
        // Arrange
        var content = CreateMockContent();
        var culture = "en-US";
        var sut = CreateServiceWithValidator();

        // Act
        await sut.ExecuteValidationAsync(content, culture);

        // Assert
        _variationContextAccessorMock.VerifySet(
            x => x.VariationContext = It.Is<VariationContext>(v => v.Culture == culture),
            Times.Once);
    }

    [Test]
    public async Task ExecuteValidationAsync_WithExplicitCulture_DoesNotCallDefaultLanguage()
    {
        // Arrange
        var content = CreateMockContent();
        var sut = CreateServiceWithValidator();

        // Act
        await sut.ExecuteValidationAsync(content, "da-DK");

        // Assert
        _languageServiceMock.Verify(
            x => x.GetDefaultIsoCodeAsync(),
            Times.Never);
    }

    [Test]
    public async Task ExecuteValidationAsync_DifferentCultures_CachesSeparately()
    {
        // Arrange
        var content = CreateMockContent();
        var totalCallCount = 0;
        var sut = CreateServiceWithCountingValidator(() => totalCallCount++);

        // Act - Validate different cultures twice each
        await sut.ExecuteValidationAsync(content, "en-US");
        await sut.ExecuteValidationAsync(content, "da-DK");
        await sut.ExecuteValidationAsync(content, "en-US");
        await sut.ExecuteValidationAsync(content, "da-DK");

        // Assert - Should be called twice total (once per culture)
        Assert.That(totalCallCount, Is.EqualTo(2),
            "Validator should be called once per culture, proving separate cache keys");
    }

    #endregion

    #region Helper Methods

    private static IPublishedContent CreateMockContent()
    {
        var contentTypeMock = new Mock<IPublishedContentType>();
        contentTypeMock.Setup(x => x.Alias).Returns("testPage");
        contentTypeMock.Setup(x => x.ItemType).Returns(PublishedItemType.Content);

        var contentMock = new Mock<IPublishedContent>();
        contentMock.Setup(x => x.Id).Returns(1);
        contentMock.Setup(x => x.Key).Returns(Guid.NewGuid());
        contentMock.Setup(x => x.Name).Returns("Test Page");
        contentMock.Setup(x => x.ContentType).Returns(contentTypeMock.Object);

        return contentMock.Object;
    }

    private CustomValidationService CreateServiceWithValidator()
    {
        var metadata = new List<ValidatorMetadata>
        {
            new() { ValidatorType = typeof(TestValidator), ContentType = typeof(IPublishedContent) }
        };

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup(metadata, logger.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TestValidator>();

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();



        var registry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            sp.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        return new CustomValidationService(
            registry,
            _cacheService,
            _statusCache,
            _optionsMock.Object,
            _variationContextAccessorMock.Object,
            _languageServiceMock.Object,
            _loggerMock.Object);
    }

    private CustomValidationService CreateServiceWithCountingValidator(Action onValidate)
    {
        var metadata = new List<ValidatorMetadata>
        {
            new() { ValidatorType = typeof(CallCountingValidator), ContentType = typeof(IPublishedContent) }
        };

        var logger = new Mock<ILogger<ValidatorLookup>>();
        var lookup = new ValidatorLookup(metadata, logger.Object);

        var services = new ServiceCollection();
        services.AddLogging();
        var validator = new CallCountingValidator(onValidate);
        services.AddSingleton(validator);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var registry = new CustomValidatorRegistry(
            scopeFactory,
            lookup,
            sp.GetRequiredService<ILogger<CustomValidatorRegistry>>());

        return new CustomValidationService(
            registry,
            _cacheService,
            _statusCache,
            _optionsMock.Object,
            _variationContextAccessorMock.Object,
            _languageServiceMock.Object,
            _loggerMock.Object);
    }

    #endregion

    #region Test Validators

    private class TestValidator : IDocumentValidator
    {
        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content)
        {
            return Task.FromResult<IEnumerable<ValidationMessage>>(new List<ValidationMessage>
            {
                new() { Message = "Test validation", Severity = ValidationSeverity.Info }
            });
        }
    }

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

    private class CallCountingValidator : IDocumentValidator
    {
        private readonly Action _onValidate;

        public CallCountingValidator(Action onValidate)
        {
            _onValidate = onValidate;
        }

        public Task<IEnumerable<ValidationMessage>> ValidateAsync(IPublishedContent content)
        {
            _onValidate();
            return Task.FromResult<IEnumerable<ValidationMessage>>(new List<ValidationMessage>());
        }
    }

    #endregion
}